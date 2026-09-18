using System.Collections.Generic;
using System.Linq;
using Unity.Netcode;
using UnityEngine;

/// <summary>
/// Old Maid (GDD §3.6) -- the first REAL designed mini-game through IMiniGame,
/// alongside NumberPickMiniGame's throwaway role as "what proves the framework
/// works." Classic draw-from-neighbor: deal a deck containing exactly one
/// unpaired "old maid" card; players take turns drawing one unseen card from
/// the next active player in turn order (which IS "the neighbor" -- turn order
/// and seating order are the same circle in this game, and the circle naturally
/// closes as players empty their hands and drop out); matched pairs discard
/// immediately; whoever is left holding the lone unmatched card when only one
/// player remains in the circle loses. Single-loser game (GDD §3.1), per the
/// v7 slice notes' explicit recommendation to prove the single-loser/solo-chase
/// case before attempting Poisoned Glass's multi-loser cull.
///
/// Debug-tier fidelity, matching NumberPickMiniGame's investment level: no
/// hover-highlight draw UI (§3.6's UI spec) and no expression-system tie-in --
/// those are real future work, not proven here. What IS real and new versus
/// NumberPickMiniGame: actual hidden-information turn logic over the network.
/// Each client only ever learns the contents of their OWN hand (sent via a
/// per-client targeted ClientRpc); everyone else's hand is visible only as a
/// card COUNT, broadcast to everyone, same as what you'd see watching the
/// table without being able to read anyone's cards.
///
/// Cannot be meaningfully tested solo -- you can't draw a card from yourself.
/// With one active player Setup() will immediately resolve them as the loser
/// (correct: they hold the only card in existence with nobody to pass it to),
/// but no actual draw/pair logic ever runs. This needs a second tester before
/// it proves anything, unlike NumberPickMiniGame which at least ran end-to-end
/// solo with one payout branch unreachable.
/// </summary>
[RequireComponent(typeof(NetworkObject))]
public class OldMaidMiniGame : NetworkBehaviour, IMiniGame
{
    [Tooltip("Distinct ranks in the deck -- Ace through King = 13 in a real deck. Pairing is by rank, not by the exact copy: any two cards sharing a rank are a valid pair. Tune lower for faster debug testing.")]
    [SerializeField] private int rankCount = 13;

    [Tooltip("Copies of each rank -- one per suit = 4 in a real deck. Total deck size, fixed regardless of player count, is rankCount * copiesPerRank + 1 joker (13*4+1 = 53 for a real deck) -- not 'X pairs per player.' Fewer players just means bigger hands, same as dealing a real deck. A future mini-game registry/picker (GDD §3.1) should refuse to pick Old Maid below some player-count floor instead of handing 2 players a 53-card deck -- not implemented yet.")]
    [SerializeField] private int copiesPerRank = 4;

    [Tooltip("If the player whose turn it is doesn't act within this many seconds, the server auto-draws on their behalf so an AFK/disconnected player can't stall the match -- same anti-stall reasoning as NumberPickMiniGame's timeoutSeconds.")]
    [SerializeField] private float turnTimeoutSeconds = 20f;

    [Tooltip("In-match Points cost to peek one card you point at in the neighbor's hand, on your turn (once per turn). Spends PlayerState.Points, which is also your score, so it trades winnings for information. Tune freely.")]
    [SerializeField] private int peekCost = 15;

    private List<PlayerState> _players;

    // Fixed for the whole game once Setup runs -- this IS the seating/turn
    // order/"neighbor" relationship. Never mutated; players who empty their
    // hand stay in this list but get removed from _stillIn instead, which is
    // what keeps "next active player after X" well-defined without having to
    // re-index anything as people drop out.
    private List<ulong> _seatingOrder;
    private HashSet<ulong> _stillIn;

    // Server-authoritative hands. Deliberately never put on a NetworkVariable
    // -- that would broadcast every hand to every client. See
    // BroadcastAllHandsAndCounts for how each client actually learns anything.
    private Dictionary<ulong, List<int>> _hands;

    private bool _complete;
    private ulong? _loserClientId;
    private float _turnElapsed;

    // Server-only: whether the current drawer has already used their one peek
    // this turn. Reset on setup and whenever the turn advances.
    private bool _peekedThisTurn;

