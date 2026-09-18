using Unity.Netcode;
using UnityEngine;

/// <summary>
/// All the camera "game feel" in one place: head bob, landing dip, strafe lean, a reusable shake
/// channel, and the sprint FOV kick. Owner-only, purely local — nothing here replicates, every player
/// sees their own.
///
/// WHY ONE COMPONENT: these all fight over the same camera transform, so keeping them together lets
/// them compose cleanly (sum the position offsets, multiply the rotation offsets) instead of several
/// scripts clobbering each other.
///
/// HOW IT AVOIDS FIGHTING PlayerCamera: PlayerCamera sets the camera's pitch in Update. This runs in
/// LateUpdate and REBUILDS the whole rotation as `pitch * feel` from PlayerCamera.Pitch — it does NOT
/// read-and-multiply the live rotation, which would re-add the feel offset every frame and drift.
/// Position offsets are free: nothing else writes the camera's localPosition.
///
/// SETUP: add to the Player prefab. Auto-finds PlayerMovement, PlayerCamera and the Camera. The
/// flashlight sway is optional — leave flashlightPivot empty to skip it.
/// </summary>
public class CameraFeel : NetworkBehaviour
{
    [Header("Head bob")]
    [Tooltip("Peak bob offset in metres. KEEP TINY (~0.03-0.05). Bob is felt, not seen; the version " +
             "that feels right in a 5-second test is usually sickening over 20 minutes.")]
    [SerializeField] private float bobAmplitude = 0.04f;
    [Tooltip("Bob cycles per metre travelled. The phase advances with DISTANCE, not time, so bob-per-" +
             "step stays constant whether you walk or sprint -- only the rate changes.")]
    [SerializeField] private float bobFrequency = 1.6f;
    [Tooltip("How much narrower the horizontal sway is than the vertical bounce (0..1). The lateral " +
             "component is what turns a straight up-down bounce into a natural figure-8.")]
    [Range(0f, 1f)] [SerializeField] private float bobHorizontalRatio = 0.5f;
    [Tooltip("Speed (m/s) at which bob reaches full amplitude. Below it, bob scales down with speed so " +
             "it fades in from a standstill and out to a stop.")]
    [SerializeField] private float bobReferenceSpeed = 6f;

    [Header("Landing dip")]
    [Tooltip("Metres of dip per m/s of impact. The camera drops and springs back when you land.")]
    [SerializeField] private float dipPerImpact = 0.012f;
    [Tooltip("Impact speed below which no dip plays -- so ordinary grounded steps don't dip.")]
    [SerializeField] private float dipMinImpact = 3f;
    [SerializeField] private float dipStiffness = 120f;   // spring return
    [SerializeField] private float dipDamping = 14f;      // higher = less bounce

    [Header("Strafe lean")]
    [Tooltip("Degrees of camera roll when strafing full-tilt. Subtle -- felt, not seen.")]
    [SerializeField] private float leanAngle = 2.5f;
    [SerializeField] private float leanSpeed = 8f;

    [Header("Sprint FOV")]
    [Tooltip("Degrees added to the base FOV while sprinting. ~8-10 reads as speed; more reads as a " +
             "fisheye. Perceived speed comes mostly from peripheral optical flow, so this works even " +
             "when the actual speed increase is small.")]
    [SerializeField] private float sprintFovBoost = 9f;
    [SerializeField] private float fovInSpeed = 60f;    // easing IN is snappier (responsive punch)
    [SerializeField] private float fovOutSpeed = 35f;   // easing OUT is relaxed

    [Header("Shake channel")]
    [Tooltip("Max positional shake (m) at full trauma.")]
    [SerializeField] private float shakePosMax = 0.06f;
    [Tooltip("Max rotational shake (deg) at full trauma.")]
    [SerializeField] private float shakeRotMax = 2.5f;
    [Tooltip("Trauma lost per second. Bigger events pass more trauma, so they also last longer.")]
    [SerializeField] private float shakeRecovery = 1.6f;

    [Header("Rumble channel (continuous — driven by the elevator ride, etc.)")]
    [Tooltip("Peak positional rumble (m) at full amount. This is a STEADY vibration, unlike shake's " +
             "one-shot trauma — call SetRumble() every frame to sustain it, and it eases out on its own.")]
    [SerializeField] private float rumblePosMax = 0.012f;
    [Tooltip("Peak rotational rumble (deg) at full amount.")]
    [SerializeField] private float rumbleRotMax = 0.25f;
    [Tooltip("Rumble noise rate. Higher = a finer buzz; lower = a slower sway.")]
    [SerializeField] private float rumbleFrequency = 22f;
    [Tooltip("How fast the rumble eases in and out (per second).")]
    [SerializeField] private float rumbleEase = 8f;

