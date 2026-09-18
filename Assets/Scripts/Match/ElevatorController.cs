using System.Collections.Generic;
using Unity.Netcode;
using UnityEngine;

/// <summary>
/// The lobby freight elevator — the single arrival/return hub. It DELIVERS players: the car travels in
/// the shaft from ABOVE (first join — descending into the casino) or from BELOW (benched respawn /
/// dungeon-escape return — rising from the depths), docks at the lobby opening, the gate opens, and after
/// a dwell the gate closes and the car departs.
///
/// NETWORKING: server-authoritative, but nothing per-frame is replicated. The server writes a single
/// arrival TIMELINE (a start timestamp + direction) to NetworkVariables; every client animates the car
/// and gate LOCALLY and deterministically from (ServerTime - start), exactly like SeatStation's fall.
/// Smooth and in-sync with no transform traffic.
///
/// PLAYER HANDOFF: this drives the SET-PIECE (car + gate). Placing/revealing the arriving players is the
/// existing spawn flow (they teleport to the LobbySpawnPoints inside the docked car during the open
/// window). Making players physically RIDE the moving car (first-person descent) is a harder follow-up
/// (moving-platform + client-auth) — noted, not done here.
///
/// SETUP (once the model exists): assign `car` (the movable platform/car root), the gate halves
/// (`gateLeft`/`gateRight` — sliding; assign one for a single gate), and tune `shaftTravel` + the timings.
/// The gate should be see-through (a cage/scissor gate) so the car is visible arriving. Wire
/// `ServerArrive(true)` at session start and `ServerArrive(false)` into the benched/escape return paths.
/// </summary>
[RequireComponent(typeof(NetworkObject))]
public class ElevatorController : NetworkBehaviour
{
    public static ElevatorController Instance { get; private set; }

    [Header("Car")]
    [Tooltip("The movable car/platform root. Its authored local position = DOCKED (aligned with the lobby opening).")]
    [SerializeField] private Transform car;
    [Tooltip("How far up/down the shaft (metres) the car starts before travelling in, and departs to after.")]
    [SerializeField] private float shaftTravel = 8f;

    [Header("Gate (see-through cage — so the travel reads)")]
    [Tooltip("Sliding gate halves. Assign both for a centre-split gate, or just one for a single slide.")]
    [SerializeField] private Transform gateLeft;
    [SerializeField] private Transform gateRight;
    [Tooltip("How far each half slides open, along its local X.")]
    [SerializeField] private float gateSlide = 0.9f;

    [Header("Riders")]
    [Tooltip("Interior anchor points INSIDE the car (children of the car), one per rider slot (max 4). " +
             "Riders glue to these and ride with the car. Order = slot index.")]
    [SerializeField] private Transform[] riderAnchors;

    [Header("Counterweight (optional — moves opposite the car)")]
    [Tooltip("Travels the SAME distance as the car but the OPPOSITE way (car down = weight up). Author " +
             "it at its mid / car-docked position; it swings to the bottom when the car idles up top. " +
             "Leave empty if you're hiding or omitting it.")]
    [SerializeField] private Transform counterweight;

    [Header("Timeline (seconds)")]
    [SerializeField] private float travelTime = 2.5f;   // car: offset -> docked
    [SerializeField] private float gateOpenTime = 1f;
    [SerializeField] private float dwellTime = 3.5f;    // gate held open — the step-out window
    [SerializeField] private float gateCloseTime = 1f;
    [SerializeField] private float departTime = 2f;     // car: docked -> offset

    [Header("Machine feel — start/stop lurch")]
    [Tooltip("How far (m) the car jolts when the machine engages/settles. Small & mild — ~0.05. The " +
             "clank SFX later syncs to these moments (start of travel, dock, depart).")]
    [SerializeField] private float joltAmplitude = 0.05f;
    [Tooltip("How fast the jolt decays. Lower = a slower, heavier settle.")]
    [SerializeField] private float joltDecay = 3.5f;
    [Tooltip("Jolt oscillations per second. Low = a slow heavy heave, not a fast buzz.")]
    [SerializeField] private float joltFrequency = 2f;
    [Tooltip("How long each jolt lasts (seconds).")]
    [SerializeField] private float joltDuration = 1.2f;

    [Header("Debug")]
    [Tooltip("Server-only: trigger a test arrival. FromAbove key = join direction; FromBelow = return.")]
    [SerializeField] private KeyCode debugArriveAboveKey = KeyCode.F11;
    [SerializeField] private KeyCode debugArriveBelowKey = KeyCode.F12;

    // Arrival timeline. _arriveStart < 0 = idle (car parked at the last offset, gate closed).
    private readonly NetworkVariable<double> _arriveStart = new NetworkVariable<double>(
        -1.0, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);
    private readonly NetworkVariable<bool> _fromAbove = new NetworkVariable<bool>(
        true, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);

    private Vector3 _carDockedLocal;
    private Vector3 _gateLeftClosed, _gateRightClosed;
    private Vector3 _cwDockedLocal;

    // Live state riders + camera read.
    private float _gateT;                 // 0 closed .. 1 open, cached each frame
    private Vector3 _carWorldDelta;       // car world-position change this frame (for moving-platform / camera)
    private Vector3 _lastCarWorld;
    private bool _haveLastCarWorld;

