using System.Collections;
using System.Collections.Generic;
using System.Linq;
using CasinoHorrorGame.Networking;
using Unity.Netcode;
using UnityEngine;

/// <summary>
/// The authoritative host-side game state machine (GDD §13.3): Table -> MiniGame
/// -> Resolution -> Chase -> Resolution -> Table -> ... -> Win. One instance,
/// placed directly in the scene (not per-player, unlike VoiceRoomRouter/
/// PlayerState).
///
/// Server-only. Update() bails immediately on clients -- same guard pattern as
/// VoiceRoomRouter's IsServer branch in OnNetworkSpawn. Every Run* method below
/// is only ever reached through that guard.
///
/// Scope note (vertical slice, 2026-06-28): ChaseInProgress is a debug stub --
/// real chase mechanics (dungeon, monster AI, catch detection) don't exist yet
/// and are a separate task (GDD §13.4 step 4). This proves the loop end-to-end
/// with a manual Escaped/Caught control standing in for "did the chase
/// resolve." VolunteerWindow is deliberately not implemented this pass -- it
/// has nothing real to volunteer into until chase exists for real.
/// </summary>
[RequireComponent(typeof(NetworkObject))]
public class MatchController : NetworkBehaviour
{
    public enum Phase
    {
        Lobby,
        RoundStart,
        GameSelect,
        GamePlay,
        GameResolve,
        ChaseInProgress,
        ChaseResolve,
        RoundEnd,
        GameOver
    }

    [Header("Slice defaults -- placeholders, tune freely")]
    [Tooltip("Host-set fixed round count (GDD §8/§11). Hardcoded for this slice.")]
    [SerializeField] private int roundCount = 5;

    [Tooltip("Minimum active (non-benched) players GameSelect requires. GDD §5.1 marks the real floor value [OPEN] -- this is a placeholder, currently only logged, not enforced.")]
    [SerializeField] private int activeFloorMinimum = 2;

    [Tooltip("Base chips earned by a non-losing player in a round, before the streak multiplier. GDD §8 confirms payout scales with streak but doesn't lock an exact formula -- placeholder (payout = basePayout * streak).")]
    [SerializeField] private int basePayout = 10;

    [Tooltip("Prefab for the mini-game currently wired into RunGameSelect. Swapped from NumberPickMiniGame to OldMaidMiniGame to prove the new game in isolation -- not yet a real registry/picker (GDD §3.1). Must be registered in NetworkManager's NetworkPrefabs list.")]
    [SerializeField] private OldMaidMiniGame miniGamePrefab;

    [Tooltip("Verbose [CHASE] console logging (drop / return). Off by default; flip on to debug.")]
    [SerializeField] private bool debugChaseLogs = false;

    [Tooltip("AI Stalker prefab, spawned into the dungeon for the chase. Must be registered " +
             "in NetworkManager's NetworkPrefabs.")]
    [SerializeField] private StalkerAI stalkerPrefab;

    [Tooltip("Seconds the loser rides the chair down into the void before being teleported " +
             "to the dungeon. Mask this window with a screen fade later.")]
    [SerializeField] private float fallDropDelay = 1.2f;   // legacy: no longer used for the drop wait

    [Tooltip("Safety cap (s) on the fall-to-shaft-bottom wait, so a stalled fall can never strand a " +
             "player in the shaft.")]
    [SerializeField] private float fallSafetyTimeout = 5f;

    [Header("Debug -- lobby hold")]
    [Tooltip("If true, once every player is ready the match WAITS in Lobby for the host " +
             "to press Start Match Key -- so you can watch the seating ring re-space " +
             "(360/N even placement) before the game begins. Ready flags aren't consumed " +
             "during the hold, so the chairs stay up. Turn off for normal auto-start.")]
    [SerializeField] private bool holdForManualStart = true;

    [Tooltip("Host key that starts the match while Hold For Manual Start is on.")]
    [SerializeField] private KeyCode startMatchKey = KeyCode.Return;

    private readonly NetworkVariable<Phase> _phase = new NetworkVariable<Phase>(
        Phase.Lobby,
        NetworkVariableReadPermission.Everyone,
        NetworkVariableWritePermission.Server);

    private readonly NetworkVariable<int> _roundNumber = new NetworkVariable<int>(
        0,
        NetworkVariableReadPermission.Everyone,
        NetworkVariableWritePermission.Server);

