using System.Collections;
using Unity.Netcode;
using UnityEngine;

/// <summary>
/// Drives the stylized face's separate rigid features. EYES are done here: each eyeball ROTATES to reflect
/// the direction the OWNER most recently moved the mouse (eyes dart toward the flick, then drift back to
/// centre), and the alpha-clip lid shells are driven by two floats (_UpperLid / _LowerLid on SG_EyeLid) for
/// blinking and lid expression. No shape keys, no bones-per-feature — transform rotation + material floats.
///
/// NETWORKED: the local player's model is hidden (first-person), so the eye direction is only seen by REMOTE
/// players — it must be replicated. The owner computes the gaze from mouse movement and writes it to a
/// NetworkVariable; every client applies it. Blink + lids are LOCAL (cosmetic; per-client blink is fine).
///
/// Brows and mouth hook in later (see the TODO region) — this is the eye pass.
///
/// SETUP: put on the character (a NetworkObject). Assign the two eyeball transforms and the two lid
/// Renderers (SG_EyeLid material, with _UpperLid / _LowerLid float props).
/// </summary>
public class FaceController : NetworkBehaviour
{
    public enum FaceExpression { Neutral, Angry, Sad, Surprised, Nervous, Pain, Happy }

    // Mouth blendshape slots. Open is reserved for voice-driven talk; the rest are expression poses.
    // Pain has no key of its own — it reuses Surprised (a wide-open mouth; the brows/eyes tell them apart).
    private enum Mouth { Open, Surprised, Angry, Sad, Happy, Nervous, COUNT }

    [Header("Eyes — gaze (follows mouse movement)")]
    [SerializeField] private Transform leftEye;
    [SerializeField] private Transform rightEye;
    [Tooltip("Max the eyes deflect from straight-ahead (deg). Keeps them from rolling into the head.")]
    [SerializeField] private float maxGazeAngle = 22f;
    [SerializeField] private float gazeSpeed = 12f;
    [Tooltip("How far a given amount of mouse movement pushes the eyes.")]
    [SerializeField] private float gazeGain = 3f;
    [Tooltip("How fast the eyes drift back to centre when the mouse stops (higher = snappier recentre).")]
    [SerializeField] private float recenterSpeed = 4f;
    [Tooltip("Invert if the eyes look the wrong way vertically.")]
    [SerializeField] private bool invertPitch = false;
    [Tooltip("Invert if the eyes look the wrong way horizontally (mouse right -> eyes left).")]
    [SerializeField] private bool invertYaw = false;
    [Tooltip("DEBUG: sweeps the eyes ±15° yaw ignoring gaze/network — to test the transform refs. " +
             "If the eyes DON'T move with this on, the eye reference is wrong or something overrides it.")]
    [SerializeField] private bool debugOscillateEyes = false;
    [Tooltip("DEBUG: logs the computed gaze + cursor lock state each frame (owner only).")]
    [SerializeField] private bool debugLogGaze = false;

    // Replicated gaze offset (x = pitch euler, y = yaw euler). Owner writes from mouse; everyone applies.
    private readonly NetworkVariable<Vector2> _gaze =
        new NetworkVariable<Vector2>(Vector2.zero, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Owner);
    private Vector2 _localGaze;   // owner's working value before it's written to _gaze

    [Header("Eyes — lids (SG_EyeLid floats)")]
    [SerializeField] private Renderer leftLid;
    [SerializeField] private Renderer rightLid;
    [Tooltip("Resting lid values (0 = open, 1 = closed). A small upper value = the tired heavy-lidded look.")]
    [SerializeField] private float restUpper = 0.22f;
    [SerializeField] private float restLower = 0f;
    [Tooltip("How fast the lids ease toward the expression's rest values (higher = snappier). This is what " +
             "makes the eye-openness LERP between expressions instead of popping, matching brows/pupil/mouth.")]
    [SerializeField] private float lidEaseSpeed = 10f;
    [Header("Eyes — blink")]
    [Tooltip("Average seconds between blinks (randomised ±50%, and offset per instance so players don't sync).")]
    [SerializeField] private float blinkInterval = 4f;
    [SerializeField] private float blinkDuration = 0.12f;
    [Tooltip("Lid values at full blink. Upper+Lower ≥ 1 fully closes the eye; 0.55 each overlaps slightly.")]
    [SerializeField] private float closedUpper = 0.55f;
    [SerializeField] private float closedLower = 0.55f;