    /// <summary>An arrival is in progress (car not idle). The batcher waits for this to clear before a new wave.</summary>
    public bool IsBusy => _arriveStart.Value >= 0.0;
    /// <summary>Gate is essentially open — riders disembark, the reveal is happening.</summary>
    public bool DoorsOpen => _gateT > 0.85f;
    /// <summary>How far the car moved in world space this frame (riders/camera read it).</summary>
    public Vector3 CarWorldDelta => _carWorldDelta;
    /// <summary>Interior rider anchors (children of the car). Riders glue to these by index.</summary>
    public IReadOnlyList<Transform> RiderAnchors => riderAnchors;
    public int RiderSlots => riderAnchors != null ? riderAnchors.Length : 0;
    public Transform RiderAnchor(int i) =>
        (riderAnchors != null && i >= 0 && i < riderAnchors.Length) ? riderAnchors[i] : null;

    private float Total => travelTime + gateOpenTime + dwellTime + gateCloseTime + departTime;

    private void Awake()
    {
        Instance = this;
        if (car != null) _carDockedLocal = car.localPosition;
        if (gateLeft != null) _gateLeftClosed = gateLeft.localPosition;
        if (gateRight != null) _gateRightClosed = gateRight.localPosition;
        if (counterweight != null) _cwDockedLocal = counterweight.localPosition;
    }

    public override void OnDestroy()
    {
        if (Instance == this) Instance = null;
        base.OnDestroy();
    }

    /// <summary>Server-only: run an arrival. fromAbove = first-join descent; false = a return rising from below.</summary>
    public void ServerArrive(bool fromAbove)
    {
        if (!IsServer) return;
        _fromAbove.Value = fromAbove;
        _arriveStart.Value = NetworkManager.ServerTime.Time;
    }

    private void Update()
    {
        // Server: end the sequence when the timeline completes, dropping back to idle.
        if (IsServer && _arriveStart.Value >= 0.0 &&
            NetworkManager.ServerTime.Time - _arriveStart.Value >= Total)
            _arriveStart.Value = -1.0;

        float offsetY = _carDockedLocal.y + (_fromAbove.Value ? shaftTravel : -shaftTravel);

        float carY;
        float gateT;   // 0 = closed, 1 = open

        if (_arriveStart.Value < 0.0)
        {
            // Idle: parked off at the last direction's offset, gate shut (hidden behind the gate / off in the shaft).
            carY = offsetY;
            gateT = 0f;
        }
        else
        {
            double e = NetworkManager.ServerTime.Time - _arriveStart.Value;
            float t1 = travelTime, t2 = t1 + gateOpenTime, t3 = t2 + dwellTime, t4 = t3 + gateCloseTime;

            if (e < t1)            { carY = Mathf.Lerp(offsetY, _carDockedLocal.y, Smooth((float)(e / travelTime))); gateT = 0f; }
            else if (e < t2)       { carY = _carDockedLocal.y; gateT = Smooth((float)((e - t1) / gateOpenTime)); }
            else if (e < t3)       { carY = _carDockedLocal.y; gateT = 1f; }
            else if (e < t4)       { carY = _carDockedLocal.y; gateT = 1f - Smooth((float)((e - t3) / gateCloseTime)); }
            else                   { carY = Mathf.Lerp(_carDockedLocal.y, offsetY, Smooth((float)((e - t4) / departTime))); gateT = 0f; }

            // Machine engaging/settling: a damped lurch at the START of travel, at the DOCK, and at
            // DEPART — heavy gear taking up slack, not a smooth glide. (Propagates to the counterweight,
            // which mirrors carY.) The clank SFX later syncs to these same three moments.
            carY += JoltAt(e) + JoltAt(e - t1) + JoltAt(e - t4);
        }

        _gateT = gateT;

        if (car != null)
        {
            car.localPosition = new Vector3(_carDockedLocal.x, carY, _carDockedLocal.z);
            Vector3 world = car.position;
            _carWorldDelta = _haveLastCarWorld ? world - _lastCarWorld : Vector3.zero;
            _lastCarWorld = world;
            _haveLastCarWorld = true;
        }

        if (gateLeft != null)
            gateLeft.localPosition = _gateLeftClosed + Vector3.left * (gateSlide * gateT);
        if (gateRight != null)
            gateRight.localPosition = _gateRightClosed + Vector3.right * (gateSlide * gateT);

        if (counterweight != null)
        {
            // Opposite the car by the same offset from docked: car drops → weight rises.
            float cwY = _cwDockedLocal.y - (carY - _carDockedLocal.y);
            counterweight.localPosition = new Vector3(_cwDockedLocal.x, cwY, _cwDockedLocal.z);
        }
    }

    // Smoothstep so travel and the gate ease in/out rather than moving linearly.
    private static float Smooth(float t) { t = Mathf.Clamp01(t); return t * t * (3f - 2f * t); }

    // A damped bounce: strong at t=0, oscillating and decaying to nothing by joltDuration. Added to the
    // car's Y at motion start / dock / depart so the machine LURCHES like slack taking up.
    private float JoltAt(double sinceEvent)
    {
        if (sinceEvent < 0.0 || sinceEvent > joltDuration) return 0f;
        float t = (float)sinceEvent;
        return joltAmplitude * Mathf.Exp(-joltDecay * t) * Mathf.Sin(2f * Mathf.PI * joltFrequency * t);
    }

    // Debug-only test triggers (strip before shipping). Server presses to preview each arrival direction.
    private void LateUpdate()
    {
        if (!IsServer) return;
        if (Input.GetKeyDown(debugArriveAboveKey)) ServerArrive(true);
        if (Input.GetKeyDown(debugArriveBelowKey)) ServerArrive(false);
    }
}