    private List<PlayerState> _activeRoster = new List<PlayerState>();
    private List<PlayerState> _fallenPlayers = new List<PlayerState>();

    // true = escaped, false = caught. Populated by the debug OnGUI control below.
    private readonly Dictionary<PlayerState, bool> _chaseOutcomes = new Dictionary<PlayerState, bool>();

    private IMiniGame _currentMiniGame;
    private NetworkObject _currentMiniGameInstance;
    private NetworkObject _stalkerInstance;

    // The exact dungeon spawn points the fallen players were dropped onto this chase. Captured at drop
    // time because the server knows them immediately -- the players' transform.position is unreliable
    // right after the drop (the teleport is an async owner RPC that hasn't round-tripped yet). Used so
    // the Stalker spawns FAR from the prey and never ON one of their spawns.
    private readonly List<Transform> _preySpawns = new List<Transform>();

    public static MatchController Instance { get; private set; }

    private void Awake()
    {
        Instance = this;
    }

    public override void OnDestroy()
    {
        if (Instance == this)
            Instance = null;
        base.OnDestroy();
    }

    void Update()
    {
        if (!IsServer)
            return;

        switch (_phase.Value)
        {
            case Phase.Lobby:
                RunLobby();
                break;
            case Phase.RoundStart:
                RunRoundStart();
                break;
            case Phase.GameSelect:
                RunGameSelect();
                break;
            case Phase.GamePlay:
                RunGamePlay();
                break;
            case Phase.GameResolve:
                RunGameResolve();
                break;
            case Phase.ChaseInProgress:
                break; // chase is event-driven (ReportEscape / ReportCaught) -- nothing per-frame
            case Phase.ChaseResolve:
                RunChaseResolve();
                break;
            case Phase.RoundEnd:
                RunRoundEnd();
                break;
            case Phase.GameOver:
                break; // terminal -- nothing left to do
        }
    }

    private void RunLobby()
    {
        // Lobby gate -- replaces the old implicit "auto-start as soon as 2 players
        // exist". Hold in Lobby until the floor minimum is connected AND every
        // connected player has readied up. No one is benched in lobby, so all
        // connected players count.
        var players = AllPlayerStates();

        if (players.Count < activeFloorMinimum)
            return;
        if (!players.All(p => p.IsReady.Value))
            return;

        // Hold gate: everyone's ready, but wait for the host to press startMatchKey
        // before actually starting, so the seating ring's 360/N re-spacing can be
        // inspected first. We return BEFORE consuming the ready flags below, so the
        // players stay seated and the chairs stay up during the hold. Ready/unready
        // during the hold still re-spaces the ring live. Set holdForManualStart = false
        // to restore the old auto-start.
        if (holdForManualStart && !Input.GetKeyDown(startMatchKey))
            return;

        // NOTE: ready flags are intentionally NOT cleared here anymore. Clearing them told
        // TableSeatingManager to un-seat everyone (ready->false despawns the chair), which
        // yanked players off their seats the instant the match started. Keeping them ready
        // keeps them seated through the minigame and the drop. Reset ready only on a real
        // return-to-lobby (not implemented yet).

        // Voice roles: players STAY in the shared 'Lobby' room through the minigame AND the fall.
        // Survivors at the table hear each other, and hear a dropping player's SCREAM on the way down
        // (Lobby is positional, so it comes from the shaft). Only a FALLEN player flips to Prey, and
        // only once they reach the dungeon (see FallThenDropRoutine) -- so the lobby never hears the
        // ongoing CHASE (that's spectators-only), just the descent.
        // ⚠ CHANGED 2026-08-01 from flipping EVERYONE to Prey here, which leaked the whole chase into
        // the table (every survivor received every dungeon prey voice positionally). Enoch's call:
        // lobby hears the fall, not the chase; the chase is spectators-only.
        //
        // ⚠ NO PLAYER GETS Role.Monster IN THE SLICE -- the monster is the AI Stalker. The Monster
        // role + routing stay intact in VoiceRoomRouter for a future player-monster; nothing hands it
        // out. Restore a monster pick here at that point.

        _phase.Value = Phase.RoundStart;
    }