    [Header("Seated camera (the sit anim is visual-only — the eye is placed here in code)")]
    [Tooltip("Local offset applied to the eye while seated, to line it up with the model's seated head. " +
             "Usually DOWN (you sit lower than you stand) and a touch FORWARD (leaning over the table). " +
             "Local space: +Z is toward the table (the seat snaps your yaw at it), +Y is up.")]
    [SerializeField] private Vector3 seatedCameraOffset = new Vector3(0f, -0.35f, 0.25f);
    [Tooltip("Degrees the FOV TIGHTENS while seated. This is what actually makes faces across the table " +
             "readable — apparent size scales with 1/distance, so a small lean barely helps but a zoom " +
             "magnifies. ~10-15 reads as leaning in to study a hand; more starts to feel scoped. 0 = off.")]
    [SerializeField] private float seatedZoom = 12f;
    [Tooltip("How fast the seated offset/zoom eases in when you sit and out when you stand.")]
    [SerializeField] private float seatedLeanSpeed = 6f;
    private SeatOccupant _seat;
    private float _seatLean;   // 0..1 eased

    [Header("Flashlight sway (optional)")]
    [Tooltip("A pivot the held flashlight parents under. Given one, the beam LAGS fast view turns and " +
             "springs back -- reading as held rather than welded to the eye. Preview of the viewmodel " +
             "arm lag. Leave empty to skip.")]
    [SerializeField] private Transform flashlightPivot;
    [SerializeField] private float flashlightSwayAmount = 6f;   // deg of lag per unit of turn rate
    [SerializeField] private float flashlightSwayReturn = 10f;  // spring back toward aligned

    private PlayerMovement _movement;
    private PlayerCamera _cameraCtrl;
    private Camera _cam;
    private Transform _camT;

    private Vector3 _restPos;
    private float _baseFov;

    private float _bobPhase;
    private float _bobAmp;          // smoothed, so amplitude eases in/out with speed
    private float _dip, _dipVel;    // spring state for the landing dip
    private float _lean;
    private float _trauma;          // 0..1 shake energy
    private float _rumbleInput;     // immediate-mode: set each frame by whoever wants rumble, then reset
    private float _rumble;          // smoothed 0..1

    private Vector2 _flashSway, _flashSwayVel;
    private Quaternion _lastCamWorldRot;

    public override void OnNetworkSpawn()
    {
        if (!IsOwner) { enabled = false; return; }

        _movement = GetComponent<PlayerMovement>();
        _seat = GetComponent<SeatOccupant>();
        _cameraCtrl = GetComponent<PlayerCamera>();
        _camT = _cameraCtrl != null ? _cameraCtrl.CameraTransform : null;
        _cam = _camT != null ? _camT.GetComponent<Camera>() : GetComponentInChildren<Camera>(true);
        if (_camT == null && _cam != null) _camT = _cam.transform;

        if (_camT != null) _restPos = _camT.localPosition;
        if (_cam != null) _baseFov = _cam.fieldOfView;
        if (_camT != null) _lastCamWorldRot = _camT.rotation;

        if (_movement != null) _movement.Landed += OnLanded;
    }

    public override void OnNetworkDespawn()
    {
        if (_movement != null) _movement.Landed -= OnLanded;
    }

    /// <summary>Kick the shake channel. `amount` adds trauma (clamped to 1); bigger = stronger AND
    /// longer, since it decays at a fixed rate. Any event can call this — landings do so automatically,
    /// and the wake-up effect uses it for the disoriented sway.</summary>
    public void AddShake(float amount) => _trauma = Mathf.Clamp01(_trauma + amount);

    /// <summary>Continuous rumble input (0..1) — the elevator ride, an engine, etc. Unlike AddShake this
    /// does NOT accumulate; call it every frame to sustain the vibration, and it eases out on its own the
    /// moment you stop. Uses Max so several sources in one frame don't cancel each other.</summary>
    public void SetRumble(float amount01) => _rumbleInput = Mathf.Max(_rumbleInput, Mathf.Clamp01(amount01));

    private void OnLanded(float impact)
    {
        if (impact < dipMinImpact) return;
        // Kick the dip spring downward; it springs back up. Also a touch of shake on a hard landing.
        _dipVel -= impact * dipPerImpact * dipStiffness * 0.1f;
        AddShake(Mathf.Clamp01((impact - dipMinImpact) * 0.03f));
    }

