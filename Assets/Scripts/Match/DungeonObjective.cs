using System.Collections.Generic;
using Unity.Netcode;
using UnityEngine;

/// <summary>
/// A single server-authoritative "objective complete" flag for the dungeon. The escape
/// exit stays LOCKED until IsComplete is true. Kept as a plain boolean on purpose -- the
/// *what* (a lever, a switch, an item pickup, reaching a room...) varies per map, but the
/// gate is uniform: whatever your map uses as its objective calls ServerComplete().
/// MatchController relocks it (ServerReset) at the start of each chase.
///
/// In-scene NetworkObject -- place one in the dungeon. While testing, before real
/// objective triggers exist, the host can press the debug key to satisfy it.
/// </summary>
[RequireComponent(typeof(NetworkObject))]
public class DungeonObjective : NetworkBehaviour
{
    [Tooltip("Host-only test key to mark the objective complete (stand-in for a real trigger).")]
    [SerializeField] private KeyCode debugCompleteKey = KeyCode.K;

    public static DungeonObjective Instance { get; private set; }

    /// <summary>Server-write. False = exits locked; true = exits open. Everyone reads it.</summary>
    public readonly NetworkVariable<bool> IsComplete = new NetworkVariable<bool>(
        false, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);

    /// <summary>Replicated so every client can show progress and light up cleared markers. How many
    /// objectives must be cleared (set by the generator).</summary>
    public readonly NetworkVariable<int> ObjectiveCount = new NetworkVariable<int>(
        0, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);

    /// <summary>Replicated set of objective ids already cleared. Markers read this to turn green; the
    /// HUD reads its Count. Server-write, everyone-read (NetworkList defaults).</summary>
    public readonly NetworkList<int> ClearedIds = new NetworkList<int>();

    /// <summary>Client-safe: has objective `id` been cleared? Guards on IsSpawned so it's safe to poll
    /// before the object exists on a client.</summary>
    public bool IsCleared(int id) => IsSpawned && ClearedIds.Contains(id);

    // Server-only run state. The generator configures the count; players clear ids co-operatively;
    // exits are claimed once each. None of this needs to replicate -- only IsComplete does.
    private int _objectiveCount;
    private readonly HashSet<int> _cleared = new HashSet<int>();
    private readonly HashSet<int> _usedExits = new HashSet<int>();

    private void Awake() => Instance = this;

    public override void OnDestroy()
    {
        if (Instance == this) Instance = null;
        base.OnDestroy();
    }

    /// <summary>Server: the generator tells us how many objective triggers must be cleared to unlock.</summary>
    public void ServerConfigure(int objectiveCount)
    {
        if (!IsServer) return;
        _objectiveCount = objectiveCount;
        _cleared.Clear();
        _usedExits.Clear();
        ObjectiveCount.Value = objectiveCount;
        ClearedIds.Clear();
        IsComplete.Value = false;
    }

    /// <summary>Server: a player cleared objective trigger `id`. Unlocks exits once ALL are cleared.</summary>
    public void ServerClearObjective(int id)
    {
        if (!IsServer) return;
        if (_cleared.Add(id))          // newly cleared this call
            ClearedIds.Add(id);        // mirror to the replicated list (markers + HUD read it)
        if (_objectiveCount > 0 && _cleared.Count >= _objectiveCount)
            IsComplete.Value = true;   // team unlock
    }

    /// <summary>Server: claim exit `id` for a player. Returns false if exits are locked (objective not
    /// done) or this exit was already used -- single-use, first-come. Atomic on the server.</summary>
    public bool ServerTryClaimExit(int id)
    {
        if (!IsServer || !IsComplete.Value) return false;   // still locked
        if (!_usedExits.Add(id)) return false;              // already used by someone
        return true;
    }

    /// <summary>Server: force the objective done (debug / stand-in trigger).</summary>
    public void ServerComplete() { if (IsServer) IsComplete.Value = true; }

    /// <summary>Server: relock and clear all progress for a new chase (keeps the configured count).</summary>
    public void ServerReset()
    {
        if (!IsServer) return;
        _cleared.Clear();
        _usedExits.Clear();
        ClearedIds.Clear();
        IsComplete.Value = false;
    }

    private void Update()
    {
        if (IsServer && Input.GetKeyDown(debugCompleteKey))
            ServerComplete();
    }
}
