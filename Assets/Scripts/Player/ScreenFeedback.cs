using System.Collections;
using Unity.Netcode;
using UnityEngine;

/// <summary>
/// Drives the BuckshotPost material's screen effects for the local player: the wake-up (regain
/// consciousness) sequence, a hurt flash, and a continuous low-stamina vignette. Owner-only, local --
/// nothing replicates; each player sees their own.
///
/// One driver for several effects because they all write the SAME three material params (exposure,
/// saturation, vignette). Each frame this rebuilds those params from the baseline and layers every
/// active effect on top, then writes once -- so they compose instead of clobbering each other. The
/// material is a single global asset, but each running client has its own instance and one local
/// camera, so writing it is effectively per-local-player (same pattern the old DungeonPostGrade used).
///
/// SETUP: add to the Player prefab; assign the BuckshotPost material. Triggered from
/// PlayerSpawnPositioner (first join) and MatchController.ReturnPlayerToLobby (bench respawn).
/// </summary>
public class ScreenFeedback : NetworkBehaviour
{
    [Header("Post material")]
    [Tooltip("Hidden_BuckshotPost.mat -- the same fullscreen material the post feature uses.")]
    [SerializeField] private Material postMaterial;

    [Header("Wake-up (regain consciousness)")]
    [Tooltip("Total length of the VISUAL effect. Control returns much sooner (Lock Seconds) so it never " +
             "feels sluggish -- vision keeps clearing after you can already move.")]
    [SerializeField] private float wakeDuration = 2.5f;
    [Tooltip("How long movement is locked at the very start -- just the initial blackout blink, where " +
             "moving is pointless because you can't see. Keep short; 0 = never lock.")]
    [SerializeField] private float wakeLockSeconds = 0.4f;
    [Tooltip("Eyelid over the wake. X = progress 0..1, Y = vignette amount. Default opens from closed; " +
             "add a dip back up near the start for a double-blink 'coming to' beat.")]
    [SerializeField] private AnimationCurve wakeVignette = AnimationCurve.EaseInOut(0f, 1f, 1f, 0f);
    [Tooltip("Exposure at the start of the wake (dim/dazed), eased back to base as you come round.")]
    [SerializeField] private float wakeStartExposure = 0.35f;
    [Tooltip("Full-screen blur (virtual px) at the start of the wake, eased to 0 as vision resolves.")]
    [SerializeField] private float wakeStartBlur = 9f;
    [Tooltip("Disoriented camera sway kicked at wake start (fed to CameraFeel's shake channel).")]
    [SerializeField] private float wakeShake = 0.6f;
    [Tooltip("Vignette shape during the wake. 1 = circular, higher = eyelid slit. ~2.5 reads as an eye.")]
    [SerializeField] private float wakeVignetteAspect = 2.5f;

    [Header("Hurt flash")]
    [SerializeField] private float hurtDuration = 0.4f;
    [Tooltip("How grey it goes at the peak of a full hit. Subtle -- a desaturate reads visceral; a red " +
             "wash reads cheap.")]
    [Range(0f, 1f)] [SerializeField] private float hurtDesaturate = 0.6f;
    [Range(0f, 1f)] [SerializeField] private float hurtDarken = 0.25f;
    [Range(0f, 1f)] [SerializeField] private float hurtVignette = 0.35f;