    // Public (everyone can read): whose turn it is, and whether the round's
    // over. Card counts and "my own hand" are NOT NetworkVariables -- see the
    // two ClientRpcs below.
    private readonly NetworkVariable<ulong> _currentTurnClientId = new NetworkVariable<ulong>(
        0,
        NetworkVariableReadPermission.Everyone,
        NetworkVariableWritePermission.Server);

    // Whose hand is currently being offered -- the player _currentTurnClientId
    // draws from. Public for the same reason _currentTurnClientId is: in a
    // real game, everyone at the table can see whose hand is fanned out right
    // now, even though nobody but the drawer gets to pick from it.
    private readonly NetworkVariable<ulong> _currentNeighborClientId = new NetworkVariable<ulong>(
        0,
        NetworkVariableReadPermission.Everyone,
        NetworkVariableWritePermission.Server);

    private readonly NetworkVariable<bool> _completeSynced = new NetworkVariable<bool>(
        false,
        NetworkVariableReadPermission.Everyone,
        NetworkVariableWritePermission.Server);

    // Which card in the neighbor's fan the current drawer is hovering over,
    // or -1 for none. Public (ReadPermission.Everyone) on purpose: this is
    // the ONE thing in Old Maid that's supposed to be readable by the whole
    // table, not just the drawer (GDD 3.6 -- the hover IS the bluff-read
    // tension). Only the server writes it, fed by SetHoverServerRpc below
    // which trusts only the current turn player.
    private readonly NetworkVariable<int> _hoveredCardIndex = new NetworkVariable<int>(
        -1,
        NetworkVariableReadPermission.Everyone,
        NetworkVariableWritePermission.Server);

    // Local-only client-side mirrors, populated purely by the two ClientRpcs
    // below -- never trusted for server logic, only used to draw this
    // client's own debug OnGUI.
    private List<int> _myHand = new List<int>();
    private readonly Dictionary<ulong, int> _handCounts = new Dictionary<ulong, int>();

    // Local-only: the last hover index THIS client pushed to the server. Used
    // purely to send SetHoverServerRpc only when the hovered card actually
    // changes, instead of once per frame while the cursor sits still.
    private int _lastSentHover = -1;

    // Local-only: the turn we last saw, so the client can clear its private peek
    // reveals the moment the turn changes (your paid intel doesn't carry over).
    private ulong _lastKnownTurn = ulong.MaxValue;

    private bool _cursorFreedLocally;

    // Client-side 3D card presentation (Phase 1). Pure rendering of the synced
    // state below -- created per client when this mini-game spawns, torn down
    // when it despawns. Null on a headless server with no camera; everything
    // that touches it null-guards.
    private OldMaidCardView _cardView;

    // ---- Client-side card presentation lifecycle (Phase 1) ----
    //
    // Runs on every client (host included). The view is pure presentation; the
    // server's authoritative state is untouched. Created on spawn, fed by the
    // two ClientRpc handlers, repositioned every frame (avatars/camera move in
    // first-person), and destroyed on despawn -- which MatchController triggers
    // at GameResolve, so a finished hand cleans itself up.
    public override void OnNetworkSpawn()
    {
        _cardView = new OldMaidCardView(new ProceduralFanSlotProvider());
    }

    public override void OnNetworkDespawn()
    {
        _cardView?.Clear();
        _cardView = null;
    }

    private void OnDisable()
    {
        // CURSOR BACKSTOP. Free/relock is normally driven by OnGUI's _cursorFreedLocally transition
        // detection -- but that needs a frame where OnGUI runs with needCursor false. If the game ENDS
        // ON THE LOCAL PLAYER'S TURN (e.g. the last player is forced out on the final card), the phase
        // transition deactivates this minigame before that relock frame runs, so the cursor is left
        // None/visible. The player then drops into the dungeon unable to look or turn, because both
        // PlayerCamera and PlayerMovement gate on Cursor.lockState == Locked -- which reads as "the
        // card-snap stuck in the dungeon" but is really just an un-relocked cursor.
        //
        // OnDisable fires on both deactivate AND destroy, so it catches the teardown however it happens.
        // No-op unless WE freed the cursor, so non-turn players are unaffected.
        if (_cursorFreedLocally)
        {
            Cursor.lockState = CursorLockMode.Locked;
            Cursor.visible = false;
            _cursorFreedLocally = false;
        }
    }