    [Header("Brows (posed per expression)")]
    [SerializeField] private Transform leftBrow;
    [SerializeField] private Transform rightBrow;
    [SerializeField] private float browEaseSpeed = 10f;
    [Tooltip("Distance a full raise (raise=1) moves the brows, in metres.")]
    [SerializeField] private float browRaiseUnit = 0.01f;
    [Tooltip("Local axis the brows TRANSLATE along to raise. If raise moves them sideways, change this to " +
             "the axis that's actually UP on the face (try (0,0,1), (1,0,0)…). Flip sign for down.")]
    [SerializeField] private Vector3 browRaiseAxis = new Vector3(0, 1, 0);
    [Tooltip("Local axis the brows ROTATE around to tilt. If tilt rolls/moves them wrong, change this " +
             "(try (0,1,0), (1,0,0)…).")]
    [SerializeField] private Vector3 browTiltAxis = new Vector3(0, 0, 1);
    [Tooltip("Flip if the right brow tilts the wrong way relative to the left (mirror the tilt).")]
    [SerializeField] private bool browMirrorTilt = true;

    [Header("Pupil (EyeProc _PupilSize)")]
    [Tooltip("Resting pupil size (matches the EyeProc material default ~0.18).")]
    [SerializeField] private float restPupil = 0.18f;
    [SerializeField] private float pupilEaseSpeed = 8f;
    [Tooltip("Iris radius as a multiple of pupil size — the iris scales WITH the pupil (stylized 'whole eye " +
             "dilates'). ~2.3 keeps the default iris ≈0.42 at rest pupil 0.18. Set to 0 to keep iris fixed.")]
    [SerializeField] private float irisToPupilRatio = 2.3f;

    [Header("Mouth (shape-key blendshapes)")]
    [Tooltip("SkinnedMeshRenderer of the separate mouth mesh (the one carrying the shape keys).")]
    [SerializeField] private SkinnedMeshRenderer mouth;
    [SerializeField] private float mouthEaseSpeed = 12f;
    [Tooltip("Weight (0-100) an active expression drives its mouth shape to. Lower for a subtler mouth.")]
    [SerializeField] private float mouthExpressionWeight = 100f;
    [Header("Mouth — shape-key names (must match the mesh, case-sensitive)")]
    [SerializeField] private string bsOpen      = "Open";
    [SerializeField] private string bsSurprised = "Surprised";
    [SerializeField] private string bsAngry     = "Angry";
    [SerializeField] private string bsSad       = "Sad";
    [SerializeField] private string bsHappy     = "Happy";
    [SerializeField] private string bsNervous   = "Nervous";
    [Header("Mouth — talk (voice-driven Open)")]
    [Tooltip("Max weight the Open shape reaches at full voice amplitude. Push amplitude in via " +
             "SetTalkAmplitude(0..1) each frame from your Dissonance/voice level.")]
    [SerializeField] private float talkOpenWeight = 70f;
    [SerializeField] private float talkEaseSpeed = 20f;
    [Tooltip("When the current expression ALREADY opens the mouth (Surprised/Pain), pulse that open shape " +
             "with the voice instead of stacking a second jaw-drop on top (which would double-open/clip). " +
             "Off = the open-mouth expression just holds, no lip-sync during it.")]
    [SerializeField] private bool pulseOpenMouthWithVoice = true;
    [Tooltip("The open-mouth shape's weight when silent during an open-mouth expression; it rises to " +
             "mouthExpressionWeight as the voice gets louder. Only used if pulseOpenMouthWithVoice is on.")]
    [SerializeField] private float openMouthTalkFloor = 65f;

    // Set by SetExpression: does the active expression open the mouth on its own? If so, talk pulses that
    // shape rather than adding the separate Open jaw-drop (see DriveMouth). Prevents the double-open.
    private bool _exprOpensMouth;