    private void RunRoundStart()
    {
        // Roster is computed from the CURRENT (pre-decrement) bench value, so
        // a player set to BenchRoundsRemaining = 1 last round is correctly
        // excluded THIS round. Decrementing first (the original order) made
        // the value hit 0 before IsActive was ever checked against it, so the
        // bench never actually excluded anyone -- the one-round sit-out was a
        // no-op in practice, just disguised by how fast phases advance.
        // Require IsReady as well as IsActive: a returnee who hasn't walked back and re-readied
        // at the table is excluded from the round (they're not seated, so they can't play).
        _activeRoster = AllPlayerStates().Where(p => p.IsActive && p.IsReady.Value).ToList();

        // Don't advance until enough players have actually connected and
        // spawned -- without this, RoundStart fires on the very first Update()
        // after Host starts, often before a second client has joined, handing
        // GameSelect a 1-player roster. A 1-player mini-game resolves itself
        // as an instant loser with no real gameplay frame ever happening
        // (2026-06-29: this is exactly what made OldMaidMiniGame look broken
        // when the actual bug was here). _activeRoster is recomputed above
        // every time this runs, so the match starts the instant the floor is
        // met instead of needing a manual retry.
        // Tick the bench down BEFORE the floor gate. With a small lobby, benching a
        // player drops the active roster below the floor; if the decrement stays gated
        // behind the floor it never fires and the match FREEZES at RoundStart -- exactly
        // the "press P -> caught -> next round never starts" symptom. Roster was computed
        // above from the PRE-decrement value, so the benched player is still correctly
        // excluded from THIS round (the one-round sit-out still holds for larger lobbies).
        // Bench cooldowns tick at the CHASE's conclusion now (RunChaseResolve), not here, so a
        // caught player sits out the whole next round (spectating) until that round's chase ends.

        // Seat whoever is ready + active for the coming round.
        TableSeatingManager.Instance?.ServerReconcileSeats();

        if (_activeRoster.Count < activeFloorMinimum)
            return;

        _phase.Value = Phase.GameSelect;
    }

    private void RunGameSelect()
    {
        // Settle any in-progress seat re-space so the chairs are still for the minigame.
        TableSeatingManager.Instance?.ServerSnapSeats();

        // Pre-build the chase dungeon NOW, while the minigame is starting, so the navmesh bake +
        // geometry spawn hitch is masked by this calm transition instead of stuttering at the fall.
        // The dungeon doesn't depend on WHO loses, so generating before the outcome is safe.
        DungeonGenerator.Instance?.ServerGenerate();

        // Only one mini-game exists this pass, so this is a stub -- but kept
        // as its own method so a real count-aware registry (GDD §3.1) has a
        // single seam to plug into later instead of requiring a rewrite.
        if (_activeRoster.Count < activeFloorMinimum)
        {
            Debug.LogWarning($"[MatchController] Active roster ({_activeRoster.Count}) below floor minimum ({activeFloorMinimum}) -- GDD §5.1 floor rule is [OPEN], this slice logs and proceeds anyway.");
        }

        var instance = Instantiate(miniGamePrefab);
        instance.NetworkObject.Spawn();
        _currentMiniGame = instance;
        _currentMiniGameInstance = instance.NetworkObject;

        _currentMiniGame.Setup(_activeRoster);
        _phase.Value = Phase.GamePlay;
    }

    private void RunGamePlay()
    {
        _currentMiniGame.Tick(Time.deltaTime);

        if (_currentMiniGame.IsComplete)
            _phase.Value = Phase.GameResolve;
    }

    private void RunGameResolve()
    {
        var losers = _currentMiniGame.GetLosers();
        _currentMiniGame.Teardown();

        if (_currentMiniGameInstance != null && _currentMiniGameInstance.IsSpawned)
            _currentMiniGameInstance.Despawn();

        _currentMiniGame = null;
        _currentMiniGameInstance = null;

        foreach (var player in _activeRoster)
        {
            if (losers.Contains(player))
                continue;

            player.SurvivalStreak.Value++;
            player.Points.Value += basePayout * Mathf.Max(1, player.SurvivalStreak.Value);
        }

        if (losers.Count == 0)
        {
            _phase.Value = Phase.RoundEnd;
            return;
        }

        _fallenPlayers = losers.ToList();
        _chaseOutcomes.Clear();

        // THE FALL (GDD §4): the loser rides their chair down through the trapdoor into the
        // void, THEN (after fallDropDelay) is teleported to the dungeon and the stalker
        // spawns. Enter the chase phase now; the coroutine sequences physical drop ->
        // teleport -> stalker so the fall reads as one continuous plunge.
        DungeonObjective.Instance?.ServerReset();       // exits start LOCKED each chase (dungeon was pre-built at GameSelect)
        _phase.Value = Phase.ChaseInProgress;
        StartCoroutine(FallThenDropRoutine());
        // Cursor stays LOCKED through the chase so the dungeon player keeps mouse-look.
    }