    // Unity Update (distinct from IMiniGame.Tick, which is the server's
    // per-frame logic call). Once the round is over the cards freeze in place
    // until despawn clears them -- no need to keep laying them out.
    private void Update()
    {
        if (_cardView == null || _completeSynced.Value)
            return;
        if (NetworkManager.Singleton == null)
            return;

        ulong localId = NetworkManager.Singleton.LocalClientId;

        // The turn changed: clear any private peek reveals -- paid intel doesn't
        // carry past your turn.
        if (_currentTurnClientId.Value != _lastKnownTurn)
        {
            _lastKnownTurn = _currentTurnClientId.Value;
            _cardView.ClearReveals();

            // If it just became MY turn, aim the (about-to-be-cursor-freed) camera at
            // the neighbor's fan so I'm never stuck facing away from the cards I must
            // pick from. Only the local player whose turn it is runs this branch.
            if (_currentTurnClientId.Value == localId)
                SnapViewToNeighbor();
        }

        HandlePickInput(localId);

        // The neighbor's hovered card physically rises for everyone, driven by
        // the synced _hoveredCardIndex -- this is the readable bluff-tell.
        _cardView.LayoutUpdate(localId, _currentNeighborClientId.Value, _hoveredCardIndex.Value);
    }

    // Phase 2 picking (free-cursor raycast). Only the local player, on their
    // turn, drives this. We ray from the camera through the mouse, find the
    // nearest card belonging to the CURRENT NEIGHBOR, and treat that as the
    // hover -- sent to the server (and thus everyone) only when it changes, same
    // throttle as before. A left-click on a hovered card draws it. The server
    // re-validates the index in PerformDraw, so a stale hover can't draw garbage.
    private void HandlePickInput(ulong localId)
    {
        bool myTurn = localId == _currentTurnClientId.Value;
        if (!myTurn)
        {
            _lastSentHover = -1; // so the first hover next turn is always sent fresh
            return;
        }

        var cam = ResolveLocalCamera();
        if (cam == null)
            return;

        int hoverIndex = -1;
        float nearest = float.MaxValue;
        var ray = cam.ScreenPointToRay(Input.mousePosition);
        var hits = Physics.RaycastAll(ray, 6f, ~0, QueryTriggerInteraction.Collide);
        foreach (var hit in hits)
        {
            var cardRef = hit.collider.GetComponentInParent<OldMaidCardRef>();
            if (cardRef == null || cardRef.OwnerClientId != _currentNeighborClientId.Value)
                continue;
            if (hit.distance < nearest)
            {
                nearest = hit.distance;
                hoverIndex = cardRef.Index;
            }
        }

        if (hoverIndex != _lastSentHover)
        {
            _lastSentHover = hoverIndex;
            SetHoverServerRpc(hoverIndex);
        }

        if (hoverIndex >= 0 && Input.GetMouseButtonDown(0))
            DrawServerRpc(hoverIndex);

        // Right-click the pointed card to pay-to-peek it (server enforces cost,
        // turn, and once-per-turn). Left-click still draws.
        if (hoverIndex >= 0 && Input.GetMouseButtonDown(1))
            PeekServerRpc(hoverIndex);
    }

    // On the transition into the local player's turn, orient their frozen (cursor
    // will be freed this frame) FP view at the neighbor's fan -- the cards they draw
    // from. Aims at the neighbor player object at ~head height, where the fan sits
    // (ProceduralFanSlotProvider anchors to the head). Client-safe: finds the neighbor
    // by scanning PlayerState (NetworkManager.ConnectedClients is server-only), same
    // pattern SpectatorController.GatherTargets uses.
    private void SnapViewToNeighbor()
    {
        var nm = NetworkManager.Singleton;
        if (nm == null) return;

        Transform neighbor = null;
        foreach (var ps in FindObjectsByType<PlayerState>(FindObjectsSortMode.None))
        {
            if (ps.OwnerClientId == _currentNeighborClientId.Value) { neighbor = ps.transform; break; }
        }
        if (neighbor == null) return;

        var localObj = nm.LocalClient?.PlayerObject;
        var cam = localObj != null ? localObj.GetComponent<PlayerCamera>() : null;
        cam?.SnapLookAt(neighbor.position + Vector3.up * 1.25f);   // ~seated head height, where the fan is
    }