    [Header("Nervous eye tremor")]
    [Tooltip("Amplitude of the eye shiver (degrees) layered on the gaze while the Nervous expression is " +
             "active. 0 = off. Small values read best — this is a jittery unease, not a wobble.")]
    [SerializeField] private float nervousShakeAmount = 1.5f;
    [Tooltip("Speed of the nervous shiver. Higher = faster, more frantic.")]
    [SerializeField] private float nervousShakeSpeed = 35f;
    [Tooltip("Seconds of CONTINUOUS Nervous for the shake to build to full strength. It starts calm and " +
             "escalates the longer the nervous face holds — the comedic panic build. Resets when the " +
             "expression changes.")]
    [SerializeField] private float nervousRampTime = 6f;
    [Tooltip("How many times stronger the shake gets at full build (speed ramps up more gently, by its " +
             "square root, so it goes frantic without turning into a pure blur).")]
    [SerializeField] private float nervousMaxMultiplier = 4f;
    private float _nervousTime;   // seconds Nervous has been continuously held; drives the build

    [Header("DEBUG expression cycler")]
    [SerializeField] private bool debugCycleExpressions = false;
    [SerializeField] private KeyCode debugCycleKey = KeyCode.Tab;

    private FaceExpression _currentExpr = FaceExpression.Neutral;   // last-set expression (drives the tremor)

    private readonly int[]   _bsIndex  = new int[(int)Mouth.COUNT];   // resolved blendshape indices (-1 = missing)
    private readonly float[] _bsWeight = new float[(int)Mouth.COUNT]; // current eased weights
    private readonly float[] _bsTarget = new float[(int)Mouth.COUNT]; // targets set by expression / talk
    private float _talkAmp;   // 0..1, pushed in by voice code; drives the Open shape

    private static readonly int UpperId = Shader.PropertyToID("_UpperLid");
    private static readonly int LowerId = Shader.PropertyToID("_LowerLid");

    private Quaternion _leftBase, _rightBase;   // straight-ahead eye orientations
    private MaterialPropertyBlock _lidMpb;
    private bool _blinking;
    private float _curUpper, _curLower;          // lid values applied this frame
    private Vector2 _appliedGaze;                // smoothed gaze offset (persists; transform is set directly)

    // Brow rest transforms + eased pose (raise: 0=rest, ±1; tilt: degrees, +tilt = inner ends down on left).
    private Vector3 _lBrowPos, _rBrowPos;
    private Quaternion _lBrowRot, _rBrowRot;
    private float _browRaise, _browTilt, _browRaiseTarget, _browTiltTarget;

    private static readonly int PupilId = Shader.PropertyToID("_PupilSize");
    private static readonly int IrisId  = Shader.PropertyToID("_IrisRadius");
    private Renderer _leftEyeRend, _rightEyeRend;
    private float _pupil, _pupilTarget;
    private FaceExpression _debugExpr;

    private void Awake()
    {
        if (leftEye)  { _leftBase  = leftEye.localRotation;  _leftEyeRend  = leftEye.GetComponent<Renderer>(); }
        if (rightEye) { _rightBase = rightEye.localRotation; _rightEyeRend = rightEye.GetComponent<Renderer>(); }
        if (leftBrow)  { _lBrowPos = leftBrow.localPosition;  _lBrowRot = leftBrow.localRotation; }
        if (rightBrow) { _rBrowPos = rightBrow.localPosition; _rBrowRot = rightBrow.localRotation; }
        _lidMpb = new MaterialPropertyBlock();
        _curUpper = restUpper; _curLower = restLower;
        _pupil = _pupilTarget = restPupil;

        // Resolve mouth blendshape indices by name (case-sensitive; -1 if the key isn't on the mesh).
        if (mouth != null && mouth.sharedMesh != null)
        {
            var m = mouth.sharedMesh;
            _bsIndex[(int)Mouth.Open]      = m.GetBlendShapeIndex(bsOpen);
            _bsIndex[(int)Mouth.Surprised] = m.GetBlendShapeIndex(bsSurprised);
            _bsIndex[(int)Mouth.Angry]     = m.GetBlendShapeIndex(bsAngry);
            _bsIndex[(int)Mouth.Sad]       = m.GetBlendShapeIndex(bsSad);
            _bsIndex[(int)Mouth.Happy]     = m.GetBlendShapeIndex(bsHappy);
            _bsIndex[(int)Mouth.Nervous]   = m.GetBlendShapeIndex(bsNervous);
        }
        else
        {
            for (int i = 0; i < _bsIndex.Length; i++) _bsIndex[i] = -1;
        }
    }