    [Header("Low-stamina vignette")]
    [Tooltip("Stamina fraction below which the vignette starts creeping in.")]
    [Range(0f, 1f)] [SerializeField] private float staminaThreshold = 0.5f;
    [Tooltip("Vignette amount at empty stamina.")]
    [Range(0f, 1f)] [SerializeField] private float staminaVignetteMax = 0.35f;
    [Tooltip("Amber wash the screen tints toward as you tire (Apeirophobia-style). The world browns " +
             "and closes in. Reached at full exertion.")]
    [SerializeField] private Color staminaTint = new Color(0.80f, 0.62f, 0.42f, 1f);
    [Tooltip("How much colour drains at full exertion (0..1). Desaturate + amber tint together are what " +
             "read as brown; either alone doesn't.")]
    [Range(0f, 1f)] [SerializeField] private float staminaDesaturate = 0.45f;
    [Tooltip("Vignette shape for low stamina. 1 = circular tunnel vision (usually right); higher = " +
             "horizontal slit.")]
    [SerializeField] private float staminaVignetteAspect = 1.2f;
    [Tooltip("Breathing pulse DEPTH of the low-stamina vignette. 0 = steady (no throb). Raise for a " +
             "heavy-breathing pulse.")]
    [Range(0f, 0.4f)] [SerializeField] private float staminaPulseAmount = 0f;
    [SerializeField] private float staminaPulseSpeed = 6f;
    [Tooltip("How fast the vignette EASES OUT once you're no longer tired (per sec). Low = a slow " +
             "'catching your breath' fade, so a brief dip to empty doesn't just flash. Rise is instant.")]
    [SerializeField] private float staminaVignetteFade = 0.6f;
    private float _breath;   // smoothed exertion, decoupled from the raw stamina curve

    private PlayerMovement _movement;
    private PlayerStamina _stamina;
    private CameraFeel _cameraFeel;

    private int _idExposure, _idSaturation, _idVignette, _idColorFilter, _idVignetteAspect, _idWakeBlur;
    private float _baseExposure, _baseSaturation;

    private float _wakeT = -1f;    // <0 = inactive; else elapsed seconds
    private bool _wakeHoldingLock;
    private float _hurtT = -1f;

    [Header("Blackout blink (to cover a cut / teleport)")]
    [Tooltip("Length of a full blink: to black and back. The covered action fires at the darkest frame.")]
    [SerializeField] private float blinkDuration = 0.35f;
    private float _blinkT = -1f;
    private System.Action _blinkAtBlack;
    private bool _blinkFired;

    // Persistent blackout: held from spawn until the wake-up clears it, so the spawn / placement / elevator-
    // boarding frames render FULLY BLACK instead of flashing the lobby. BeginBlackout() on; PlayWakeUp() off.
    private bool _blackout;
    public void BeginBlackout()
    {
        _blackout = true;
        // Write the material black RIGHT NOW (not next Update) so the very first rendered frame after spawn
        // is already black -- otherwise the 1-2 frame gap before Update runs is the "split second" flash.
        if (postMaterial != null)
        {
            postMaterial.SetFloat(Shader.PropertyToID("_Exposure"), 0f);
            postMaterial.SetFloat(Shader.PropertyToID("_Vignette"), 1f);
        }
    }

    private void Awake()
    {
        // Capture the AUTHORED baseline + property ids in Awake -- BEFORE any OnNetworkSpawn runs. If we
        // read the base in OnNetworkSpawn, a BeginBlackout() called from PlayerSpawnPositioner.OnNetworkSpawn
        // (which writes exposure = 0) could be captured AS the base, leaving the wake fading toward 0 =
        // permanent black. Awake runs for every component before any OnNetworkSpawn, so the base is clean.
        _idExposure    = Shader.PropertyToID("_Exposure");
        _idSaturation  = Shader.PropertyToID("_Saturation");
        _idVignette    = Shader.PropertyToID("_Vignette");
        _idColorFilter = Shader.PropertyToID("_ColorFilter");
        _idVignetteAspect = Shader.PropertyToID("_VignetteAspect");
        _idWakeBlur    = Shader.PropertyToID("_WakeBlur");

        if (postMaterial != null)
        {
            _baseExposure   = postMaterial.GetFloat(_idExposure);
            _baseSaturation = postMaterial.GetFloat(_idSaturation);
        }
    }

    public override void OnNetworkSpawn()
    {
        if (!IsOwner) { enabled = false; return; }

        _movement = GetComponent<PlayerMovement>();
        _stamina = GetComponent<PlayerStamina>();
        _cameraFeel = GetComponent<CameraFeel>();
    }

