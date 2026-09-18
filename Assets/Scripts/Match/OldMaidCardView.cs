using System;
using System.Collections.Generic;
using System.Linq;
using Unity.Netcode;
using UnityEngine;

/// <summary>
/// Phase 1 of Old Maid's real (non-debug) presentation layer: render each
/// player's hand as physical 3D cards -- your own faces up, everyone else's
/// face down -- fanned at their avatar. PURE PRESENTATION: nothing here is
/// networked or authoritative. It only ever reads state OldMaidMiniGame has
/// already synced (per-client hand counts, your own hand, whose turn it is),
/// exactly mirroring the codebase's "authority reports, presentation renders"
/// split (see IMiniGame's header and PlayerMovement's owner-writes /
/// everyone-drives-their-own-Animator pattern).
///
/// The hidden-information rule is enforced by the network, not by this view:
/// remotes never receive your ranks (only a count), so they literally cannot
/// render your faces even if this code tried to. We render faces only for the
/// local client's own hand (from _myHand), backs for everyone else (from the
/// count).
///
/// Card PLACEMENT is deliberately held behind the ICardSlotProvider seam below.
/// In the high-fidelity version the cards won't be placed by code at all -- the
/// avatar will play a hold/fan animation and each card will ride a hand/finger
/// bone socket the animation poses. The procedural arc used now is an explicit
/// stand-in for that. When the animation exists, only the slot provider gets
/// swapped; this renderer, the data flow, and (later) hover-lift and transfer
/// animations don't change. That's the whole point of isolating placement here.
/// </summary>

/// <summary>
/// The placement seam. Given "card i of n in player X's hand," return where it
/// should sit in the world. isLocalOwner is true only for the local client's
/// OWN hand, which is what lets the procedural provider tilt your fan up toward
/// your camera (the chosen "option 1" readability tweak) without affecting how
/// anyone else sees you -- each client renders its own copy, so there's no
/// shared transform to fight. Returns false when the player isn't resolvable
/// yet (not spawned, or no local camera), so the renderer can skip that card.
/// </summary>
public interface ICardSlotProvider
{
    // faceTowardViewer: orient the card's face at the local camera. True for the
    // local player's OWN hand, and also for a neighbor card the local player has
    // paid to peek (turned face-up toward them only).
    bool TryGetCardSlot(ulong clientId, int index, int count, bool faceTowardViewer, out Pose slot);
}

/// <summary>
/// Placeholder slot provider: a simple horizontal fan at chest height in front
/// of each avatar, cards rolled apart and oriented toward the viewer. This is
/// the temporary stand-in for animation-driven bone sockets (see the file
/// header). Every constant here is a feel knob -- tune freely; none of it
/// affects game logic.
/// </summary>
public sealed class ProceduralFanSlotProvider : ICardSlotProvider
{
    private const float ForwardOffset = 0.5f;  // how far in front of the head the fan floats
    private const float DropBelowHead = 0.35f; // cards sit below the eye line so they don't block the view
    private const float CardStep = 0.08f;      // lateral metres BETWEEN adjacent cards -- fan width grows with hand size
    private const float ArcHeight = 0.04f;     // middle cards ride slightly higher (gentle bow)
    private const float FanHalfAngleDeg = 17f; // outer cards splay up to this much each way
    private const float DepthStagger = 0.004f; // per-card forward nudge so they don't z-fight

    // clientId -> that player's NetworkObject. Rebuilt lazily on a miss; player
    // objects live for the whole session, so this almost never re-scans.
    private readonly Dictionary<ulong, NetworkObject> _playerCache = new Dictionary<ulong, NetworkObject>();