    private void Start()
    {
        StartCoroutine(BlinkLoop());   // blink is local per-client (cosmetic, no need to sync)
    }

    private void Update()
    {
        // Owner computes the gaze data from the mouse here; the EYE ROTATION is applied in LateUpdate
        // (after the Animator) because the eyes are mapped as Humanoid eye bones and the Animator would
        // otherwise overwrite our rotation every frame.
        if (IsOwner && !debugOscillateEyes) OwnerComputeGaze();

        // DEBUG: step through expressions to test brow poses + pupil sizes.
        if (debugCycleExpressions && Input.GetKeyDown(debugCycleKey))
        {
            _debugExpr = (FaceExpression)(((int)_debugExpr + 1) % System.Enum.GetValues(typeof(FaceExpression)).Length);
            SetExpression(_debugExpr);
            Debug.Log($"[FaceController] expression = {_debugExpr}", this);
        }

        // Lids + pupil are material floats (not animated by the Animator), so they're fine in Update.
        // Ease the lids toward the expression's rest values so eye-openness LERPS between expressions
        // (a blink overrides them directly via its coroutine; easing resumes from the blink's end value).
        if (!_blinking)
        {
            float t = 1f - Mathf.Exp(-lidEaseSpeed * Time.deltaTime);
            _curUpper = Mathf.Lerp(_curUpper, restUpper, t);
            _curLower = Mathf.Lerp(_curLower, restLower, t);
        }
        ApplyLids(_curUpper, _curLower);
        DrivePupil();
        DriveMouth();
    }

    private void LateUpdate()
    {
        // Runs AFTER the Animator poses the skeleton, so these writes override the eye-bone animation.
        if (debugOscillateEyes)
        {
            float a = Mathf.Sin(Time.time * 2f) * 15f;   // ±15° yaw sweep — ref/override test
            if (leftEye)  leftEye.localRotation  = _leftBase  * Quaternion.Euler(0f, a, 0f);
            if (rightEye) rightEye.localRotation = _rightBase * Quaternion.Euler(0f, a, 0f);
        }
        else DriveGaze();   // everyone applies the replicated _gaze to the eyeballs
        DriveBrows();       // pose the brows for the current expression (also in LateUpdate, override-safe)
    }

    // --- Brows: ease toward the expression pose (raise = translate up, tilt = Z-rotate) and set directly ---
    private void DriveBrows()
    {
        if (leftBrow == null && rightBrow == null) return;
        float t = 1f - Mathf.Exp(-browEaseSpeed * Time.deltaTime);
        _browRaise = Mathf.Lerp(_browRaise, _browRaiseTarget, t);
        _browTilt  = Mathf.Lerp(_browTilt,  _browTiltTarget,  t);

        Vector3 up = browRaiseAxis.normalized * (_browRaise * browRaiseUnit);
        Vector3 tiltAxis = browTiltAxis.normalized;   // in HEAD-BONE (parent) space, so the brow's own
                                                       // rotated local frame doesn't skew the tilt
        if (leftBrow)
        {
            leftBrow.localPosition = _lBrowPos + up;
            leftBrow.localRotation = Quaternion.AngleAxis(_browTilt, tiltAxis) * _lBrowRot;
        }
        if (rightBrow)
        {
            rightBrow.localPosition = _rBrowPos + up;
            rightBrow.localRotation = Quaternion.AngleAxis(browMirrorTilt ? -_browTilt : _browTilt, tiltAxis) * _rBrowRot;
        }
    }