    // Waits until every current faller's station satisfies `done`, or a timeout. Sequences the drop
    // off real fall progress (clear-the-opening, reach-the-bottom) instead of fixed delays.
    private IEnumerator WaitFallersUntil(float timeout, System.Func<SeatStation, bool> done)
    {
        var seating = TableSeatingManager.Instance;
        float waited = 0f;
        while (waited < timeout)
        {
            bool all = true;
            foreach (var loser in _fallenPlayers)
            {
                if (loser == null) continue;
                var st = seating != null ? seating.FallingStationOf(loser.OwnerClientId) : null;
                if (st != null && !done(st)) { all = false; break; }
            }
            if (all) yield break;
            waited += Time.deltaTime;
            yield return null;
        }
    }

    // Sequences the physical drop: chairs fall first, then (after a delay that masks the
    // hand-off) the fallen are released, teleported to the dungeon, their chairs cleaned up,
    // and the stalker spawns. Server-only (started from RunGameResolve, which only runs on
    // the server via the Update guard).
    private IEnumerator FallThenDropRoutine()
    {
        var seating = TableSeatingManager.Instance;

        // 1) Open each loser's trapdoor and start the chair falling -- they ride it down.
        foreach (var loser in _fallenPlayers)
            if (loser != null && seating != null)
                seating.ServerDrop(loser.OwnerClientId);

        // 2a) Wait until the fallers CLEAR the opening (a short drop past the frame), then slam each
        //     trapdoor shut behind them. The open-hole edge-outline artifact is only visible for this
        //     split-second window; from here the scream muffles behind the closed floor.
        yield return WaitFallersUntil(fallSafetyTimeout, st => st.HasClearedOpening);
        foreach (var loser in _fallenPlayers)
        {
            if (loser == null) continue;
            var st = seating != null ? seating.FallingStationOf(loser.OwnerClientId) : null;
            if (st != null) st.ServerSetOpen(false);
        }

        // 2b) Wait until they reach the shaft BOTTOM -- they fall the rest behind the closed floor,
        //     seeing the descent from inside -- so the shaft length is the drop length. A safety
        //     timeout guarantees a stalled fall can never strand a player.
        yield return WaitFallersUntil(fallSafetyTimeout, st => st.HasReachedShaftBottom);

        // 2c) VOICE: they've reached the dungeon -- flip each faller from Lobby to Prey now (NOT
        //     earlier, so the table heard the scream on the way down). From here only spectators +
        //     the monster hear them; the lobby never hears the chase.
        foreach (var loser in _fallenPlayers)
        {
            if (loser == null) continue;
            var voice = loser.GetComponent<VoiceRoomRouter>();
            if (voice != null) voice.ServerAssignRole(VoiceRoomRouter.Role.Prey);
        }

        // 3) Release each loser from the (falling) chair so the teleport can take over.
        foreach (var loser in _fallenPlayers)
            if (loser != null)
            {
                var occ = loser.GetComponent<SeatOccupant>();
                if (occ != null) occ.ServerRelease();
            }

        // Let the release replicate one frame before teleporting, so the owner has stopped
        // gluing itself to the chair (avoids a 1-frame snap-back fight).
        yield return null;

        // 4) Teleport to the dungeon. Trapdoors are now CLOSED (artifact hidden); the empty seats
        //    remain as the marker. Stations are NOT despawned here -- the next ready-up's re-space
        //    (ServerReconcileSeats) sweeps them when the table resets.
        DropLosersIntoDungeon();

        // Each faller now has their flashlight -- lit for a few beats, then it comedically drops.
        foreach (var loser in _fallenPlayers)
            if (loser != null) loser.GetComponent<FlashlightController>()?.ServerEnterDungeon();

        // 5) The chase begins for real.
        SpawnStalker();
    }

