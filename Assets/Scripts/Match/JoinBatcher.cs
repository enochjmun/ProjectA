using System.Collections;
using System.Collections.Generic;
using Unity.Netcode;
using UnityEngine;

/// <summary>
/// Grouped join spawning via the elevator. As players connect they're BOARDED onto the elevator car
/// (up to `maxPerWave` = 4 rider slots), where they wake up top. On the batch timer the car descends
/// with the wave and the doors open into the lobby; riders walk out. Overflow (a 5th+ player joining
/// within a window) is held until a slot frees on the next wave.
///
/// Server-only logic (scene NetworkBehaviour; clients keep it so PlayerSpawnPositioner can detect that
/// batched spawning is on).
///
/// REQUIRES: an ElevatorController in the scene with its rider anchors assigned, and an ElevatorRider on
/// the Player prefab. If those aren't wired, boarded players can't be positioned — remove this component
/// to fall back to PlayerSpawnPositioner's plain spawn.
/// </summary>
[RequireComponent(typeof(NetworkObject))]
public class JoinBatcher : NetworkBehaviour
{
    [Tooltip("Window between elevator departures. Players who join within one window ride together.")]
    [SerializeField] private float batchInterval = 4f;
    [Tooltip("Max riders per trip (the car's rider-slot count caps this too).")]
    [SerializeField] private int maxPerWave = 4;
    [Tooltip("Beat the car holds up top after the window, so riders wake before it moves.")]
    [SerializeField] private float boardSettle = 1.5f;

    private readonly List<ulong> _boarded = new List<ulong>();   // currently on the car
    private readonly List<ulong> _queue = new List<ulong>();     // overflow waiting for a slot
    private bool _descending;

    public override void OnNetworkSpawn()
    {
        if (!IsServer) return;
        NetworkManager.OnClientConnectedCallback += OnConnect;
        NetworkManager.OnClientDisconnectCallback += OnDisconnect;
        foreach (var id in NetworkManager.ConnectedClientsIds) OnConnect(id);
        StartCoroutine(WaveLoop());
    }

    public override void OnNetworkDespawn()
    {
        if (NetworkManager.Singleton != null)
        {
            NetworkManager.Singleton.OnClientConnectedCallback -= OnConnect;
            NetworkManager.Singleton.OnClientDisconnectCallback -= OnDisconnect;
        }
    }

    private int Capacity()
    {
        var e = ElevatorController.Instance;
        int slots = e != null ? e.RiderSlots : 0;
        return slots > 0 ? Mathf.Min(maxPerWave, slots) : maxPerWave;
    }

    private void OnConnect(ulong id) { StartCoroutine(BoardWhenReady(id)); }

    private void OnDisconnect(ulong id) { _boarded.Remove(id); _queue.Remove(id); }

    // Wait for the player object to exist, then board onto the car if a slot's free, else queue.
    private IEnumerator BoardWhenReady(ulong id)
    {
        float t = 0f;
        NetworkObject po = null;
        while (t < 5f)
        {
            if (NetworkManager.ConnectedClients.TryGetValue(id, out var cc) && cc.PlayerObject != null) { po = cc.PlayerObject; break; }
            t += Time.deltaTime;
            yield return null;
        }
        if (po == null || _boarded.Contains(id) || _queue.Contains(id)) yield break;

        if (!_descending && _boarded.Count < Capacity())
            Board(id, po);
        else
            _queue.Add(id);
    }

    private void Board(ulong id, NetworkObject po)
    {
        int index = _boarded.Count;
        _boarded.Add(id);

        var rider = po.GetComponent<ElevatorRider>();
        if (rider == null)
        {
            Debug.LogWarning($"[JoinBatcher] Client {id} has NO ElevatorRider component — can't board. " +
                             "Add ElevatorRider to the Player prefab.");
            return;
        }
        var e = ElevatorController.Instance;
        if (e == null || e.RiderSlots == 0)
        {
            Debug.LogWarning($"[JoinBatcher] No ElevatorController with rider anchors in the scene — can't " +
                             $"seat rider {index}. Add the ElevatorController and assign its Rider Anchors.");
            return;
        }
        Debug.Log($"[JoinBatcher] Boarded client {id} into car slot {index}.");
        rider.ServerBoard(index);
    }

    private IEnumerator WaveLoop()
    {
        while (true)
        {
            yield return new WaitForSeconds(batchInterval);

            var e = ElevatorController.Instance;
            if (_descending || _boarded.Count == 0) continue;
            if (e != null && e.IsBusy) continue;   // elevator mid-cycle → wait for the next window

            _descending = true;
            yield return new WaitForSeconds(boardSettle);   // riders wake up top before it moves

            e?.ServerArrive(true);   // descend with the wave (from above)

            // Wait out the full arrival cycle, then let the riders off (frees the car for the next wave).
            yield return new WaitUntil(() => ElevatorController.Instance == null || !ElevatorController.Instance.IsBusy);
            foreach (var id in _boarded)
                if (NetworkManager.ConnectedClients.TryGetValue(id, out var cc) && cc.PlayerObject != null)
                    cc.PlayerObject.GetComponent<ElevatorRider>()?.ServerRelease();
            _boarded.Clear();
            _descending = false;

            // Board any overflow for the next trip.
            while (_queue.Count > 0 && _boarded.Count < Capacity())
            {
                ulong id = _queue[0];
                _queue.RemoveAt(0);
                if (NetworkManager.ConnectedClients.TryGetValue(id, out var cc) && cc.PlayerObject != null)
                    Board(id, cc.PlayerObject);
            }
        }
    }
}
