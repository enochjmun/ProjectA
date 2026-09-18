using System.Collections;
using System.Linq;
using Unity.Netcode;
using Unity.Netcode.Components;
using UnityEngine;

/// <summary>
/// On the Player. Makes a player RIDE the elevator: when the batcher/MatchController boards them, the
/// owner glues to an interior anchor (a child of the moving car), movement locked, and wakes up inside
/// the car; when the gate opens at the destination they unlock and walk out.
///
/// It's the SeatOccupant glue pattern applied to the elevator: the anchor is one of ElevatorController's
/// rider slots, referenced by INDEX (the anchors are scene children of the car, not NetworkObjects, and
/// the elevator is deterministic on every client, so an int index replicates cleanly).
///
/// Stage 1 = glue-locked ride (reliable). Free walk-around-the-cage (moving-platform carry) is a Stage-2
/// swap — this component stays the integration point for it.
///
/// SETUP: add to the Player prefab (no serialized refs). Needs an ElevatorController in the scene with
/// its rider anchors assigned; harmless if there isn't one (just never rides).
/// </summary>
[RequireComponent(typeof(NetworkObject))]
public class ElevatorRider : NetworkBehaviour
{
    [Header("Camera feel while riding (a single bump per lurch — the descent itself is felt from riding)")]
    [Tooltip("Car acceleration (m/s^2) above which a lurch registers and fires ONE shake pulse.")]
    [SerializeField] private float jerkThreshold = 6f;
    [Tooltip("Size of that one shake pulse (0..1 trauma). Small — the world already moves as you ride.")]
    [SerializeField] private float jerkShakeAmount = 0.08f;
    [Tooltip("Minimum seconds between shake pulses, so a lurch can't stack into a saturated blur.")]
    [SerializeField] private float shakeMinInterval = 0.4f;
    [Tooltip("Strength (0..1) of the continuous travel rumble at full car speed. This is the steady " +
             "engine vibration under the ride — separate from the lurch bumps above.")]
    [Range(0f, 1f)] [SerializeField] private float travelRumbleAmount = 0.55f;
    [Tooltip("Car speed (m/s) at which the rumble reaches full strength.")]
    [SerializeField] private float travelRumbleRefSpeed = 3f;

    [Header("Force-eject (don't let a dawdler get carried off)")]
    [Tooltip("If the gate closes while the rider is still within this distance of their car anchor, the " +
             "elevator shoves them toward the gate — and fade-teleports them out if the shove doesn't clear it.")]
    [SerializeField] private float ejectRadius = 2f;
    [Tooltip("Speed (m/s) of the shove toward the gate when the doors close on a dawdler. It decays via " +
             "PlayerMovement's external damping, so this is the initial kick, not a sustained push.")]
    [SerializeField] private float shoveSpeed = 6f;
    [Tooltip("Seconds the shove is given to clear the car before the fade-covered teleport backstop fires.")]
    [SerializeField] private float shoveGrace = 0.6f;

    // Server-set rider slot: -1 = not riding, else an index into ElevatorController.RiderAnchors.
    private readonly NetworkVariable<int> _slot = new NetworkVariable<int>(
        -1, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);

    private CharacterController _cc;
    private PlayerMovement _movement;
    private CameraFeel _cameraFeel;
    private ScreenFeedback _screenFeedback;
    private bool _ridingLocal;
    private bool _disembarked;     // latched once the doors open, so a later gate-close can't re-glue us
    private bool _wasDoorsOpen;    // edge-detect the gate closing (open -> shut) to force-eject dawdlers
    private bool _forceEjected;    // teleported out once — don't repeat
    private float _lastCarSpeed;
    private float _shakeCooldown;

    public bool IsRiding => _slot.Value >= 0;
    /// <summary>Owner-side: we've actually started gluing to a real anchor (BeginRide ran). Distinct from
    /// IsRiding, which is just "a slot was assigned" — that can be true with a missing/unwired anchor.</summary>
    public bool ActivelyRiding => _ridingLocal;

    private void Awake()
    {
        _cc = GetComponent<CharacterController>();
        _movement = GetComponent<PlayerMovement>();
        _cameraFeel = GetComponent<CameraFeel>();
        _screenFeedback = GetComponent<ScreenFeedback>();
    }

    /// <summary>Server: board this player into elevator rider slot `index`.</summary>
    public void ServerBoard(int index) { if (IsServer) _slot.Value = index; }

    /// <summary>Server: take this player off the elevator (frees the slot).</summary>
    public void ServerRelease() { if (IsServer) _slot.Value = -1; }