    private static Camera ResolveLocalCamera()
    {
        var nm = NetworkManager.Singleton;
        if (nm != null && nm.LocalClient != null && nm.LocalClient.PlayerObject != null)
        {
            var cam = nm.LocalClient.PlayerObject.GetComponentInChildren<Camera>(true);
            if (cam != null)
                return cam;
        }
        return Camera.main;
    }

    // Rebuild the local card visuals from whatever this client currently knows
    // (own hand + per-player counts). Called from both ClientRpc handlers so the
    // fans always reflect the latest broadcast.
    private void RefreshCardView()
    {
        if (_cardView == null || NetworkManager.Singleton == null)
            return;

        _cardView.UpdateData(_handCounts, _myHand, NetworkManager.Singleton.LocalClientId, CardLabel);
    }

    // The "exactly one unpaired card" invariant (the whole point of Old Maid)
    // silently depends on copiesPerRank being EVEN -- an odd count leaves every
    // rank with a leftover, so "loser = last player holding a card" stops
    // meaning "holds the old maid." The tooltip invites tuning this down for
    // debug testing, so clamp it to a legal value in the inspector before it
    // can ever reach Setup. Editor-only; Setup re-checks at runtime as a
    // backstop for values set via code/prefab.
    void OnValidate()
    {
        if (rankCount < 1)
            rankCount = 1;
        if (copiesPerRank < 2)
            copiesPerRank = 2;
        if (copiesPerRank % 2 != 0)
            copiesPerRank++;
    }

    public void Setup(IReadOnlyList<PlayerState> players)
    {
        // Runtime backstop for the even-copies invariant (see OnValidate) --
        // OnValidate only fires in the editor, so anything that sets this from
        // code or a hand-edited prefab still gets corrected here rather than
        // dealing a deck with multiple unpaired cards.
        if (copiesPerRank % 2 != 0)
        {
            Debug.LogError($"[OldMaidMiniGame] copiesPerRank must be even to guarantee a single unpaired card; got {copiesPerRank}. Forcing {copiesPerRank + 1}.");
            copiesPerRank++;
        }

        _players = players.ToList();
        _seatingOrder = _players.Select(p => p.OwnerClientId).ToList();
        _stillIn = new HashSet<ulong>(_seatingOrder);
        _hands = _seatingOrder.ToDictionary(id => id, id => new List<int>());
        _complete = false;
        _completeSynced.Value = false;
        _loserClientId = null;
        _turnElapsed = 0f;
        _hoveredCardIndex.Value = -1;
        _peekedThisTurn = false;

        DealDeck();

        // Dealing can occasionally empty someone out immediately (e.g. they're
        // dealt exactly a pair and nothing else) -- prune before the first
        // turn rather than assuming everyone starts with cards.
        foreach (var id in _seatingOrder)
        {
            if (_hands[id].Count == 0)
                _stillIn.Remove(id);
        }

        CheckForGameEnd();
        if (!_complete)
        {
            _currentTurnClientId.Value = _seatingOrder.First(id => _stillIn.Contains(id));
            _currentNeighborClientId.Value = NextActivePlayerAfter(_currentTurnClientId.Value);
        }

        BroadcastAllHandsAndCounts();
    }

    public void Tick(float dt)
    {
        if (_complete)
            return;

        _turnElapsed += dt;
        if (_turnElapsed >= turnTimeoutSeconds)
        {
            var current = _currentTurnClientId.Value;
            Debug.Log($"[OldMaidMiniGame] Client {current} idled past {turnTimeoutSeconds}s -- auto-drawing on their behalf.");
            PerformDraw(current);
        }
    }

    public bool IsComplete => _complete;

    public IReadOnlyList<PlayerState> GetLosers()
    {
        if (_loserClientId == null)
            return new List<PlayerState>(); // shouldn't happen by deck construction, but no losers is the safe default

        var loser = _players.FirstOrDefault(p => p.OwnerClientId == _loserClientId.Value);
        return loser != null ? new List<PlayerState> { loser } : new List<PlayerState>();
    }

    public void Teardown()
    {
        _players = null;
        _seatingOrder = null;
        _stillIn = null;
        _hands = null;
    }