    // --- Owner: push the eyes toward the direction the mouse just moved, then drift back to centre ---
    private void OwnerComputeGaze()
    {
        // Only while the cursor is LOCKED (a freed cursor = a menu/cards UI is up, mouse isn't "looking").
        if (Cursor.lockState == CursorLockMode.Locked)
        {
            float dx = Input.GetAxisRaw("Mouse X");
            float dy = Input.GetAxisRaw("Mouse Y");
            _localGaze.y += (invertYaw ? -dx : dx) * gazeGain;      // yaw: mouse right -> eyes right
            _localGaze.x += (invertPitch ? dy : -dy) * gazeGain;   // pitch: mouse up -> eyes up (negative)
        }
        // Always decay toward centre so the eyes settle when the mouse stops (the "lead then recentre" feel).
        _localGaze = Vector2.Lerp(_localGaze, Vector2.zero, recenterSpeed * Time.deltaTime);
        _localGaze = Vector2.ClampMagnitude(_localGaze, maxGazeAngle);
        _gaze.Value = _localGaze;

        if (debugLogGaze)
            Debug.Log($"[FaceController] gaze={_localGaze}  locked={Cursor.lockState}  isOwner={IsOwner}", this);
    }

    // --- Everyone: ease the eyeballs toward the replicated gaze offset ---
    private void DriveGaze()
    {
        // Smooth the OFFSET VALUE, not the transform. The eyes are Humanoid eye bones, so the Animator
        // resets their rotation to bind every frame before LateUpdate — slerping the transform from its
        // (reset) current rotation caps the motion at one frame's fraction. Instead we ease the value and
        // SET the transform directly (same as the debug oscillate, which is why that one worked).
        float t = 1f - Mathf.Exp(-gazeSpeed * Time.deltaTime);
        _appliedGaze = Vector2.Lerp(_appliedGaze, _gaze.Value, t);

        // Nervous tremor: a fast, small jitter layered on the gaze while Nervous is active. Perlin (not
        // pure random) so it's a smooth shiver, not a per-frame strobe. Local/cosmetic like the blink —
        // each client shivers at its own phase, which is fine. Sampled on offset axes so pitch and yaw
        // jitter independently (a locked-together shake reads as a wobble, not nerves).
        float sx = 0f, sy = 0f;
        if (_currentExpr == FaceExpression.Nervous && nervousShakeAmount > 0f)
        {
            // Build the panic: accumulate held-time and ramp amplitude 1x→max over nervousRampTime.
            _nervousTime += Time.deltaTime;
            float build = nervousRampTime > 0f ? Mathf.Clamp01(_nervousTime / nervousRampTime) : 1f;
            float ampMult   = Mathf.Lerp(1f, nervousMaxMultiplier, build);
            float speedMult = Mathf.Lerp(1f, Mathf.Sqrt(nervousMaxMultiplier), build);   // faster, but gentler than amplitude

            float amp = nervousShakeAmount * ampMult;
            float tt  = Time.time * nervousShakeSpeed * speedMult;
            sx = (Mathf.PerlinNoise(tt, 0f) - 0.5f) * 2f * amp;
            sy = (Mathf.PerlinNoise(0f, tt + 13.7f) - 0.5f) * 2f * amp;
        }

        // Pitch on local X (works), yaw on local Z — the eye's forward axis isn't +Z after bone-parenting,
        // so yaw goes on Z, not Y. If it ROLLS instead of turning left/right, the axis is different again.
        Quaternion offset = Quaternion.Euler(_appliedGaze.x + sx, 0f, _appliedGaze.y + sy);
        if (leftEye)  leftEye.localRotation  = _leftBase  * offset;
        if (rightEye) rightEye.localRotation = _rightBase * offset;
    }

    // --- Blink: close both lids to `closed` and back, on a randomised loop ---
    private IEnumerator BlinkLoop()
    {
        yield return new WaitForSeconds(Random.Range(0f, blinkInterval));   // desync players
        while (true)
        {
            yield return new WaitForSeconds(blinkInterval * Random.Range(0.5f, 1.5f));
            yield return Blink();
        }
    }