    private void RunChaseResolve()
    {
        // A benched spectator's cooldown ends when the NEXT round's chase concludes -- i.e. now,
        // for anyone benched BEFORE this round. Tick them down; whoever hits 0 stops spectating
        // and drops back into the lobby to re-ready. Skip THIS round's fallen (their bench is set
        // below and only starts counting from the next chase).
        foreach (var player in AllPlayerStates())
        {
            if (_fallenPlayers.Contains(player) || player.BenchRoundsRemaining.Value <= 0) continue;
            player.BenchRoundsRemaining.Value--;
            if (player.BenchRoundsRemaining.Value == 0)
            {
                player.GetComponent<SpectatorController>()?.ServerSetSpectating(false);
                ReturnPlayerToLobby(player);
            }
        }

        // Return any LOBBY player who opted into spectating this chase back to normal control -- they
        // have bench 0 and aren't fallen, so neither cleanup path (bench above, fallen below) touches
        // them. No-op for the majority who never spectated.
        foreach (var player in AllPlayerStates())
        {
            if (_fallenPlayers.Contains(player) || player.BenchRoundsRemaining.Value > 0) continue;
            player.GetComponent<SpectatorController>()?.ServerSetSpectating(false);
        }

        foreach (var player in _fallenPlayers)
        {
            bool escaped = _chaseOutcomes.TryGetValue(player, out var value) && value;
            if (!escaped)
            {
                player.Points.Value = 0;
                player.SurvivalStreak.Value = 0;
                player.BenchRoundsRemaining.Value = 1;
            }

            // Returning players are NOT auto-seated -- they must walk back to the table and
            // re-ready to rejoin. Clearing ready keeps them standing (excluded from both the
            // seated set AND the next round's roster) until they interact with the ready-up.
            player.IsReady.Value = false;
            player.GetComponent<FlashlightController>()?.ServerReturnToLobby();   // recover their light for next time
        }

        DespawnStalker();
        ReturnFallenToTable();

        _fallenPlayers = new List<PlayerState>();
        _chaseOutcomes.Clear();

        // Reconcile after the chase. Returnees were un-readied above, so they stay standing
        // (excluded) until they walk back and re-ready at the table; winners keep their seats.
        TableSeatingManager.Instance?.ServerReconcileSeats();

        _phase.Value = Phase.RoundEnd;
    }

    // Called server-side when a fallen player reaches the dungeon exit (Stage 2 win
    // condition -- the real version of the debug [O] key). Marks them escaped; resolves
    // the chase once every fallen player has an outcome.
    public void ReportEscape(PlayerState player)
    {
        if (!IsServer || player == null)
            return;
        if (_phase.Value != Phase.ChaseInProgress)
            return;
        if (!_fallenPlayers.Contains(player))
            return;
        if (_chaseOutcomes.ContainsKey(player))
            return;

        _chaseOutcomes[player] = true; // escaped

        if (_chaseOutcomes.Count >= _fallenPlayers.Count)
            _phase.Value = Phase.ChaseResolve;
    }

    // Server-side. The Stalker calls this when it catches a fallen player.
    public void ReportCaught(PlayerState player)
    {
        if (!IsServer || player == null)
            return;
        if (_phase.Value != Phase.ChaseInProgress)
            return;
        if (!_fallenPlayers.Contains(player))
            return;
        if (_chaseOutcomes.ContainsKey(player))
            return;

        _chaseOutcomes[player] = false; // caught

        // Caught -> immediately become a BENCHED spectator (own 2D room, hears everything).
        player.GetComponent<SpectatorController>()?.ServerSetSpectating(true, benched: true);

        if (_chaseOutcomes.Count >= _fallenPlayers.Count)
            _phase.Value = Phase.ChaseResolve;
    }

    // Read access for the Stalker AI to target the active prey.
    public IReadOnlyList<PlayerState> FallenPlayers => _fallenPlayers;
    public bool HasChaseOutcome(PlayerState player) => _chaseOutcomes.ContainsKey(player);

    // Ready-up is only allowed between rounds (lobby / round boundaries). This keeps seated
    // players locked to their chair during a minigame or chase -- the only way to leave a chair
    // is to unready, and that's blocked here mid-round.
    public bool ReadyUpAllowed =>
        _phase.Value == Phase.Lobby || _phase.Value == Phase.RoundStart ||
        _phase.Value == Phase.RoundEnd || _phase.Value == Phase.ChaseInProgress;

    // True only while a chase is running. Seated survivors are free to leave the table during this
    // window (the minigame phases stay locked); the seating manager uses this to leave/retake a
    // single chair without re-spacing the ring.
    public bool ChaseActive => _phase.Value == Phase.ChaseInProgress;