    [ServerRpc(RequireOwnership = false)]
    private void DrawServerRpc(int cardIndex, ServerRpcParams rpcParams = default)
    {
        var requester = rpcParams.Receive.SenderClientId;
        if (_complete || requester != _currentTurnClientId.Value)
            return; // not your turn (or stale click after the round already ended) -- ignore

        PerformDraw(requester, cardIndex);
    }

    // Drawer-only: report which face-down slot the cursor is currently over so
    // the server can re-broadcast it to the whole table. Trusts only the
    // current turn player -- a spectator (or a stale client after the turn
    // moved on) can't push a highlight onto someone else's fan. Cheap and
    // frequent by design, but the client only calls it when the hovered slot
    // actually changes (see _lastSentHover), not every frame.
    [ServerRpc(RequireOwnership = false)]
    private void SetHoverServerRpc(int cardIndex, ServerRpcParams rpcParams = default)
    {
        if (_complete || rpcParams.Receive.SenderClientId != _currentTurnClientId.Value)
            return;

        _hoveredCardIndex.Value = cardIndex;
    }

    // Pay to peek one pointed-at card in the neighbor's hand (GDD-departure
    // economy mechanic). Server-authoritative end to end: only the current
    // drawer, once per turn, with enough Points, and a valid index. The rank is
    // sent to the peeker ALONE (targeted ClientRpc, same private channel as your
    // own hand); a separate broadcast plays the visible tell without leaking the
    // rank. Guaranteed reveal -- no chance roll.
    [ServerRpc(RequireOwnership = false)]
    private void PeekServerRpc(int cardIndex, ServerRpcParams rpcParams = default)
    {
        var requester = rpcParams.Receive.SenderClientId;
        if (_complete || requester != _currentTurnClientId.Value || _peekedThisTurn)
            return;

        var peeker = _players?.FirstOrDefault(p => p.OwnerClientId == requester);
        if (peeker == null || peeker.Points.Value < peekCost)
            return; // can't afford it (or roster lookup failed) -- ignore

        ulong neighborClientId = NextActivePlayerAfter(requester);
        var neighborHand = _hands[neighborClientId];
        if (cardIndex < 0 || cardIndex >= neighborHand.Count)
            return; // stale index that raced a hand change -- ignore, don't charge

        // Spending in-match Points is a deliberate exception to IMiniGame's
        // "don't touch PlayerState" rule (see IMiniGame.cs note): mini-games may
        // DEDUCT Points for in-game purchases, but never award them.
        peeker.Points.Value -= peekCost;
        _peekedThisTurn = true;

        int cardId = neighborHand[cardIndex];
        var toPeeker = new ClientRpcParams
        {
            Send = new ClientRpcSendParams { TargetClientIds = new[] { requester } }
        };
        RevealPeekClientRpc(neighborClientId, cardIndex, cardId, toPeeker);

        PeekTellClientRpc(neighborClientId, cardIndex);
    }

