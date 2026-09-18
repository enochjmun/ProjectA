using System.Collections;
using System.Collections.Generic;
using System.Linq;
using Unity.Netcode;
using UnityEngine;

/// <summary>
/// Server-only seating. Seats are spaced EVENLY by the number of seated players (seat i at
/// 360/N * i) and re-space smoothly while people ready up. Crucially, re-spacing ONLY happens
/// in ServerReconcileSeats(), which runs on ready-changes and the round-start re-ready -- never
/// during a minigame (no ready-changes happen then), and ServerSnapSeats() settles any slide
/// the instant a minigame starts. A dropped chair frees its spot WITHOUT re-spacing, so the
/// remaining players stay put during the chase and only re-space at the next ready-up.
///
/// "Should be seated" = present clients that are ready, active (not benched), and not down in
/// the dungeon.
/// </summary>
[RequireComponent(typeof(NetworkObject))]
public class TableSeatingManager : NetworkBehaviour
{
    [Header("Refs")]
    [SerializeField] private GameObject seatStationPrefab;
    [Tooltip("Ring centre. Defaults to this object's transform if left empty.")]
    [SerializeField] private Transform tableCenter;

    [Header("Ring")]
    [SerializeField] private float seatRadius = 2.5f;
    [Tooltip("How fast seats slide to their new even spots while the ring re-spaces (ready-up only).")]
    [SerializeField] private float ringLerpSpeed = 4f;
    [Tooltip("Angle (degrees) of the first seat, so the ring has a consistent orientation.")]
    [SerializeField] private float firstSeatAngleDeg = 0f;

    public static TableSeatingManager Instance { get; private set; }

    private readonly List<SeatStation> _seats = new List<SeatStation>();
    private readonly Dictionary<ulong, SeatStation> _seatByClient = new Dictionary<ulong, SeatStation>();
    private readonly List<ulong> _order = new List<ulong>();   // stable ring order of seated clients
    private readonly Dictionary<SeatStation, Vector3> _targetPos = new Dictionary<SeatStation, Vector3>();
    private readonly Dictionary<SeatStation, Quaternion> _targetRot = new Dictionary<SeatStation, Quaternion>();
    private readonly Dictionary<ulong, SeatStation> _falling = new Dictionary<ulong, SeatStation>();

    private Transform Center => tableCenter != null ? tableCenter : transform;

    private void Awake() => Instance = this;

    public override void OnDestroy()
    {
        if (Instance == this) Instance = null;
        base.OnDestroy();
    }

    public override void OnNetworkSpawn()
    {
        if (!IsServer) { enabled = false; return; }

        NetworkManager.OnClientConnectedCallback += HandleClientConnected;
        NetworkManager.OnClientDisconnectCallback += HandleClientDisconnected;

        foreach (var id in NetworkManager.ConnectedClientsIds)
            StartCoroutine(BindWhenReady(id));
    }

    public override void OnNetworkDespawn()
    {
        if (NetworkManager.Singleton != null)
        {
            NetworkManager.Singleton.OnClientConnectedCallback -= HandleClientConnected;
            NetworkManager.Singleton.OnClientDisconnectCallback -= HandleClientDisconnected;
        }
    }

    private void HandleClientConnected(ulong clientId) => StartCoroutine(BindWhenReady(clientId));
    private void HandleClientDisconnected(ulong clientId) => ServerReconcileSeats();

    private IEnumerator BindWhenReady(ulong clientId)
    {
        PlayerState ps = null;
        float t = 0f;
        while (ps == null && t < 5f)
        {
            if (NetworkManager.ConnectedClients.TryGetValue(clientId, out var cc) && cc.PlayerObject != null)
                ps = cc.PlayerObject.GetComponent<PlayerState>();
            if (ps != null) break;
            t += Time.deltaTime;
            yield return null;
        }

        if (ps == null)
        {
            Debug.LogWarning($"[TableSeatingManager] No PlayerState for client {clientId}; not binding.", this);
            yield break;
        }

        ps.IsReady.OnValueChanged += (_, now) => OnReadyChanged(ps, now);
        ServerReconcileSeats();
    }

    // Ready-change router. Between rounds we do a full re-space. DURING THE CHASE we never touch the
    // ring -- a watching survivor may leave (unready) or retake (re-ready) their OWN chair while
    // every other chair stays exactly where it is (no re-space, no despawn).
    private void OnReadyChanged(PlayerState ps, bool nowReady)
    {
        if (!IsServer) return;
        var mc = MatchController.Instance;
        if (mc != null && mc.ChaseActive)
        {
            if (nowReady) ServerSitInEmptySeat(ps.OwnerClientId);
            else ServerLeaveSeat(ps.OwnerClientId);
            return;
        }
        ServerReconcileSeats();
    }