    public bool TryGetCardSlot(ulong clientId, int index, int count, bool faceTowardViewer, out Pose slot)
    {
        slot = default;

        if (NetworkManager.Singleton == null || NetworkManager.Singleton.SpawnManager == null)
            return false;

        var playerObj = ResolvePlayer(clientId);
        if (playerObj == null)
            return false;

        // The viewer is the LOCAL player's camera. We resolve it from the local
        // player object rather than Camera.main on purpose: the prefab's camera
        // is Untagged, so Camera.main is null. Camera.main stays only as a last
        // resort for a non-networked test scene.
        var viewer = ResolveLocalViewer();
        if (viewer == null)
            return false;

        // Anchor to the card OWNER's head, not the root: the root origin may be
        // at feet OR center depending on the rig, but the camera/head child is
        // always at eye level. A peeked neighbor card still hangs at the
        // neighbor's hand (owner head) -- only its FACING flips toward you.
        var head = ResolveHead(playerObj);
        Vector3 up = Vector3.up;

        // Symmetric integer offset (-(n-1)/2 .. +(n-1)/2) so the fan is centred
        // and its WIDTH grows with the card count instead of cramming a fixed
        // width -- that fixed width was what made big hands clump.
        float centered = index - (count - 1) * 0.5f;
        float norm = count > 1 ? centered / ((count - 1) * 0.5f) : 0f; // -1 .. +1

        Vector3 right = head.right;
        Vector3 anchor = head.position + head.forward * ForwardOffset - up * DropBelowHead;
        Vector3 pos = anchor
                      + right * (centered * CardStep)
                      + up * ((1f - norm * norm) * ArcHeight) // gentle bow, peak in the middle
                      + head.forward * (index * DepthStagger);

        // Orient the labelled (+z) face toward the local camera when this card
        // should be readable to the viewer (own hand, or a card they've peeked);
        // otherwise turn the back toward the camera so they see it face-down.
        Vector3 faceDir = faceTowardViewer ? (viewer.position - pos) : (pos - viewer.position);
        if (faceDir.sqrMagnitude < 1e-6f)
            faceDir = head.forward;

        Quaternion look = Quaternion.LookRotation(faceDir.normalized, up);
        Quaternion roll = Quaternion.AngleAxis(-norm * FanHalfAngleDeg, Vector3.forward); // splay about the card's own forward
        slot = new Pose(pos, look * roll);
        return true;
    }

    // Client-safe player lookup. SpawnManager.GetPlayerNetworkObject only works
    // for OTHER clients on the server, so we instead scan SpawnedObjects (which
    // every client can see) for the player objects. Cached by clientId; on a
    // miss we rebuild the cache from the current spawned set in one pass.
    private NetworkObject ResolvePlayer(ulong clientId)
    {
        if (_playerCache.TryGetValue(clientId, out var cached) && cached != null)
            return cached;

        var nm = NetworkManager.Singleton;
        if (nm == null || nm.SpawnManager == null)
            return null;

        foreach (var kv in nm.SpawnManager.SpawnedObjects)
        {
            var obj = kv.Value;
            if (obj != null && obj.IsPlayerObject)
                _playerCache[obj.OwnerClientId] = obj;
        }

        _playerCache.TryGetValue(clientId, out var found);
        return found;
    }

    // The player's eye-level reference. Every player prefab carries a camera
    // child (enabled only for the owner); its transform is a dependable head
    // anchor regardless of where the avatar's root origin sits. Falls back to
    // the root only if no camera is found.
    private static Transform ResolveHead(NetworkObject playerObj)
    {
        var cam = playerObj.GetComponentInChildren<Camera>(true);
        return cam != null ? cam.transform : playerObj.transform;
    }

    private static Transform ResolveLocalViewer()
    {
        var nm = NetworkManager.Singleton;
        if (nm != null && nm.LocalClient != null && nm.LocalClient.PlayerObject != null)
        {
            // include inactive: non-owner cameras are disabled, but the LOCAL
            // player's is the enabled one we want.
            var cam = nm.LocalClient.PlayerObject.GetComponentInChildren<Camera>(true);
            if (cam != null)
                return cam.transform;
        }
        return Camera.main != null ? Camera.main.transform : null;
    }
}

/// <summary>
/// Builds and maintains the actual card GameObjects from synced data. Two entry
/// points, both driven by OldMaidMiniGame:
///   UpdateData(...)  -- called when the hand composition changes (the two
///                       ClientRpc handlers), rebuilds only the hands that
///                       actually changed (cheap signature check).
///   LayoutUpdate(...) -- called every frame, repositions existing cards via
///                       the slot provider, because in first-person the avatars
///                       (and your camera) move, so the fans must follow.
/// </summary>
public sealed class OldMaidCardView
{
    // Placeholder card dimensions, metres (roughly a real playing card, scaled
    // up a little for legibility). Swapped for real card meshes/art later.
    private const float CardWidth = 0.16f;
    private const float CardHeight = 0.22f;
    private const float CardThickness = 0.004f;