    private void PerformDraw(ulong drawerClientId, int? chosenCardIndex = null)
    {
        ulong neighborClientId = NextActivePlayerAfter(drawerClientId);

        var neighborHand = _hands[neighborClientId];

        // chosenCardIndex is the drawer picking a specific face-down slot in
        // the neighbor's fanned-out hand (GDD §3.6 -- the drawer chooses
        // blind, it's not a server-random pull). Falls back to random only
        // when there's no real player choice behind the call, i.e. the
        // anti-stall auto-draw path in Tick(), or a stale/out-of-range click
        // that raced a server-side hand-count change.
        int cardIndex = chosenCardIndex.HasValue && chosenCardIndex.Value >= 0 && chosenCardIndex.Value < neighborHand.Count
            ? chosenCardIndex.Value
            : Random.Range(0, neighborHand.Count);

        int drawnCard = neighborHand[cardIndex];
        neighborHand.RemoveAt(cardIndex);

        var drawerHand = _hands[drawerClientId];
        drawerHand.Add(drawnCard);
        bool paired = TryDiscardPair(drawerHand, drawnCard, out int discardedA, out int discardedB);

        // Phase 3 cosmetic events (fired before the count broadcast so clients
        // capture the pre-rebuild hand positions for the flight start/end).
        // Draw transfer carries no card identity -- a draw is blind, so the
        // flying card is face-down and leaks nothing. A completed pair IS shown
        // to everyone, so the discard event carries both card ids.
        DrawTransferClientRpc(neighborClientId, drawerClientId);
        if (paired)
            PairDiscardClientRpc(drawerClientId, discardedA, discardedB);

        // Compute "whose turn comes after the drawer" BEFORE removing anyone
        // who just emptied out -- the seating order itself never changes,
        // only who's still "in" it, so this is well-defined either way.
        ulong nextTurnClientId = NextActivePlayerAfter(drawerClientId);

        if (neighborHand.Count == 0)
            _stillIn.Remove(neighborClientId);
        if (drawerHand.Count == 0)
            _stillIn.Remove(drawerClientId);

        CheckForGameEnd();
        if (!_complete)
        {
            // If the player picked as "next" was one of the two who just
            // emptied out, walk forward again now that _stillIn reflects it.
            _currentTurnClientId.Value = _stillIn.Contains(nextTurnClientId)
                ? nextTurnClientId
                : NextActivePlayerAfter(drawerClientId);
            _currentNeighborClientId.Value = NextActivePlayerAfter(_currentTurnClientId.Value);
            _turnElapsed = 0f;
            // New turn, new (empty) hover -- clear the broadcast highlight so it
            // doesn't carry the previous drawer's last hovered slot into the
            // next drawer's fan before they've moved their cursor.
            _hoveredCardIndex.Value = -1;
            // New drawer gets a fresh peek allowance.
            _peekedThisTurn = false;
        }

        BroadcastAllHandsAndCounts();
    }

    private ulong NextActivePlayerAfter(ulong id)
    {
        int startIndex = _seatingOrder.IndexOf(id);
        for (int step = 1; step <= _seatingOrder.Count; step++)
        {
            var candidate = _seatingOrder[(startIndex + step) % _seatingOrder.Count];
            if (_stillIn.Contains(candidate))
                return candidate;
        }
        return id; // only reachable if id is the sole remaining player
    }

    private void CheckForGameEnd()
    {
        if (_stillIn.Count <= 1)
        {
            _complete = true;
            _completeSynced.Value = true;
            _loserClientId = _stillIn.Count == 1 ? _stillIn.First() : (ulong?)null;

            // The joker is unpaired by construction and can never be discarded,
            // so exactly one player must always be left holding it -- reaching
            // zero means the deck invariant (one and only one unpaired card)
            // was violated upstream. Don't fail silently: a future deck change
            // that breaks this should be loud, not just quietly report "nobody
            // lost" via the null path in GetLosers.
            if (_stillIn.Count == 0)
                Debug.LogError("[OldMaidMiniGame] Game ended with zero players still in -- the joker should always leave exactly one holder. Deck construction invariant violated; no loser will be reported.");
        }
    }

    private void DealDeck()
    {
        // Real-deck structure with FULL-DECK identity: every card is a UNIQUE id
        // 0..(rankCount*copiesPerRank - 1), encoding both rank and suit, so the
        // renderer can draw the right of 52 faces. rank = id / copiesPerRank,
        // suit = id % copiesPerRank (copiesPerRank == number of suits). Pairing
        // is by RANK, not by exact id (see RankOf / TryDiscardPair), so a hand
        // holding all four suits of a rank still discards two pairs in a row.
        var deck = new List<int>();
        for (int rank = 0; rank < rankCount; rank++)
        {
            for (int suit = 0; suit < copiesPerRank; suit++)
                deck.Add(rank * copiesPerRank + suit);
        }
        deck.Add(JokerId); // the joker -- unique, appears exactly once, no partner

        Shuffle(deck);

        for (int i = 0; i < deck.Count; i++)
        {
            var clientId = _seatingOrder[i % _seatingOrder.Count];
            _hands[clientId].Add(deck[i]);
        }

        // Auto-discard any pairs dealt straight into a player's own hand --
        // you'd notice and discard those immediately in real life, before any
        // turns even start.
        foreach (var clientId in _seatingOrder)
        {
            var hand = _hands[clientId];
            bool foundPair;
            do
            {
                foundPair = false;
                for (int i = 0; i < hand.Count && !foundPair; i++)
                {
                    int j = IndexOfSameRankAfter(hand, i);
                    if (j >= 0)
                    {
                        hand.RemoveAt(j);
                        hand.RemoveAt(i);
                        foundPair = true;
                    }
                }
            } while (foundPair);
        }
    }