    // Chase-only: stand the player up and leave their chair EMPTY in place (no re-space, no despawn).
    private void ServerLeaveSeat(ulong clientId)
    {
        if (!_seatByClient.TryGetValue(clientId, out var seat) || seat == null) return;
        GetOccupant(clientId)?.ServerRelease();            // stand up, movement unlocked
        _seatByClient.Remove(clientId);
        _order.Remove(clientId);
        // seat stays in _seats at its pose -> the chair doesn't move or vanish during the chase
    }

    // Chase-only: sit the player back into an existing empty chair at its current pose (no re-space).
    private void ServerSitInEmptySeat(ulong clientId)
    {
        if (_seatByClient.ContainsKey(clientId)) return;   // already seated
        var seat = FirstEmptySeat();
        if (seat == null) return;                          // no free chair -> wait for the next ready-up
        _seatByClient[clientId] = seat;
        if (!_order.Contains(clientId)) _order.Add(clientId);
        GetOccupant(clientId)?.ServerSeat(seat);
    }

    /// <summary>Server: seat exactly the seated players, spaced evenly by count. Re-spaces smoothly
    /// (via the Update lerp). Only called during ready-up / re-ready, never mid-minigame.</summary>
    public void ServerReconcileSeats()
    {
        if (!IsServer) return;

        // Last round's fallen-player markers: their open trapdoors persisted through the chase as
        // gravestones (survivors held their seats, the holes stayed open to mark who went down). A
        // new ready-up resets the table, so despawn them now -- this is where the markers clear.
        foreach (var kv in _falling.ToList())
            if (kv.Value != null && kv.Value.NetworkObject.IsSpawned)
                kv.Value.NetworkObject.Despawn();
        _falling.Clear();

        var target = TargetSeatedClients();

        // Free non-target occupants (unready / benched / fallen / gone).
        foreach (var id in _seatByClient.Keys.ToList())
        {
            if (target.Contains(id)) continue;
            GetOccupant(id)?.ServerRelease();
            _seatByClient.Remove(id);
        }

        // Stable order: keep existing, drop gone, append newly-seated.
        _order.RemoveAll(id => !target.Contains(id));
        foreach (var id in target)
            if (!_order.Contains(id)) _order.Add(id);

        // Give each seated client a seat and its EVEN target pose (i of N around the ring).
        int n = _order.Count;
        for (int i = 0; i < n; i++)
        {
            ulong id = _order[i];
            ComputeSlot(i, n, out var pos, out var rot);

            if (!_seatByClient.TryGetValue(id, out var seat) || seat == null)
            {
                seat = FirstEmptySeat();
                if (seat == null)
                {
                    seat = SpawnStation(pos, rot);   // spawn already at the slot -> correct facing
                    if (seat == null) continue;
                    _seats.Add(seat);
                }
                _seatByClient[id] = seat;
                GetOccupant(id)?.ServerSeat(seat);
            }

            _targetPos[seat] = pos;
            _targetRot[seat] = rot;
        }

        // Despawn any leftover empty seats. This is the ONLY place seats are despawned during
        // normal play, and it only runs during ready-up -- so nothing vanishes mid-minigame.
        foreach (var seat in _seats.ToList())
        {
            if (seat == null) { _seats.Remove(seat); continue; }
            if (_seatByClient.ContainsValue(seat)) continue;
            _seats.Remove(seat);
            _targetPos.Remove(seat);
            _targetRot.Remove(seat);
            if (seat.NetworkObject.IsSpawned) seat.NetworkObject.Despawn();
        }
    }

    /// <summary>Server: finish any in-progress re-space instantly. Called right before a minigame
    /// so chairs are settled and don't keep sliding once play begins.</summary>
    public void ServerSnapSeats()
    {
        if (!IsServer) return;
        foreach (var kv in _seatByClient)
        {
            var seat = kv.Value;
            if (seat == null) continue;
            if (_targetPos.TryGetValue(seat, out var tp)) seat.transform.position = tp;
            if (_targetRot.TryGetValue(seat, out var tr)) seat.transform.rotation = tr;
        }
    }

    private HashSet<ulong> TargetSeatedClients()
    {
        var set = new HashSet<ulong>();
        if (NetworkManager.Singleton == null) return set;

        var mc = MatchController.Instance;
        foreach (var client in NetworkManager.Singleton.ConnectedClientsList)
        {
            var ps = client.PlayerObject != null ? client.PlayerObject.GetComponent<PlayerState>() : null;
            if (ps == null) continue;
            if (!ps.IsReady.Value) continue;
            if (!ps.IsActive) continue;                                 // benched -> spectating
            if (mc != null && mc.FallenPlayers.Contains(ps)) continue;  // in the dungeon
            set.Add(client.ClientId);
        }
        return set;
    }