    // Phase 2: how far the hovered card rises, and how fast it lerps there.
    private const float HoverLift = 0.06f;     // metres the hovered card rises
    private const float LiftSpeed = 0.5f;      // metres/second of rise/fall (smooth, not a snap)
    private const float PeekSpinDuration = 0.6f; // seconds the "someone peeked" spin tell lasts
    private const float TransferDuration = 0.35f; // seconds for a drawn card to fly neighbor -> drawer
    private const float DiscardDuration = 0.55f;  // seconds for a matched pair to fly off and fade
    private const float DiscardRise = 0.45f;      // metres the discard target floats above the table centre

    private readonly ICardSlotProvider _slots;
    private readonly Dictionary<ulong, List<GameObject>> _cards = new Dictionary<ulong, List<GameObject>>();
    // Cheap "what did I last build for this player" key, so UpdateData only
    // rebuilds a hand when its faces/count actually changed instead of every
    // broadcast.
    private readonly Dictionary<ulong, string> _signatures = new Dictionary<ulong, string>();

    // Phase 3: transient "flying" cards for draw/discard transfers. Pure overlay,
    // not tied to _cards -- the real fans rebuild underneath as before; these just
    // sell the motion and then destroy themselves.
    private readonly List<CardFlight> _flights = new List<CardFlight>();

    private sealed class CardFlight
    {
        public GameObject Go;
        public Vector3 Start;
        public Vector3 End;
        public float Elapsed;
        public float Duration;
        public bool FadeOut; // shrink to nothing as it lands (used by discards)
    }

    private Transform _root;
    private Material _faceMat;
    private Material _backMat;
    private Font _labelFont;

    public OldMaidCardView(ICardSlotProvider slots)
    {
        _slots = slots;
    }

    private void EnsureResources()
    {
        if (_root == null)
            _root = new GameObject("OldMaidCardView").transform;

        if (_faceMat == null)
            _faceMat = MakeMaterial(new Color(0.92f, 0.90f, 0.85f)); // off-white card front
        if (_backMat == null)
            _backMat = MakeMaterial(new Color(0.18f, 0.06f, 0.22f)); // dark casino-purple back
        if (_labelFont == null)
            _labelFont = LoadBuiltinFont();
    }

    // Unity 2022+ removed the implicit built-in Arial that TextMesh used to fall
    // back to, so a font-less TextMesh renders nothing. Pull the built-in legacy
    // font explicitly (older name "Arial.ttf" as a fallback) so the ranks show.
    private static Font LoadBuiltinFont()
    {
        return Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf")
               ?? Resources.GetBuiltinResource<Font>("Arial.ttf");
    }

    private static Material MakeMaterial(Color c)
    {
        // Project renders on URP; fall back to Standard so this still shows in a
        // built-in pipeline test scene rather than turning magenta.
        var shader = Shader.Find("Universal Render Pipeline/Lit") ?? Shader.Find("Standard");
        var m = new Material(shader);
        if (m.HasProperty("_BaseColor"))
            m.SetColor("_BaseColor", c); // URP Lit
        m.color = c;                     // Standard / safety
        return m;
    }

    public void UpdateData(IReadOnlyDictionary<ulong, int> counts, IReadOnlyList<int> myHand, ulong localId, Func<int, string> label)
    {
        if (counts == null)
            return;

        EnsureResources();

        // Drop any player who left the table since last refresh.
        var gone = _cards.Keys.Where(id => !counts.ContainsKey(id)).ToList();
        foreach (var id in gone)
            DestroyHand(id);

        foreach (var kv in counts)
        {
            ulong id = kv.Key;
            int count = kv.Value;
            bool owner = id == localId;

            // Your own hand is rendered from the real ranks (faces); everyone
            // else from the count alone (backs). The signature captures exactly
            // that, so a rebuild only happens on a real change.
            string signature = owner
                ? "F:" + string.Join(",", myHand ?? new List<int>())
                : "B:" + count;

            if (_signatures.TryGetValue(id, out var prev) && prev == signature)
                continue;

            DestroyHand(id);

            var list = new List<GameObject>();
            if (owner)
            {
                var hand = myHand ?? new List<int>();
                for (int i = 0; i < hand.Count; i++)
                    list.Add(CreateCard(label != null ? label(hand[i]) : hand[i].ToString(), true, id, i));
            }
            else
            {
                for (int i = 0; i < count; i++)
                    list.Add(CreateCard(null, false, id, i));
            }

            _cards[id] = list;
            _signatures[id] = signature;
        }
    }