    // Pair the just-drawn card with any other card of the SAME RANK (cards are
    // now unique ids, so there's never a second copy of the exact id -- we match
    // on rank). The joker has no rank and never pairs.
    private bool TryDiscardPair(List<int> hand, int drawnCard, out int discardedA, out int discardedB)
    {
        discardedA = drawnCard;
        discardedB = -1;

        int rank = RankOf(drawnCard);
        if (rank < 0)
            return false; // joker -- never pairs

        int drawnIndex = hand.LastIndexOf(drawnCard); // the copy just added
        for (int k = 0; k < hand.Count; k++)
        {
            if (k == drawnIndex || RankOf(hand[k]) != rank)
                continue;

            discardedB = hand[k];
            int hi = Mathf.Max(k, drawnIndex);
            int lo = Mathf.Min(k, drawnIndex);
            hand.RemoveAt(hi); // remove the higher index first so the lower stays valid
            hand.RemoveAt(lo);
            return true;
        }
        return false;
    }

    // rank = id / copiesPerRank; the joker (JokerId) has no rank, returns -1 so
    // it never matches anything (including, defensively, another joker).
    private int JokerId => rankCount * copiesPerRank;

    private int RankOf(int cardId) => cardId == JokerId ? -1 : cardId / copiesPerRank;

    private int IndexOfSameRankAfter(List<int> hand, int i)
    {
        int rank = RankOf(hand[i]);
        if (rank < 0)
            return -1; // joker never pairs

        for (int k = i + 1; k < hand.Count; k++)
            if (RankOf(hand[k]) == rank)
                return k;
        return -1;
    }

    private static void Shuffle(List<int> deck)
    {
        for (int i = deck.Count - 1; i > 0; i--)
        {
            int j = Random.Range(0, i + 1);
            (deck[i], deck[j]) = (deck[j], deck[i]);
        }
    }

    // Cosmetic only -- the card id is the real key everywhere else in this file;
    // this turns an id into "rank+suit" (e.g. "QH") for the readout/peek, and is
    // also what a real face-texture lookup would key off. Falls back rather than
    // throwing if rankCount/copiesPerRank are tuned past the label tables.
    private static readonly string[] RankLabels = { "A", "2", "3", "4", "5", "6", "7", "8", "9", "10", "J", "Q", "K" };
    private static readonly string[] SuitLabels = { "♠", "♥", "♦", "♣" }; // ♠ ♥ ♦ ♣

    private string CardLabel(int cardId)
    {
        if (cardId == JokerId)
            return "Joker";

        int rank = cardId / copiesPerRank;
        int suit = cardId % copiesPerRank;
        string r = rank < RankLabels.Length ? RankLabels[rank] : $"R{rank}";
        string s = suit < SuitLabels.Length ? SuitLabels[suit] : $"S{suit}";
        return r + s;
    }

    // ---- Network sync: counts to everyone, hand contents only to the owner --
    private void BroadcastAllHandsAndCounts()
    {
        var ids = _seatingOrder.ToArray();
        var counts = _seatingOrder.Select(id => _hands[id].Count).ToArray();
        ReceiveHandCountsClientRpc(ids, counts);

        // NGO doesn't support "broadcast, but a different payload per
        // recipient" in one call -- looping a targeted ClientRpc per player
        // is the correct pattern for per-client-private data, not a hack.
        foreach (var id in _seatingOrder)
        {
            var hand = _hands[id].ToArray();
            var sendParams = new ClientRpcParams
            {
                Send = new ClientRpcSendParams { TargetClientIds = new[] { id } }
            };
            ReceiveOwnHandClientRpc(hand, sendParams);
        }
    }

    [ClientRpc]
    private void ReceiveHandCountsClientRpc(ulong[] clientIds, int[] counts)
    {
        _handCounts.Clear();
        for (int i = 0; i < clientIds.Length; i++)
            _handCounts[clientIds[i]] = counts[i];

        RefreshCardView();
    }

    [ClientRpc]
    private void ReceiveOwnHandClientRpc(int[] hand, ClientRpcParams rpcParams = default)
    {
        _myHand = hand.ToList();

        RefreshCardView();
    }