    private void LateUpdate()
    {
        if (_camT == null) return;
        float dt = Time.deltaTime;

        // --- Head bob: figure-8, phase advanced by DISTANCE ---
        float speed = _movement != null ? _movement.PlanarSpeed : 0f;
        float targetAmp = bobAmplitude * Mathf.Clamp01(speed / Mathf.Max(bobReferenceSpeed, 0.01f));
        _bobAmp = Mathf.MoveTowards(_bobAmp, targetAmp, bobAmplitude * 4f * dt);
        if (speed > 0.1f) _bobPhase += speed * bobFrequency * dt;
        float bobV = Mathf.Sin(_bobPhase * 2f) * _bobAmp;                       // vertical, twice per cycle
        float bobH = Mathf.Sin(_bobPhase) * _bobAmp * bobHorizontalRatio;       // horizontal, once -> figure-8

        // --- Landing dip: damped spring back to 0 ---
        _dipVel += (-_dip * dipStiffness - _dipVel * dipDamping) * dt;
        _dip += _dipVel * dt;

        // --- Strafe lean ---
        float strafe = _movement != null ? _movement.StrafeInput : 0f;
        _lean = Mathf.Lerp(_lean, -strafe * leanAngle, leanSpeed * dt);

        // --- Shake: trauma^2 gives a punchy falloff; Perlin per axis, centred to -1..1 ---
        _trauma = Mathf.MoveTowards(_trauma, 0f, shakeRecovery * dt);
        float sh = _trauma * _trauma;
        float t = Time.time * 25f;
        Vector3 shakePos = new Vector3(Perlin(1, t), Perlin(2, t), 0f) * (sh * shakePosMax);
        Vector3 shakeRot = new Vector3(Perlin(3, t), Perlin(4, t), Perlin(5, t)) * (sh * shakeRotMax);

        // --- Seated eye placement: the sit animation is visual-only, so ease the first-person eye to the
        // seated head position here (local space; +Z is body-forward = toward the table since the seat
        // snaps our yaw at it). Magnification for reading faces comes from the FOV tighten below. ---
        bool seated = _seat != null && _seat.IsSeated;
        _seatLean = Mathf.MoveTowards(_seatLean, seated ? 1f : 0f, seatedLeanSpeed * dt);
        Vector3 seatOffset = seatedCameraOffset * _seatLean;

        // --- Rumble: continuous vibration, eased toward this frame's input, then input reset (immediate-
        // mode, so it fades out automatically when nobody's feeding it). ---
        _rumble = Mathf.MoveTowards(_rumble, _rumbleInput, rumbleEase * dt);
        _rumbleInput = 0f;
        Vector3 rumblePos = Vector3.zero, rumbleRot = Vector3.zero;
        if (_rumble > 0.001f)
        {
            float rt = Time.time * rumbleFrequency;
            rumblePos = new Vector3(Perlin(6, rt), Perlin(7, rt), Perlin(8, rt)) * (rumblePosMax * _rumble);
            rumbleRot = new Vector3(Perlin(9, rt), Perlin(10, rt), 0f) * (rumbleRotMax * _rumble);
        }

        // --- Compose. Rotation rebuilt from PITCH, never from the live value (no drift). ---
        _camT.localPosition = _restPos + new Vector3(bobH, bobV + _dip, 0f) + shakePos + rumblePos + seatOffset;
        float pitch = _cameraCtrl != null ? _cameraCtrl.Pitch : 0f;
        _camT.localRotation = Quaternion.Euler(pitch + shakeRot.x + rumbleRot.x, shakeRot.y + rumbleRot.y, _lean + shakeRot.z);

        // --- Sprint FOV: ease in faster than out ---
        if (_cam != null)
        {
            bool sprinting = _movement != null && _movement.IsSprinting;
            // Sprint widens FOV; sitting tightens it to magnify faces across the table. They never overlap
            // (you can't sprint while seated), so a straight sum is safe.
            float targetFov = _baseFov + (sprinting ? sprintFovBoost : 0f) - seatedZoom * _seatLean;
            float rate = sprinting ? fovInSpeed : fovOutSpeed;
            _cam.fieldOfView = Mathf.MoveTowards(_cam.fieldOfView, targetFov, rate * dt);
        }

        UpdateFlashlightSway(dt);
    }

    // Beam lags fast view turns and springs back. Measures how far the camera rotated this frame and
    // pushes the pivot the opposite way, then relaxes toward aligned -- the held-flashlight lag, and a
    // stand-in for the viewmodel arm lag until real arms exist.
    private void UpdateFlashlightSway(float dt)
    {
        if (flashlightPivot == null) return;

        Quaternion delta = _camT.rotation * Quaternion.Inverse(_lastCamWorldRot);
        _lastCamWorldRot = _camT.rotation;
        delta.ToAngleAxis(out float ang, out Vector3 axis);
        if (ang > 180f) ang -= 360f;
        // Yaw turn -> horizontal lag, pitch turn -> vertical lag.
        Vector2 turn = new Vector2(axis.y, -axis.x) * (ang * Mathf.Deg2Rad);

        _flashSwayVel += (turn * flashlightSwayAmount - _flashSway * flashlightSwayReturn) * dt;
        _flashSwayVel = Vector2.Lerp(_flashSwayVel, Vector2.zero, flashlightSwayReturn * dt);
        _flashSway += _flashSwayVel * dt;

        flashlightPivot.localRotation = Quaternion.Euler(-_flashSway.y, _flashSway.x, 0f);
    }

    private static float Perlin(int seed, float t) => Mathf.PerlinNoise(seed * 13.7f, t) * 2f - 1f;
}