    public void LayoutUpdate(ulong localId, ulong neighborId, int hoveredIndex)
    {
        float dt = Time.deltaTime;

        UpdateFlights(dt);

        foreach (var kv in _cards)
        {
            ulong id = kv.Key;
            var list = kv.Value;
            bool owner = id == localId;

            for (int i = 0; i < list.Count; i++)
            {
                var go = list[i];
                if (go == null)
                    continue;

                var cardRef = go.GetComponent<OldMaidCardRef>();
                bool revealed = cardRef != null && cardRef.Revealed;

                // Face the viewer for your own cards, and for a neighbor card
                // you've peeked (revealed locally on your client only).
                bool faceTowardViewer = owner || revealed;

                if (_slots.TryGetCardSlot(id, i, list.Count, faceTowardViewer, out var pose))
                {
                    if (cardRef != null)
                    {
                        // Raise the hovered card (or a peeked one). Hover is
                        // driven by the synced hoveredIndex, so every client
                        // lerps the same card -- the shared, watchable tell.
                        bool isHovered = id == neighborId && i == hoveredIndex;
                        float target = (isHovered || revealed) ? HoverLift : 0f;
                        cardRef.CurrentLift = Mathf.MoveTowards(cardRef.CurrentLift, target, LiftSpeed * dt);
                        pose.position += Vector3.up * cardRef.CurrentLift;

                        // "Someone peeked this card" spin tell -- shown on every
                        // client. The rank only exists on the peeker's machine,
                        // so a back-faced card spinning reveals nothing.
                        if (cardRef.SpinTimer > 0f)
                        {
                            cardRef.SpinTimer -= dt;
                            float progress = Mathf.Clamp01(cardRef.SpinTimer / PeekSpinDuration);
                            pose.rotation *= Quaternion.AngleAxis(progress * 360f, Vector3.up);
                        }
                    }

                    go.transform.SetPositionAndRotation(pose.position, pose.rotation);
                    if (!go.activeSelf)
                        go.SetActive(true);
                }
                else if (go.activeSelf)
                {
                    // Player/camera not resolvable this frame -- hide rather
                    // than leave the card stranded at the origin.
                    go.SetActive(false);
                }
            }
        }
    }

    public void Clear()
    {
        foreach (var list in _cards.Values)
            foreach (var go in list)
                if (go != null)
                    UnityEngine.Object.Destroy(go);

        foreach (var flight in _flights)
            if (flight.Go != null)
                UnityEngine.Object.Destroy(flight.Go);
        _flights.Clear();

        _cards.Clear();
        _signatures.Clear();

        if (_root != null)
            UnityEngine.Object.Destroy(_root.gameObject);
        _root = null;
    }

    private void DestroyHand(ulong id)
    {
        if (_cards.TryGetValue(id, out var list))
        {
            foreach (var go in list)
                if (go != null)
                    UnityEngine.Object.Destroy(go);
            _cards.Remove(id);
        }
        _signatures.Remove(id);
    }

    // A placeholder card: an unscaled root (the thing the slot provider moves),
    // a flattened cube body for the card itself, and -- for face-up cards -- a
    // TextMesh rank label sitting just off the +z face. Kept as a root+children
    // so the label isn't distorted by the body's non-uniform scale.
    private GameObject CreateCard(string face, bool faceUp, ulong ownerClientId, int index)
    {
        var card = new GameObject(faceUp ? $"Card_{face}" : "Card_Back");
        card.transform.SetParent(_root, false);

        // Phase 2: tag the card so a raycast hit knows whose hand and which slot
        // it is (see OldMaidCardRef / OldMaidMiniGame.HandlePickInput).
        var cardRef = card.AddComponent<OldMaidCardRef>();
        cardRef.OwnerClientId = ownerClientId;
        cardRef.Index = index;

        var body = GameObject.CreatePrimitive(PrimitiveType.Cube);
        body.transform.SetParent(card.transform, false);
        body.transform.localScale = new Vector3(CardWidth, CardHeight, CardThickness);

        // Phase 2: keep the box collider so the picker's ray can hit the card,
        // but make it a TRIGGER. A solid collider sat ~0.5 m in front of your
        // face and blocked/pushed the CharacterController -- walking forward hit
        // your own cards (so you could only back up) and lifting/brushing cards
        // shoved the capsule. Triggers don't block or push movement, and the
        // pick raycast still hits them (it passes QueryTriggerInteraction.Collide).
        var col = body.GetComponent<Collider>();
        if (col != null)
            col.isTrigger = true;

        cardRef.Body = body.GetComponent<MeshRenderer>();
        cardRef.Body.sharedMaterial = faceUp ? _faceMat : _backMat;

        if (faceUp && !string.IsNullOrEmpty(face))
            MakeLabel(card.transform, face);

        return card;
    }