    /// <summary>Current match phase (replicated). Read-only accessor for LOCAL visual reactors
    /// like LobbyLightingController — reacting to this on each client is local-only, no netcode.</summary>
    public Phase CurrentPhase => _phase.Value;

    /// <summary>Server: may this player use the manual lobby Spectate button right now? Only an ACTIVE
    /// (un-benched) player who is NOT in this chase, while a chase is running -- benched players already
    /// auto-spectate, and fallen players are IN the chase and must not be able to ghost out via it.</summary>
    public bool CanLobbySpectate(PlayerState p) =>
        IsServer && p != null && _phase.Value == Phase.ChaseInProgress
        && !_fallenPlayers.Contains(p) && p.IsActive;

    // Server-side. Spawns the AI Stalker into the dungeon at a random spawn for the
    // chase; despawned on resolve.
    private void SpawnStalker()
    {
        if (stalkerPrefab == null)
            return;

        var dungeon = DungeonManager.Instance;
        // Spawn far from where the players were DROPPED, and never ON one of their spawns. We use the
        // captured spawn points (not the players' transform.position, which is stale here -- the teleport
        // is an async RPC that hasn't updated the server's copy yet).
        var preyPositions = new List<Vector3>();
        foreach (var s in _preySpawns)
            if (s != null) preyPositions.Add(s.position);
        var spawn = dungeon != null ? dungeon.GetSpawnFarFrom(preyPositions, _preySpawns) : null;
        Vector3 pos = spawn != null ? spawn.position : Vector3.zero;

        var stalker = Instantiate(stalkerPrefab, pos, Quaternion.identity);
        stalker.NetworkObject.Spawn();
        _stalkerInstance = stalker.NetworkObject;
    }

    private void DespawnStalker()
    {
        if (_stalkerInstance != null && _stalkerInstance.IsSpawned)
            _stalkerInstance.Despawn();
        _stalkerInstance = null;
    }

    // Server-side. Teleports each fallen player to a random dungeon spawn via their
    // NetworkTeleporter (owner-targeted, so it sticks under client-auth transforms).
    private void DropLosersIntoDungeon()
    {
        var dungeon = DungeonManager.Instance;
        if (dungeon == null)
        {
            Debug.LogWarning("[MatchController] No DungeonManager in scene -- losers can't be dropped.");
            return;
        }

        // One DISTINCT, spread-apart spawn per faller so they land SEPARATED across the dungeon.
        var spawns = dungeon.GetDistinctSpawns(_fallenPlayers.Count);
        _preySpawns.Clear();
        for (int i = 0; i < _fallenPlayers.Count; i++)
        {
            var loser = _fallenPlayers[i];
            var spawn = spawns.Count > 0 ? spawns[i % spawns.Count] : null;   // reuse only if too few points
            var tp = loser != null ? loser.GetComponent<NetworkTeleporter>() : null;
            if (spawn != null && tp != null)
            {
                if (!_preySpawns.Contains(spawn)) _preySpawns.Add(spawn);   // remember it for the Stalker
                if (debugChaseLogs)
                    Debug.Log($"[CHASE] DROP client {loser.OwnerClientId} -> dungeon {spawn.position}");
                tp.TeleportRpc(spawn.position, spawn.rotation);
            }
            else
            {
                Debug.LogWarning($"[CHASE] DROP skipped: spawn={(spawn == null ? "NULL" : "ok")}, teleporter={(tp == null ? "NULL" : "ok")}");
            }
        }
    }

    // Server-side. On chase resolve, send the fallen back to a lobby spawn point so the
    // loop closes (escape and caught both return to the table; caught just got benched
    // above). Escape's own exit-driven return comes in Stage 2.
    private void ReturnFallenToTable()
    {
        if (debugChaseLogs)
            Debug.Log($"[CHASE] RETURN-TO-TABLE -- {_fallenPlayers.Count} players.");
        var points = FindObjectsByType<SpawnPoint>(FindObjectsSortMode.None);
        if (points.Length == 0)
            return;

        // The returning group rises from the dungeon → elevator arrives from BELOW (null-safe: works
        // with or without the elevator wired). One arrival for the whole group.
        ElevatorController.Instance?.ServerArrive(false);

        foreach (var player in _fallenPlayers)
        {
            var tp = player != null ? player.GetComponent<NetworkTeleporter>() : null;
            if (tp == null)
                continue;
            var p = points[Random.Range(0, points.Length)].transform;
            tp.TeleportRpc(p.position, p.rotation);
        }
    }