    private SeatStation FirstEmptySeat()
    {
        foreach (var s in _seats)
            if (s != null && !_seatByClient.ContainsValue(s))
                return s;
        return null;
    }

    [Tooltip("Yaw correction (deg) applied AFTER facing the table — fixes a model whose forward " +
             "axis isn't +Z. If stations face sideways, try 90 or -90. 0 = no correction. Tunable " +
             "live in play.")]
    [SerializeField] private float seatYawOffsetDeg = 0f;

    // Even placement: seat i of n at angle firstSeatAngleDeg + 360/n * i, facing the centre.
    private void ComputeSlot(int i, int n, out Vector3 pos, out Quaternion rot)
    {
        float ang = (firstSeatAngleDeg + 360f * i / Mathf.Max(1, n)) * Mathf.Deg2Rad;
        Vector3 offset = new Vector3(Mathf.Cos(ang), 0f, Mathf.Sin(ang)) * seatRadius;
        pos = Center.position + offset;

        Vector3 toCenter = Center.position - pos;
        toCenter.y = 0f;
        rot = toCenter.sqrMagnitude > 0.0001f
            ? Quaternion.LookRotation(toCenter.normalized, Vector3.up)
            : Center.rotation;
        rot *= Quaternion.Euler(0f, seatYawOffsetDeg, 0f);   // correct a non-+Z model forward
    }

    private SeatStation SpawnStation(Vector3 pos, Quaternion rot)
    {
        if (seatStationPrefab == null)
        {
            Debug.LogError("[TableSeatingManager] seatStationPrefab not assigned.", this);
            return null;
        }
        var go = Instantiate(seatStationPrefab, pos, rot);
        go.GetComponent<NetworkObject>().Spawn();
        return go.GetComponent<SeatStation>();
    }

    // ---- Drop (called by MatchController). Frees the spot; everyone else stays put. ----

    public void ServerDrop(ulong clientId)
    {
        if (!IsServer) return;
        if (!_seatByClient.TryGetValue(clientId, out var seat) || seat == null) return;

        seat.ServerBeginFall();            // occupant rides it down
        _seatByClient.Remove(clientId);
        _seats.Remove(seat);
        _order.Remove(clientId);
        _targetPos.Remove(seat);
        _targetRot.Remove(seat);
        _falling[clientId] = seat;
        // NO re-space here -- remaining players stay put during the chase and only re-space at
        // the next ready-up (ServerReconcileSeats).
    }

    /// <summary>Server: despawn a dropped (fallen) chair after the player is teleported out.</summary>
    public void ServerDespawnStation(ulong clientId)
    {
        if (!IsServer) return;
        if (_falling.TryGetValue(clientId, out var seat) && seat != null && seat.NetworkObject.IsSpawned)
            seat.NetworkObject.Despawn();
        _falling.Remove(clientId);
    }

    /// <summary>Server: the station a just-dropped (falling) client is riding down, or null.</summary>
    public SeatStation FallingStationOf(ulong clientId)
        => _falling.TryGetValue(clientId, out var seat) ? seat : null;

    private void Update()
    {
        if (!IsServer) return;

        // Slide seats to their even targets during a re-space, but SETTLE exactly once there. A bare
        // Vector3.Lerp toward a fixed target never actually arrives -- it creeps by a hair every frame
        // forever. Because the station is a server-auth, interpolated NetworkTransform, that perpetual
        // micro-motion replicates and (on clients) interpolates into a fine jitter that the seated player
        // -- glued to the seat anchor -- rides. Snapping within an epsilon stops the creep so the chair,
        // and the body on it, goes truly still between re-spaces.
        const float posEpsSqr = 1e-6f;   // ~1mm
        const float rotEps = 0.02f;      // degrees
        float dt = Time.deltaTime;
        foreach (var kv in _seatByClient)
        {
            var seat = kv.Value;
            if (seat == null) continue;
            if (_targetPos.TryGetValue(seat, out var tp))
            {
                var cur = seat.transform.position;
                seat.transform.position = (cur - tp).sqrMagnitude <= posEpsSqr
                    ? tp : Vector3.Lerp(cur, tp, ringLerpSpeed * dt);
            }
            if (_targetRot.TryGetValue(seat, out var tr))
            {
                var cur = seat.transform.rotation;
                seat.transform.rotation = Quaternion.Angle(cur, tr) <= rotEps
                    ? tr : Quaternion.Slerp(cur, tr, ringLerpSpeed * dt);
            }
        }
    }

    private SeatOccupant GetOccupant(ulong clientId)
    {
        if (NetworkManager.Singleton != null &&
            NetworkManager.Singleton.ConnectedClients.TryGetValue(clientId, out var cc) && cc.PlayerObject != null)
            return cc.PlayerObject.GetComponent<SeatOccupant>();
        return null;
    }
}