    private IEnumerator Blink()
    {
        _blinking = true;
        float half = Mathf.Max(0.01f, blinkDuration * 0.5f);
        // close
        for (float t = 0f; t < 1f; t += Time.deltaTime / half)
        {
            float e = Mathf.SmoothStep(0f, 1f, t);
            ApplyLids(Mathf.Lerp(restUpper, closedUpper, e), Mathf.Lerp(restLower, closedLower, e));
            yield return null;
        }
        // open
        for (float t = 0f; t < 1f; t += Time.deltaTime / half)
        {
            float e = Mathf.SmoothStep(0f, 1f, t);
            ApplyLids(Mathf.Lerp(closedUpper, restUpper, e), Mathf.Lerp(closedLower, restLower, e));
            yield return null;
        }
        ApplyLids(restUpper, restLower);
        _blinking = false;
    }

    private void ApplyLids(float upper, float lower)
    {
        _curUpper = upper; _curLower = lower;
        SetLid(leftLid, upper, lower);
        SetLid(rightLid, upper, lower);
    }

    // Ease the pupil toward the expression target and write _PupilSize on the eye materials.
    private void DrivePupil()
    {
        _pupil = Mathf.Lerp(_pupil, _pupilTarget, 1f - Mathf.Exp(-pupilEaseSpeed * Time.deltaTime));
        SetPupil(_leftEyeRend, _pupil);
        SetPupil(_rightEyeRend, _pupil);
    }

    /// <summary>Feed the local voice amplitude (0..1) here each frame — it drives the Open shape for
    /// lip-sync. Works on every client for every player, since Dissonance decodes each speaker's playback
    /// amplitude locally, so no extra network variable is needed. Call with 0 when not speaking.</summary>
    public void SetTalkAmplitude(float amp01) => _talkAmp = Mathf.Clamp01(amp01);

    // Ease every mouth blendshape toward its target and write the weights. Open tracks voice amplitude
    // (its own faster ease so lip-sync stays snappy); the expression shapes ease at mouthEaseSpeed.
    private void DriveMouth()
    {
        if (mouth == null) return;

        if (_exprOpensMouth)
        {
            // The expression already opens the mouth (Surprised/Pain → Surprised shape). Never stack the
            // separate Open jaw-drop on top — that's the double-open. Instead pulse the open shape itself
            // with the voice (floor→full), so it still visibly talks, or just hold it if pulsing is off.
            _bsTarget[(int)Mouth.Open] = 0f;
            if (pulseOpenMouthWithVoice)
                _bsTarget[(int)Mouth.Surprised] =
                    Mathf.Lerp(openMouthTalkFloor, mouthExpressionWeight, _talkAmp);
        }
        else
        {
            // Closed-lip expression: talk adds a jaw-drop on top of the lip shape ("talking angrily" etc.).
            _bsTarget[(int)Mouth.Open] = _talkAmp * talkOpenWeight;
        }

        float tExpr = 1f - Mathf.Exp(-mouthEaseSpeed * Time.deltaTime);
        float tTalk = 1f - Mathf.Exp(-talkEaseSpeed  * Time.deltaTime);
        for (int i = 0; i < (int)Mouth.COUNT; i++)
        {
            float rate = (i == (int)Mouth.Open) ? tTalk : tExpr;
            _bsWeight[i] = Mathf.Lerp(_bsWeight[i], _bsTarget[i], rate);
            if (_bsIndex[i] >= 0) mouth.SetBlendShapeWeight(_bsIndex[i], _bsWeight[i]);
        }
    }

    private void SetPupil(Renderer r, float size)
    {
        if (r == null) return;
        r.GetPropertyBlock(_lidMpb);
        _lidMpb.SetFloat(PupilId, size);
        if (irisToPupilRatio > 0f)
            _lidMpb.SetFloat(IrisId, Mathf.Clamp(size * irisToPupilRatio, 0.05f, 0.9f)); // iris scales WITH pupil
        r.SetPropertyBlock(_lidMpb);
    }

    private void SetLid(Renderer r, float upper, float lower)
    {
        if (r == null) return;
        r.GetPropertyBlock(_lidMpb);
        _lidMpb.SetFloat(UpperId, upper);
        _lidMpb.SetFloat(LowerId, lower);
        r.SetPropertyBlock(_lidMpb);
    }