    // Server: send one player to a random lobby spawn (used when a spectator's bench ends).
    private void ReturnPlayerToLobby(PlayerState player)
    {
        if (player == null) return;
        var points = FindObjectsByType<SpawnPoint>(FindObjectsSortMode.None);
        if (points.Length == 0) return;
        var tp = player.GetComponent<NetworkTeleporter>();
        if (tp == null) return;
        var p = points[Random.Range(0, points.Length)].transform;
        tp.TeleportRpc(p.position, p.rotation);

        // Rises from below in the elevator (null-safe if no ElevatorController).
        ElevatorController.Instance?.ServerArrive(false);

        // A benched player returning to the lobby (elevator) wakes up. Owner-targeted so ONLY this
        // return triggers it -- dungeon teleports share NetworkTeleporter and must not.
        player.GetComponent<ScreenFeedback>()?.PlayWakeUpRpc();
    }

    private void RunRoundEnd()
    {
        _roundNumber.Value++;
        _phase.Value = _roundNumber.Value >= roundCount ? Phase.GameOver : Phase.RoundStart;
    }

    private List<PlayerState> AllPlayerStates()
    {
        var result = new List<PlayerState>();
        if (NetworkManager.Singleton == null)
            return result;
        foreach (var client in NetworkManager.Singleton.ConnectedClientsList)
        {
            var state = client.PlayerObject != null ? client.PlayerObject.GetComponent<PlayerState>() : null;
            if (state != null)
                result.Add(state);
        }
        return result;
    }

    // ---- Debug-only chase resolution control (stub for real chase mechanics) --
    // Host-only, plain OnGUI, same minimal style as DevConnectUi.cs. Lets you
    // manually resolve each fallen player as Escaped/Caught so the rest of the
    // loop (bench, streak reset, point zeroing) can be proven without a real
    // dungeon/monster existing yet.
    //
    // Cursor handling lives at the two transition points (RunGameResolve when
    // entering ChaseInProgress; the Resolve-chase click below when leaving it)
    // instead of here, so it's a one-shot write rather than an every-frame one
    // -- see NumberPickMiniGame's _cursorFreedLocally comment for why.
    void OnGUI()
    {
        // NetworkVariables can't be read before the object spawns -- on a client
        // OnGUI runs before that, so bail until spawned. This also fixes the lobby
        // readout silently failing for clients.
        if (!IsSpawned)
            return;

        // Temporary always-on readout (TODO remove once the phase machine is
        // confirmed working) -- _phase/_roundNumber are private fields with no
        // [SerializeField], so they don't show in the Inspector at all; this
        // sidesteps that and works for every connected client too, since both
        // NetworkVariables have ReadPermission.Everyone.
        // TOP-RIGHT corner (transport/connect UI owns top-left). Anchored off Screen.width
        // so it stays in the corner at any resolution.
        GUILayout.BeginArea(new Rect(Screen.width - 410, 10, 400, 40));
        GUILayout.Label($"[DEBUG] Phase: {_phase.Value} | Round: {_roundNumber.Value + 1}/{roundCount}");
        GUILayout.EndArea();

        // ---- Lobby readout: who has readied up. Ready-up itself is now a world
        // interaction (ReadyUpInteractable), so this is display-only. FindObjectsByType
        // is used (not AllPlayerStates) because ConnectedClientsList is only fully
        // populated server-side, whereas player objects are spawned on every peer.
        if (_phase.Value == Phase.Lobby)
        {
            var states = FindObjectsByType<PlayerState>(FindObjectsSortMode.None);
            int readyCount = states.Count(s => s.IsReady.Value);

            // Also top-right, stacked under the phase readout.
            GUILayout.BeginArea(new Rect(Screen.width - 370, 60, 360, 260));
            GUILayout.Label($"[LOBBY] {readyCount}/{states.Length} ready (need {activeFloorMinimum}+ players)");
            foreach (var s in states)
                GUILayout.Label($"Client {s.OwnerClientId}: {(s.IsReady.Value ? "READY" : "waiting")}");

            if (IsServer && holdForManualStart &&
                states.Length >= activeFloorMinimum && readyCount == states.Length)
                GUILayout.Label($"All ready -- press [{startMatchKey}] to start the match.");

            GUILayout.EndArea();
        }
    }
}
