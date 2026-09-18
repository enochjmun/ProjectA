using System.Collections.Generic;
using Unity.Netcode;
using UnityEngine;

/// <summary>
/// Replicated open/closed state for every door in the current dungeon.
///
/// WHY A CENTRAL REGISTRY INSTEAD OF NETWORKING EACH DOOR: the dungeon is LOCAL-DETERMINISTIC -- every
/// client rebuilds identical geometry from the seed, and only ~2 NetworkObjects exist in the whole
/// level (see DungeonGenerator). Making each door a NetworkObject would spawn dozens per round and
/// throw that model away. Instead doors are plain MonoBehaviours that register here for an id, and
/// only the small state list replicates. Same pattern as DungeonObjective's ClearedIds.
///
/// IDS COME FROM REGISTRATION ORDER, and that's safe for the same reason the geometry is: every client
/// generates the same rooms in the same order from the same seed, so door N is the same door
/// everywhere. If generation ever becomes non-deterministic, this breaks with it -- but so does the
/// entire dungeon, so there's no new assumption here.
///
/// SETUP: put this on the SAME GameObject as DungeonGenerator. It needs that object's NetworkObject;
/// several NetworkBehaviours on one NetworkObject is fine and keeps the count unchanged.
/// </summary>
public class DungeonDoors : NetworkBehaviour
{
    public static DungeonDoors Instance { get; private set; }

    /// <summary>
    /// Server-write, everyone-read. Indexed by door id. NOT a bool: doors are double-acting, so the
    /// state has to carry WHICH WAY a door was pushed as well as whether it's open.
    ///   0  = closed
    ///  +1  = open, swung positive
    ///  -1  = open, swung negative
    /// The direction must replicate, or clients that didn't do the pushing would animate the leaf
    /// swinging through the pusher.
    /// </summary>
    private readonly NetworkList<sbyte> _state = new NetworkList<sbyte>();

    /// <summary>Local instances, in registration order. Rebuilt on every generate, not replicated.</summary>
    private readonly List<DungeonDoor> _doors = new List<DungeonDoor>();

    private void Awake() => Instance = this;

    public override void OnNetworkSpawn()
    {
        // A late joiner receives the whole list at once and then deltas. Re-applying on every change is
        // cheap (doors are few, changes are rare) and means a client that joins mid-round sees doors in
        // the state everyone else left them, rather than all closed.
        _state.OnListChanged += OnStateListChanged;
        PushStateToDoors();
    }

    public override void OnNetworkDespawn() => _state.OnListChanged -= OnStateListChanged;

    // Named, not a lambda: `-= _ => Foo()` creates a NEW delegate and removes nothing, so the handler
    // would leak across despawns and fire on a dead object.
    private void OnStateListChanged(NetworkListEvent<sbyte> _) => PushStateToDoors();

    public override void OnDestroy()
    {
        if (Instance == this) Instance = null;
        base.OnDestroy();
    }

    /// <summary>
    /// Called by DungeonGenerator at the START of a generate, before any room is instantiated. Clears
    /// the local list everywhere; the server also clears replicated state so ids start from 0 again.
    /// Without this, ids keep climbing across regens and desync from the freshly-built doors.
    /// </summary>
    public void ResetForGenerate()
    {
        _doors.Clear();
        if (IsServer && IsSpawned) _state.Clear();
    }

    /// <summary>Door asks for its id as it spawns. Server grows the replicated list to match.</summary>
    public int Register(DungeonDoor door)
    {
        int id = _doors.Count;
        _doors.Add(door);

        // Only the server may write a NetworkList. Clients just track locally and read state that
        // arrives from the server -- their list is already the right length by the time it matters.
        if (IsServer && IsSpawned)
            while (_state.Count <= id) _state.Add(0);

        return id;
    }

    /// <summary>Client-safe: 0 = closed, +/-1 = open in that direction. 0 before state replicates.</summary>
    public sbyte StateOf(int id) => (IsSpawned && id >= 0 && id < _state.Count) ? _state[id] : (sbyte)0;

    /// <summary>
    /// Owner asks, server decides. `dir` is which way the pusher wants it to swing (+1/-1), computed
    /// on the client from which side of the leaf they're standing. Ignored when closing -- a door only
    /// needs a direction on the way open, and it returns to the same closed position either way.
    /// Server-authoritative so two players pushing on the same frame can't desync it.
    /// </summary>
    [Rpc(SendTo.Server)]
    public void RequestToggleRpc(int id, sbyte dir)
    {
        if (id < 0 || id >= _state.Count) return;
        _state[id] = _state[id] != 0 ? (sbyte)0 : (dir >= 0 ? (sbyte)1 : (sbyte)-1);
    }

    private void PushStateToDoors()
    {
        for (int i = 0; i < _doors.Count; i++)
            if (_doors[i] != null) _doors[i].SetState(StateOf(i));
    }
}