    /// <summary>Set the lid shape + brow pose for an expression (blink overrides lids briefly). Mouth TODO.</summary>
    public void SetExpression(FaceExpression e)
    {
        // lids (restUpper/restLower), brows (_browRaiseTarget = raise ±1, _browTiltTarget = tilt degrees:
        // +tilt drops the inner ends = angry/frown; -tilt lifts the inner ends = sad/worried).
        if (e != _currentExpr) _nervousTime = 0f;   // restart the panic build each time the expression changes
        _currentExpr = e;   // remembered so the nervous eye tremor (DriveGaze) knows when it's active

        // Exaggerated on purpose — this is a cartoon-technique face, so the poses are pushed well past
        // realistic. Tune per-value if any reads TOO extreme, but err loud: these read at a distance.
        switch (e)
        {
            case FaceExpression.Angry:     restUpper = 0.42f; restLower = 0.32f; _browRaiseTarget = -0.6f; _browTiltTarget =  28f; _pupilTarget = 0.10f; break;  // hard low V, constricted
            case FaceExpression.Sad:       restUpper = 0.38f; restLower = 0f;    _browRaiseTarget =  0.4f; _browTiltTarget = -20f; _pupilTarget = 0.24f; break;  // steep inner-up brows
            case FaceExpression.Surprised: restUpper = 0f;    restLower = 0f;    _browRaiseTarget =  1.4f; _browTiltTarget =   0f; _pupilTarget = 0.06f; break;  // brows way up, pinprick pupil
            case FaceExpression.Nervous:   restUpper = 0.15f; restLower = 0.22f; _browRaiseTarget =  0.6f; _browTiltTarget =   9f; _pupilTarget = 0.32f; break;  // blown pupils, uneasy
            case FaceExpression.Pain:      restUpper = 0.52f; restLower = 0.42f; _browRaiseTarget = -0.6f; _browTiltTarget =  30f; _pupilTarget = 0.09f; break;  // clenched wince
            case FaceExpression.Happy:     restUpper = 0.08f; restLower = 0.32f; _browRaiseTarget =  0.5f; _browTiltTarget = -10f; _pupilTarget = 0.22f; break;  // big smiling-eyes squint
            default:                       restUpper = 0.22f; restLower = 0f;    _browRaiseTarget =  0f;   _browTiltTarget =   0f; _pupilTarget = restPupil; break;
        }

        // Which expressions open the mouth on their own (so talk pulses instead of stacking — see DriveMouth).
        _exprOpensMouth = (e == FaceExpression.Surprised || e == FaceExpression.Pain);

        // Mouth: zero all expression shapes, then raise the one this expression uses. Open is NOT touched
        // here — it's owned by voice (DriveMouth). Pain reuses the Surprised shape (no key of its own).
        _bsTarget[(int)Mouth.Surprised] = 0f;
        _bsTarget[(int)Mouth.Angry]     = 0f;
        _bsTarget[(int)Mouth.Sad]       = 0f;
        _bsTarget[(int)Mouth.Happy]     = 0f;
        _bsTarget[(int)Mouth.Nervous]   = 0f;
        switch (e)
        {
            case FaceExpression.Angry:     _bsTarget[(int)Mouth.Angry]     = mouthExpressionWeight; break;
            case FaceExpression.Sad:       _bsTarget[(int)Mouth.Sad]       = mouthExpressionWeight; break;
            case FaceExpression.Surprised: _bsTarget[(int)Mouth.Surprised] = mouthExpressionWeight; break;
            case FaceExpression.Nervous:   _bsTarget[(int)Mouth.Nervous]   = mouthExpressionWeight; break;
            case FaceExpression.Pain:      _bsTarget[(int)Mouth.Surprised] = mouthExpressionWeight; break;  // reuse
            case FaceExpression.Happy:     _bsTarget[(int)Mouth.Happy]     = mouthExpressionWeight; break;
            // Neutral: all zero (Basis).
        }
    }
}