    // Sent only to the peeker: turn the pointed card face-up toward them with its
    // rank. No other client receives this, so the intel stays private.
    [ClientRpc]
    private void RevealPeekClientRpc(ulong neighborClientId, int cardIndex, int cardId, ClientRpcParams rpcParams = default)
    {
        _cardView?.RevealCard(neighborClientId, cardIndex, CardLabel(cardId));
    }

    // Sent to everyone: play the spin tell on the peeked card. Carries no rank,
    // and remotes have no face data for that card, so it reveals nothing -- it
    // just shows the table that a peek happened.
    [ClientRpc]
    private void PeekTellClientRpc(ulong neighborClientId, int cardIndex)
    {
        _cardView?.PlayPeekTell(neighborClientId, cardIndex);
    }

    // Phase 3: a face-down card flies from the neighbor's hand to the drawer's.
    [ClientRpc]
    private void DrawTransferClientRpc(ulong fromClientId, ulong toClientId)
    {
        _cardView?.PlayDrawTransfer(fromClientId, toClientId);
    }

    // Phase 3: a matched pair flies off the drawer's hand and fades, revealed to
    // everyone (the cards are named because a discarded pair is public).
    [ClientRpc]
    private void PairDiscardClientRpc(ulong fromClientId, int cardIdA, int cardIdB)
    {
        _cardView?.PlayPairDiscard(fromClientId, CardLabel(cardIdA), CardLabel(cardIdB));
    }

    // ---- Debug-only display + draw control (no real UI investment, per the
    // slice scope -- same minimal style as NumberPickMiniGame) --
    //
    // Cursor handling follows the same transition-only pattern established by
    // NumberPickMiniGame and MatchController: free the cursor while this
    // mini-game is active so the Draw button is clickable, hand control back
    // the instant it resolves. Written only on the actual transition, not
    // every frame -- see NumberPickMiniGame's _cursorFreedLocally comment for
    // why every-frame writes from multiple scripts fight each other.
    void OnGUI()
    {
        bool gameActive = !_completeSynced.Value;
        bool myTurn = gameActive
            && NetworkManager.Singleton != null
            && NetworkManager.Singleton.LocalClientId == _currentTurnClientId.Value;

        // Free the cursor ONLY for the player whose turn it is -- they need to
        // point at cards to pick/peek, which (via the cursor-lock guard in
        // PlayerMovement/PlayerCamera) suspends their mouse-look for that turn.
        // Everyone else keeps the locked FPS cursor, so non-pickers still look
        // and move freely while watching the table. Transition-only writes.
        bool needCursor = myTurn;

        if (needCursor && !_cursorFreedLocally)
        {
            Cursor.lockState = CursorLockMode.None;
            Cursor.visible = true;
            _cursorFreedLocally = true;
        }
        else if (!needCursor && _cursorFreedLocally)
        {
            Cursor.lockState = CursorLockMode.Locked;
            Cursor.visible = false;
            _cursorFreedLocally = false;
        }

        if (!gameActive)
            return;

        ulong neighborId = _currentNeighborClientId.Value;

        // BOTTOM-LEFT corner, anchored off Screen.height so it stays put at any resolution.
        GUILayout.BeginArea(new Rect(10, Screen.height - 250, 280, 240));
        GUILayout.Label($"[OldMaid] Your hand ({_myHand.Count}): {string.Join(", ", _myHand.Select(CardLabel))}");
        GUILayout.Label(myTurn ? $"Your turn -- pick a face-down card from Client {neighborId}:" : "Waiting for other players...");

        foreach (var kvp in _handCounts)
        {
            if (kvp.Key == NetworkManager.Singleton.LocalClientId)
                continue;
            GUILayout.Label($"Client {kvp.Key}: {kvp.Value} card(s)");
        }

        GUILayout.EndArea();

        // Phase 2: the neighbor's fan and the draw interaction are now the real
        // 3D cards, not IMGUI boxes -- you hover them with the cursor (raycast
        // in HandlePickInput) and the hovered card physically rises for everyone
        // (driven by the synced _hoveredCardIndex, see OldMaidCardView). The old
        // [?] button row lived here and has been retired. This OnGUI is now just
        // the debug text readout above plus the cursor-freeing so the pointer is
        // available for picking.
    }
}