    public override void OnNetworkDespawn()
    {
        // Restore neutral params so a despawn mid-effect doesn't leave the shared material dimmed.
        if (IsOwner && postMaterial != null)
        {
            postMaterial.SetFloat(_idExposure, _baseExposure);
            postMaterial.SetFloat(_idSaturation, _baseSaturation);
            postMaterial.SetFloat(_idVignette, 0f);
            postMaterial.SetFloat(_idWakeBlur, 0f);
            postMaterial.SetColor(_idColorFilter, Color.white);
        }
    }

    /// <summary>Local trigger for the wake-up sequence (first join calls this owner-side).</summary>
    public void PlayWakeUp()
    {
        _blackout = false;   // the wake fades IN from black -> release the held blackout
        _wakeT = 0f;
        if (wakeLockSeconds > 0f && _movement != null)
        {
            _movement.MovementLocked = true;
            _wakeHoldingLock = true;
        }
        if (_cameraFeel != null) _cameraFeel.AddShake(wakeShake);
    }

    /// <summary>
    /// Wake-up for the FIRST JOIN, where the connect/load frames hitch badly -- playing the effect then
    /// loses the blink and sway in the stutter. Waits for the frame rate to settle first, so the effect
    /// lands on smooth frames. Bench respawns are already in-session and use PlayWakeUp directly.
    /// </summary>
    public void PlayWakeUpAfterLoad()
    {
        if (IsOwner) StartCoroutine(WakeWhenSmooth());
    }

    private IEnumerator WakeWhenSmooth()
    {
        // Play once we've seen a few consecutive smooth frames (or after a timeout, so it always fires).
        const float smoothFrame = 0.05f;   // 20fps -- loading hitches are far worse than this
        int smoothRun = 0;
        float waited = 0f;
        while (waited < 5f)
        {
            yield return null;
            waited += Time.unscaledDeltaTime;
            smoothRun = Time.unscaledDeltaTime < smoothFrame ? smoothRun + 1 : 0;
            if (smoothRun >= 3) break;
        }
        PlayWakeUp();
    }

    /// <summary>Server-to-owner trigger for the bench-respawn wake-up. MatchController calls this on the
    /// returning player alongside the teleport, so ONLY the lobby return wakes -- not dungeon teleports,
    /// which share NetworkTeleporter.</summary>
    [Rpc(SendTo.Owner)]
    public void PlayWakeUpRpc() => PlayWakeUp();

    /// <summary>Blink to black and back over blinkDuration, invoking `atBlack` at the darkest frame — so
    /// a teleport/cut done in that callback is hidden behind the blackout instead of visibly popping.</summary>
    public void Blink(System.Action atBlack = null)
    {
        _blinkT = 0f;
        _blinkAtBlack = atBlack;
        _blinkFired = false;
    }

    /// <summary>Trigger a hurt flash. `strength` 0..1 scales it.</summary>
    public void PlayHurt(float strength = 1f)
    {
        _hurtT = 0f;
        _hurtStrength = Mathf.Clamp01(strength);
    }
    private float _hurtStrength = 1f;