    // Builds the rank text just off the +z face. Shared by face-up own cards and
    // by the peek reveal (which adds a label to an otherwise face-down card).
    private GameObject MakeLabel(Transform parent, string text)
    {
        var labelGo = new GameObject("Label");
        labelGo.transform.SetParent(parent, false);
        labelGo.transform.localPosition = new Vector3(0f, 0f, CardThickness * 0.5f + 0.001f);
        // TextMesh's readable side faces its local -Z; the card is turned so its
        // +Z faces the viewer, so without this flip you'd see the text's back
        // (mirrored). Rotate 180° on Y so the readable side faces the viewer.
        labelGo.transform.localRotation = Quaternion.Euler(0f, 180f, 0f);

        var tm = labelGo.AddComponent<TextMesh>();
        tm.text = text;
        tm.anchor = TextAnchor.MiddleCenter;
        tm.alignment = TextAlignment.Center;
        tm.color = Color.black;
        tm.fontSize = 90;            // high res, scaled down by characterSize
        tm.characterSize = 0.0075f;  // sized to sit inside the card face -- tune here

        // Assign the built-in font AND its material -- a TextMesh with a font but
        // the default material still renders blank.
        if (_labelFont == null)
            _labelFont = LoadBuiltinFont();
        if (_labelFont != null)
        {
            tm.font = _labelFont;
            var mr = labelGo.GetComponent<MeshRenderer>();
            if (mr != null)
                mr.sharedMaterial = _labelFont.material;
        }

        return labelGo;
    }

    // ---- Peek (called only on the peeker's client, via targeted ClientRpc) ----

    // Turn a face-down neighbor card face-up toward the local viewer and show its
    // rank. Only this client ever calls this, so the reveal stays private.
    public void RevealCard(ulong ownerClientId, int index, string rankLabel)
    {
        if (!_cards.TryGetValue(ownerClientId, out var list) || index < 0 || index >= list.Count)
            return;

        var go = list[index];
        if (go == null)
            return;

        var cardRef = go.GetComponent<OldMaidCardRef>();
        if (cardRef == null)
            return;

        cardRef.Revealed = true;
        if (cardRef.Body != null)
            cardRef.Body.sharedMaterial = _faceMat;
        if (cardRef.RevealLabel == null)
            cardRef.RevealLabel = MakeLabel(go.transform, rankLabel);
    }

    // Revert any peeked cards to face-down backs (e.g. when the turn ends).
    public void ClearReveals()
    {
        foreach (var list in _cards.Values)
            foreach (var go in list)
            {
                if (go == null)
                    continue;
                var cardRef = go.GetComponent<OldMaidCardRef>();
                if (cardRef == null || !cardRef.Revealed)
                    continue;

                cardRef.Revealed = false;
                if (cardRef.Body != null)
                    cardRef.Body.sharedMaterial = _backMat;
                if (cardRef.RevealLabel != null)
                {
                    UnityEngine.Object.Destroy(cardRef.RevealLabel);
                    cardRef.RevealLabel = null;
                }
            }
    }

    // Start the "someone peeked this card" spin tell. Called on EVERY client.
    public void PlayPeekTell(ulong ownerClientId, int index)
    {
        if (!_cards.TryGetValue(ownerClientId, out var list) || index < 0 || index >= list.Count)
            return;

        var go = list[index];
        if (go == null)
            return;

        var cardRef = go.GetComponent<OldMaidCardRef>();
        if (cardRef != null)
            cardRef.SpinTimer = PeekSpinDuration;
    }

    // ---- Phase 3: draw/discard transfer animations (overlay flights) ----

    // A face-DOWN card flies from the neighbor's hand to the drawer's. Face-down
    // is the hidden-info-safe choice: a real draw is blind, so this leaks nothing
    // to the table; the drawer still learns the card via their own rebuilt fan.
    public void PlayDrawTransfer(ulong fromClientId, ulong toClientId)
    {
        if (!TryGetHandCenter(fromClientId, out var start) || !TryGetHandCenter(toClientId, out var end))
            return;

        var go = CreateFlightCard(false, null);
        _flights.Add(new CardFlight { Go = go, Start = start, End = end, Duration = TransferDuration, FadeOut = false });
    }