    private void Update()
    {
        if (!IsOwner) return;

        var elevator = ElevatorController.Instance;
        int slot = _slot.Value;

        if (slot < 0 || elevator == null)
        {
            if (_ridingLocal) EndRide();
            _disembarked = false;
            _wasDoorsOpen = false;
            _forceEjected = false;
            return;
        }

        var anchor = elevator.RiderAnchor(slot);
        if (anchor == null) return;

        if (!_ridingLocal) BeginRide(anchor);

        bool doorsOpen = elevator.DoorsOpen;

        // Doors open at the destination → stop gluing, unlock, let them walk out. Latched, so the later
        // gate-close/depart can't drag them back onto the car.
        if (!_disembarked && doorsOpen)
        {
            _disembarked = true;
            if (_movement != null) _movement.MovementLocked = false;
            if (_cc != null) _cc.enabled = true;
        }

        // Gate CLOSING (was open, now shut) and the rider is still standing in the car footprint — the
        // dwell window elapsed and they didn't walk out. Dump them into the lobby so the departing car
        // can't carry them off or strand them on empty space.
        if (_disembarked && !_forceEjected && _wasDoorsOpen && !doorsOpen &&
            Horizontal(transform.position, anchor.position) < ejectRadius)
        {
            _forceEjected = true;
            StartCoroutine(ForceEjectRoutine(anchor));
        }
        _wasDoorsOpen = doorsOpen;

        if (!_disembarked)
        {
            // Ride: glue to the (moving) car anchor. CC is off so our transform write wins.
            transform.position = anchor.position;
            DriveCameraFeel(elevator);
        }
    }

    // Horizontal (XZ) distance — vertical offset inside the car shouldn't count toward "still aboard".
    private static float Horizontal(Vector3 a, Vector3 b)
    {
        a.y = 0f; b.y = 0f;
        return Vector3.Distance(a, b);
    }

    // Option 3: first SHOVE the dawdler toward the gate (diegetic — the elevator boots them out); if
    // they're still aboard after a short grace, a fade-covered teleport as a guaranteed backstop. So the
    // common case reads as a physical push and the edge case can never leave them on the departing car.
    private IEnumerator ForceEjectRoutine(Transform anchor)
    {
        Vector3 outDir = anchor.forward; outDir.y = 0f;
        outDir = outDir.sqrMagnitude > 1e-4f ? outDir.normalized : transform.forward;
        _movement?.AddImpulse(outDir * shoveSpeed);

        float t = 0f;
        while (t < shoveGrace)
        {
            if (Horizontal(transform.position, anchor.position) > ejectRadius) yield break;   // shove cleared it
            t += Time.deltaTime;
            yield return null;
        }

        // Still aboard → fade to black, teleport at the darkest frame, fade back (no visible pop).
        if (_screenFeedback != null) _screenFeedback.Blink(TeleportToLobbySpawn);
        else TeleportToLobbySpawn();
    }

    // Teleport a rider out to a lobby spawn (client-auth owner move, CC toggled off across the write like
    // PlayerSpawnPositioner/NetworkTeleporter do). Run inside ScreenFeedback.Blink so it's hidden.
    private void TeleportToLobbySpawn()
    {
        var points = FindObjectsByType<SpawnPoint>(FindObjectsSortMode.None)
            .OrderBy(p => p.name).ToArray();
        if (points.Length > 0)
        {
            var p = points[(int)(OwnerClientId % (ulong)points.Length)].transform;
            if (_cc != null) _cc.enabled = false;
            var nt = GetComponent<NetworkTransform>();
            if (nt != null) nt.Teleport(p.position, p.rotation, transform.localScale);
            else transform.SetPositionAndRotation(p.position, p.rotation);
            if (_cc != null) _cc.enabled = true;   // walking again in the lobby
        }
        else
        {
            Debug.LogWarning("[ElevatorRider] Force-eject backstop: no SpawnPoint in the scene to place the " +
                             "rider — add a lobby SpawnPoint. Left in place (already shoved).", this);
        }

        if (_movement != null) _movement.MovementLocked = false;
    }

    private void BeginRide(Transform anchor)
    {
        _ridingLocal = true;
        _disembarked = false;
        _lastCarSpeed = 0f;
        if (_cc != null) _cc.enabled = false;                 // our transform writes win while gluing
        if (_movement != null) _movement.MovementLocked = true;
        transform.rotation = Quaternion.Euler(0f, anchor.rotation.eulerAngles.y, 0f);  // face the gate; look stays free
        GetComponent<ScreenFeedback>()?.PlayWakeUp();         // come to inside the car
    }

    private void EndRide()
    {
        _ridingLocal = false;
        if (_movement != null) _movement.MovementLocked = false;
        if (_cc != null) _cc.enabled = true;
    }

    // Feel the ride: fire ONE small shake pulse when the car lurches (start/dock/depart). Not per-frame —
    // AddShake accumulates trauma, so a continuous feed saturates to a violent blur. The descent itself is
    // felt by simply riding the car (the camera moves with it), so this is only the occasional bump.
    private void DriveCameraFeel(ElevatorController elevator)
    {
        if (_cameraFeel == null) return;
        float dt = Mathf.Max(Time.deltaTime, 1e-4f);
        float speed = elevator.CarWorldDelta.magnitude / dt;          // car m/s
        float jerk = Mathf.Abs(speed - _lastCarSpeed) / dt;           // acceleration → the lurch
        _lastCarSpeed = speed;

        // One bump per lurch (cooldown-gated so trauma can't stack into a saturated blur).
        _shakeCooldown -= dt;
        if (jerk > jerkThreshold && _shakeCooldown <= 0f)
        {
            _cameraFeel.AddShake(jerkShakeAmount);
            _shakeCooldown = shakeMinInterval;
        }

        // Continuous engine vibration under the ride, scaled by how fast the car is moving (0 when docked).
        float rumble = Mathf.Clamp01(speed / Mathf.Max(travelRumbleRefSpeed, 0.01f)) * travelRumbleAmount;
        _cameraFeel.SetRumble(rumble);
    }
}