    private void Update()
    {
        // NOTE: effect timing and the movement UNLOCK run regardless of the material. They used to sit
        // below a `postMaterial == null` early-return, which meant a missing material left the player
        // locked forever after the first wake-up. Only the param WRITES depend on the material now.
        float dt = Time.deltaTime;

        // Start each frame from the authored baseline; layer effects on top.
        float exposure = _baseExposure;
        float saturation = _baseSaturation;
        float vignette = 0f;
        float wakeBlur = 0f;
        Color colorFilter = Color.white;   // white = no tint
        float aspect = staminaVignetteAspect;   // circular-ish by default; wake overrides to the eye shape

        // --- Low-stamina vignette (continuous, smoothed) ---
        // Target exertion: full while EXHAUSTED (ran dry, sprint locked out -- the clearest "gasping"
        // moment), otherwise ramps in below the threshold. Then _breath rises to the target instantly
        // but FALLS slowly, so the vignette eases out over a couple of seconds instead of snapping off
        // with the raw stamina value -- which is why it only flashed before.
        float exertTarget = 0f;
        if (_stamina != null)
        {
            if (_stamina.IsExhausted)
                exertTarget = 1f;
            else if (_stamina.Fraction < staminaThreshold)
            {
                float t = 1f - _stamina.Fraction / Mathf.Max(staminaThreshold, 1e-4f);   // 0 at threshold -> 1 empty
                // sqrt so the vignette gains presence EARLY (when you're near out), not only near empty.
                // A linear ramp is ~0.07 at 40% stamina -- technically on, too faint to read.
                exertTarget = Mathf.Sqrt(t);
            }
        }
        _breath = exertTarget > _breath
            ? exertTarget                                                   // rise instantly
            : Mathf.MoveTowards(_breath, exertTarget, staminaVignetteFade * dt);   // fall slowly
        if (_breath > 0f)
        {
            float pulse = 1f - staminaPulseAmount * (0.5f + 0.5f * Mathf.Sin(Time.time * staminaPulseSpeed));
            vignette = Mathf.Max(vignette, staminaVignetteMax * _breath * pulse);
            // The world browns and drains of colour as you tire -- amber tint + desaturate together.
            colorFilter = Color.Lerp(Color.white, staminaTint, _breath);
            saturation *= 1f - staminaDesaturate * _breath;
        }

        // --- Wake-up (transient) ---
        if (_wakeT >= 0f)
        {
            _wakeT += dt;
            float p = Mathf.Clamp01(_wakeT / Mathf.Max(wakeDuration, 0.01f));

            float wv = wakeVignette.Evaluate(p);
            if (wv >= vignette) aspect = wakeVignetteAspect;   // use the eye shape while the wake dominates
            vignette = Mathf.Max(vignette, wv);
            exposure = Mathf.Lerp(wakeStartExposure, _baseExposure, p);
            saturation = Mathf.Lerp(0f, _baseSaturation, p);
            wakeBlur = Mathf.Lerp(wakeStartBlur, 0f, p);   // vision resolves from blurred to sharp

            // Free movement once the initial blackout passes -- long before the visual finishes.
            if (_wakeHoldingLock && _wakeT >= wakeLockSeconds)
            {
                if (_movement != null) _movement.MovementLocked = false;
                _wakeHoldingLock = false;
            }

            if (p >= 1f) _wakeT = -1f;   // done
        }

        // --- Hurt flash (transient) ---
        if (_hurtT >= 0f)
        {
            _hurtT += dt;
            float p = Mathf.Clamp01(_hurtT / Mathf.Max(hurtDuration, 0.01f));
            float k = (1f - p) * _hurtStrength;   // peaks immediately, recovers
            saturation *= 1f - hurtDesaturate * k;
            exposure   *= 1f - hurtDarken * k;
            vignette    = Mathf.Max(vignette, hurtVignette * k);
            if (p >= 1f) _hurtT = -1f;
        }

        // --- Blackout blink (transient): triangle 0->1->0, fully black at the midpoint ---
        if (_blinkT >= 0f)
        {
            _blinkT += dt;
            float p = Mathf.Clamp01(_blinkT / Mathf.Max(blinkDuration, 0.01f));
            float k = 1f - Mathf.Abs(p * 2f - 1f);   // 0 at ends, 1 at the middle
            vignette = Mathf.Max(vignette, k);
            exposure *= 1f - k;                       // to black at the peak
            saturation *= 1f - k;
            if (!_blinkFired && p >= 0.5f) { _blinkAtBlack?.Invoke(); _blinkAtBlack = null; _blinkFired = true; }
            if (p >= 1f) _blinkT = -1f;
        }

        // Held blackout (spawn -> boarded) overrides everything: full black until the wake releases it.
        if (_blackout) { vignette = 1f; exposure = 0f; saturation = 0f; }

        if (postMaterial != null)
        {
            postMaterial.SetFloat(_idExposure, exposure);
            postMaterial.SetFloat(_idSaturation, saturation);
            postMaterial.SetFloat(_idVignette, vignette);
            postMaterial.SetFloat(_idVignetteAspect, aspect);
            postMaterial.SetFloat(_idWakeBlur, wakeBlur);
            postMaterial.SetColor(_idColorFilter, colorFilter);
        }
    }
}