    // Two face-UP cards lift off the drawer's hand toward the table centre and
    // fade -- revealing the matched pair to everyone, which is the rule.
    public void PlayPairDiscard(ulong fromClientId, string labelA, string labelB)
    {
        if (!TryGetHandCenter(fromClientId, out var start))
            return;

        Vector3 end = TryGetTableCenter(out var centre)
            ? centre + Vector3.up * DiscardRise
            : start + Vector3.up * DiscardRise;

        // Nudge the two cards apart so they read as a pair, not one card.
        SpawnDiscardCard(labelA, start + Vector3.left * 0.05f, end + Vector3.left * 0.08f);
        SpawnDiscardCard(labelB, start + Vector3.right * 0.05f, end + Vector3.right * 0.08f);
    }

    private void SpawnDiscardCard(string label, Vector3 start, Vector3 end)
    {
        var go = CreateFlightCard(true, label);
        _flights.Add(new CardFlight { Go = go, Start = start, End = end, Duration = DiscardDuration, FadeOut = true });
    }

    private void UpdateFlights(float dt)
    {
        var viewer = ResolveViewer();

        for (int i = _flights.Count - 1; i >= 0; i--)
        {
            var f = _flights[i];
            if (f.Go == null)
            {
                _flights.RemoveAt(i);
                continue;
            }

            f.Elapsed += dt;
            float t = Mathf.Clamp01(f.Elapsed / f.Duration);
            float eased = 1f - (1f - t) * (1f - t); // ease-out

            Vector3 pos = Vector3.Lerp(f.Start, f.End, eased);
            // small arc so it lofts rather than slides flat
            pos += Vector3.up * (Mathf.Sin(t * Mathf.PI) * 0.12f);

            Quaternion rot = viewer != null
                ? Quaternion.LookRotation((pos - viewer.position).normalized, Vector3.up)
                : f.Go.transform.rotation;
            f.Go.transform.SetPositionAndRotation(pos, rot);

            if (f.FadeOut)
                f.Go.transform.localScale = Vector3.one * Mathf.Lerp(1f, 0.1f, t);

            if (t >= 1f)
            {
                UnityEngine.Object.Destroy(f.Go);
                _flights.RemoveAt(i);
            }
        }
    }

    // A lightweight transient card -- no collider, no OldMaidCardRef (it's not a
    // hand card and must never be raycast-pickable).
    private GameObject CreateFlightCard(bool faceUp, string label)
    {
        EnsureResources();

        var card = new GameObject(faceUp ? "Flight_Face" : "Flight_Back");
        card.transform.SetParent(_root, false);

        var body = GameObject.CreatePrimitive(PrimitiveType.Cube);
        body.transform.SetParent(card.transform, false);
        body.transform.localScale = new Vector3(CardWidth, CardHeight, CardThickness);
        var col = body.GetComponent<Collider>();
        if (col != null)
            UnityEngine.Object.Destroy(col);
        body.GetComponent<MeshRenderer>().sharedMaterial = faceUp ? _faceMat : _backMat;

        if (faceUp && !string.IsNullOrEmpty(label))
            MakeLabel(card.transform, label);

        return card;
    }

    private bool TryGetHandCenter(ulong clientId, out Vector3 center)
    {
        center = Vector3.zero;
        if (!_cards.TryGetValue(clientId, out var list))
            return false;

        int n = 0;
        Vector3 sum = Vector3.zero;
        foreach (var go in list)
            if (go != null && go.activeSelf)
            {
                sum += go.transform.position;
                n++;
            }

        if (n == 0)
            return false;
        center = sum / n;
        return true;
    }

    // Rough table centre = average of every player's hand centre. Good enough as
    // a discard target for the placeholder; a real discard tray is art-side.
    private bool TryGetTableCenter(out Vector3 center)
    {
        center = Vector3.zero;
        int n = 0;
        Vector3 sum = Vector3.zero;
        foreach (var id in _cards.Keys)
            if (TryGetHandCenter(id, out var c))
            {
                sum += c;
                n++;
            }

        if (n == 0)
            return false;
        center = sum / n;
        return true;
    }

    private static Transform ResolveViewer()
    {
        var nm = NetworkManager.Singleton;
        if (nm != null && nm.LocalClient != null && nm.LocalClient.PlayerObject != null)
        {
            var cam = nm.LocalClient.PlayerObject.GetComponentInChildren<Camera>(true);
            if (cam != null)
                return cam.transform;
        }
        return Camera.main != null ? Camera.main.transform : null;
    }
}
