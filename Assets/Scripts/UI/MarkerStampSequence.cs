using System;
using System.Collections;
using UnityEngine;

/// <summary>
/// The "collection" cinematic that plays when the player COMMITS to entering the game (Host confirmed /
/// Join connected) — NOT on the Play button. It's the moment the operator delusion collapses: the camera
/// is seized from the Connect wing to a close-up of the player's own marker, a ceiling press stamps it
/// CALLED IN, and then the session starts (the existing blackout/elevator flow takes over).
///
/// Deliberately SEPARATE from MenuCameraDirector's interactive navigation — this is a one-shot cinematic,
/// not a station, so it must never enter the Back/GoTo history. MainMenuController calls Begin(onComplete)
/// instead of starting the session directly; we run the beat, then invoke onComplete to actually StartHost/
/// StartClient.
///
/// Design: HARD CUT to the marker by default (moveDuration = 0). The involuntary jump — camera taken from
/// you the instant you clicked "connect" — is the loss of agency that sells the collection. A pan softens
/// it; keep it a cut unless you want it gentler.
///
/// SETUP: put on a menu object (under menuRoot). Assign:
///   - menuCamera        : the parked menu camera this seizes.
///   - markerPose        : an empty at the close-up framing on the marker (name + amount fill frame).
///   - stampArm          : the press head/shaft; animated straight DOWN by pressDrop then back up.
///   - calledInDecal     : the "CALLED IN" decal object on the marker, LEFT INACTIVE — revealed on contact.
///   - audioSource + clip: the KA-CHUNK.
/// Everything is unscaled-time so it plays regardless of timeScale.
/// </summary>
public class MarkerStampSequence : MonoBehaviour
{
    [Header("Camera")]
    [SerializeField] private Camera menuCamera;
    [Tooltip("Empty at the close-up framing on the marker (signature + amount fill the frame).")]
    [SerializeField] private Transform markerPose;
    [Tooltip("Seconds to move to the marker pose. 0 = HARD CUT (recommended — sells the seizure).")]
    [SerializeField] private float moveDuration = 0f;

    [Header("Focus (Bokeh)")]
    [Tooltip("The camera director, borrowed only to set DoF focus for this shot (reuses its Bokeh/Gaussian " +
             "handling). The stamp pose isn't a View, so without this the marker cuts in out of focus. " +
             "Leave null to leave DoF alone.")]
    [SerializeField] private MenuCameraDirector director;
    [Tooltip("Focus distance for the marker close-up = camera-to-marker distance at markerPose.")]
    [SerializeField] private float markerFocusDistance = 0.5f;
    [Tooltip("Beat to hold on the marker before the press drops (lets the player read their debt).")]
    [SerializeField] private float preStampDelay = 0.6f;

    [Header("Stamp press")]
    [Tooltip("The press head/shaft. Moved straight down (local space) by pressDrop, then retracted.")]
    [SerializeField] private Transform stampArm;
    [Tooltip("The press model(s) to keep HIDDEN during the menu and only show for the stamp (plunger + " +
             "housing). Defaults to stampArm's GameObject if left null.")]
    [SerializeField] private GameObject pressRoot;
    [Tooltip("Local-down distance the press travels to contact the marker.")]
    [SerializeField] private float pressDrop = 0.25f;
    [SerializeField] private float pressDownTime = 0.12f;   // fast, decisive
    [SerializeField] private float contactHold  = 0.35f;
    [SerializeField] private float pressUpTime   = 0.18f;   // the fast full retract

    [Header("Rebound (recoil off the strike)")]
    [Tooltip("After the strike the press kicks back UP this fraction of pressDrop before the full retract " +
             "— the recoil that gives the press weight. 0 = no rebound.")]
    [Range(0f, 0.5f)] [SerializeField] private float reboundFraction = 0.15f;
    [Tooltip("Time for that quick shallow kick.")]
    [SerializeField] private float reboundTime = 0.06f;
    [Tooltip("Tiny beat at the top of the rebound before the fast retract.")]
    [SerializeField] private float reboundHold = 0.04f;

    [Header("Contact effects")]
    [Tooltip("The CALLED IN decal on the marker. Leave INACTIVE in the scene — revealed on contact.")]
    [SerializeField] private GameObject calledInDecal;
    [Tooltip("Decal pops from this scale multiplier to 1 on contact (fresh-ink snap).")]
    [SerializeField] private float decalPopScale = 1.08f;
    [SerializeField] private float decalPopTime  = 0.12f;
    [SerializeField] private AudioSource audioSource;
    [SerializeField] private AudioClip   stampClip;
    [Tooltip("Camera shake magnitude at contact (metres).")]
    [SerializeField] private float shakeMagnitude = 0.03f;
    [SerializeField] private float shakeDuration  = 0.25f;

    [Header("Reveal light")]
    [Tooltip("Light brought UP for the stamp (the House illuminating its verdict). It KEEPS its normal " +
             "intensity during the menu — only rises to stampIntensity for the beat, then restores. Safe " +
             "to use your menu key light. Null = no light change.")]
    [SerializeField] private Light centralLight;
    [Tooltip("Intensity DURING the stamp beat. Set above the light's normal level for a flare; the menu " +
             "keeps whatever intensity the light has in the inspector.")]
    [SerializeField] private float stampIntensity = 3f;
    [Tooltip("Seconds to bring the light up. 0 = hard snap (most brutal); ~0.08 = a fast flare.")]
    [SerializeField] private float lightFadeTime = 0.08f;
    [Tooltip("Rise at the stamp CONTACT (true) or at the cut (false — whole marker shot brighter).")]
    [SerializeField] private bool lightAtContact = true;

    [Header("Cinematic bars")]
    [Tooltip("Optional letterbox bars that slide in for the stamp beat (frames it as a cutscene). Null = off.")]
    [SerializeField] private CinematicBars cinematicBars;
    [Tooltip("Seconds for the bars to slide in as the shot cuts.")]
    [SerializeField] private float barsInTime = 0.4f;

    [Header("Finish")]
    [Tooltip("Beat to hold on the stamped document before starting the session (blackout takes over).")]
    [SerializeField] private float postHold = 0.7f;

    private Vector3 _armRest;
    private bool _running;
    private float _lightBase;   // the light's NORMAL menu intensity — preserved, restored after the beat

    private void Awake()
    {
        if (menuCamera == null) menuCamera = GetComponentInChildren<Camera>(true);
        if (stampArm != null) _armRest = stampArm.localPosition;
        if (calledInDecal != null) calledInDecal.SetActive(false);
        if (centralLight != null) _lightBase = centralLight.intensity;   // its normal menu level — leave it
        if (pressRoot == null && stampArm != null) pressRoot = stampArm.gameObject;
    }

    private void OnEnable()
    {
        HidePress();   // press stays hidden during normal menu browsing
    }

    private void OnDisable()
    {
        // Menu is being hidden (session start / return). Restore the light so the menu is normal next time.
        if (centralLight != null) centralLight.intensity = _lightBase;
    }

    private void ShowPress() { if (pressRoot != null) pressRoot.SetActive(true); }
    private void HidePress() { if (pressRoot != null) pressRoot.SetActive(false); }

    /// <summary>Play the collection cinematic, then invoke onComplete (which starts the session).</summary>
    public void Begin(Action onComplete)
    {
        if (_running) return;
        StartCoroutine(Run(onComplete));
    }

    private IEnumerator Run(Action onComplete)
    {
        _running = true;

        // 0. Bars slide in as the cutscene begins.
        if (cinematicBars != null) cinematicBars.Show(barsInTime);

        // 1. Seize the camera to the marker (hard cut by default).
        yield return MoveCamera();
        if (!lightAtContact) LightUp();   // light the whole marker shot

        // 2. Let the player read their own name + debt.
        yield return Wait(preStampDelay);

        // 3. Reveal the press, then it drops in.
        ShowPress();
        yield return PressArm(down: true);

        // 4. CONTACT — reveal stamp, sound, shake, and the light punches on (the House's verdict).
        RevealDecal();
        if (lightAtContact) LightUp();
        if (audioSource != null && stampClip != null) audioSource.PlayOneShot(stampClip);
        yield return Shake(shakeDuration, shakeMagnitude);
        yield return Wait(Mathf.Max(0f, contactHold - shakeDuration));

        // 5. Recoil off the strike (slight kick up), then the fast full retract, then hide the press again.
        yield return Retract();
        HidePress();

        // 6. Hold on the stamped document.
        yield return Wait(postHold);

        // 7. Hand off — start the session (existing blackout/elevator takes it from here).
        onComplete?.Invoke();
        _running = false;
    }

    private IEnumerator MoveCamera()
    {
        if (menuCamera == null || markerPose == null) yield break;

        if (moveDuration <= 0f)   // hard cut
        {
            menuCamera.transform.SetPositionAndRotation(markerPose.position, markerPose.rotation);
            if (director != null) director.SetFocus(markerFocusDistance);   // focus the marker on the cut
            yield break;
        }
        Vector3 p0 = menuCamera.transform.position;
        Quaternion r0 = menuCamera.transform.rotation;
        float f0 = director != null ? director.ReadFocus() : 0f;
        float t = 0f;
        while (t < 1f)
        {
            t += Time.unscaledDeltaTime / moveDuration;
            float e = Mathf.SmoothStep(0f, 1f, Mathf.Clamp01(t));
            menuCamera.transform.position = Vector3.Lerp(p0, markerPose.position, e);
            menuCamera.transform.rotation = Quaternion.Slerp(r0, markerPose.rotation, e);
            if (director != null) director.SetFocus(Mathf.Lerp(f0, markerFocusDistance, e));  // pull focus with the move
            yield return null;
        }
    }

    // The drop: accelerates into the hit (ease-in).
    private IEnumerator PressArm(bool down)
    {
        if (stampArm == null) yield break;
        Vector3 from = down ? _armRest : _armRest + Vector3.down * pressDrop;
        Vector3 to   = down ? _armRest + Vector3.down * pressDrop : _armRest;
        float dur = down ? pressDownTime : pressUpTime;
        float t = 0f;
        while (t < 1f)
        {
            t += Time.unscaledDeltaTime / Mathf.Max(dur, 1e-4f);
            float e = down ? t * t : Mathf.SmoothStep(0f, 1f, t);
            stampArm.localPosition = Vector3.LerpUnclamped(from, to, e);
            yield return null;
        }
        stampArm.localPosition = to;
    }

    // The recoil: from the struck position, a quick shallow kick UP (rebound), a tiny beat, then a fast
    // full retract to rest. The kick is what gives the press weight.
    private IEnumerator Retract()
    {
        if (stampArm == null) yield break;
        Vector3 contact = _armRest + Vector3.down * pressDrop;
        Vector3 rebound = contact + Vector3.up * (pressDrop * reboundFraction);

        if (reboundFraction > 0f)
        {
            yield return LerpArm(contact, rebound, reboundTime);   // quick kick up off the strike
            if (reboundHold > 0f) yield return Wait(reboundHold);  // brief beat at the top of the recoil
            yield return LerpArm(rebound, _armRest, pressUpTime);  // fast full retract
        }
        else
        {
            yield return LerpArm(contact, _armRest, pressUpTime);
        }
    }

    // Ease-out lerp (fast start, settles) — snappy for the recoil + retract.
    private IEnumerator LerpArm(Vector3 from, Vector3 to, float dur)
    {
        if (stampArm == null) yield break;
        if (dur <= 0f) { stampArm.localPosition = to; yield break; }
        float t = 0f;
        while (t < 1f)
        {
            t += Time.unscaledDeltaTime / dur;
            float c = Mathf.Clamp01(t);
            float e = 1f - (1f - c) * (1f - c);   // ease-out
            stampArm.localPosition = Vector3.LerpUnclamped(from, to, e);
            yield return null;
        }
        stampArm.localPosition = to;
    }

    private void LightUp()
    {
        if (centralLight == null) return;
        StartCoroutine(FadeLight(stampIntensity, lightFadeTime));   // rise from its normal level to the flare
    }

    private IEnumerator FadeLight(float target, float dur)
    {
        if (dur <= 0f) { centralLight.intensity = target; yield break; }
        float start = centralLight.intensity, t = 0f;
        while (t < 1f)
        {
            t += Time.unscaledDeltaTime / dur;
            centralLight.intensity = Mathf.Lerp(start, target, Mathf.Clamp01(t));
            yield return null;
        }
        centralLight.intensity = target;
    }

    private void RevealDecal()
    {
        if (calledInDecal == null) return;
        calledInDecal.SetActive(true);
        StartCoroutine(PopDecal());
    }

    private IEnumerator PopDecal()
    {
        Vector3 baseScale = calledInDecal.transform.localScale;
        Vector3 big = baseScale * decalPopScale;
        float t = 0f;
        while (t < 1f)
        {
            t += Time.unscaledDeltaTime / Mathf.Max(decalPopTime, 1e-4f);
            calledInDecal.transform.localScale = Vector3.Lerp(big, baseScale, Mathf.Clamp01(t));
            yield return null;
        }
        calledInDecal.transform.localScale = baseScale;
    }

    private IEnumerator Shake(float dur, float mag)
    {
        if (menuCamera == null || markerPose == null) { yield return Wait(dur); yield break; }
        Vector3 basePos = markerPose.position;
        float t = 0f;
        while (t < dur)
        {
            t += Time.unscaledDeltaTime;
            float falloff = 1f - (t / dur);                       // decays over the shake
            menuCamera.transform.position = basePos + UnityEngine.Random.insideUnitSphere * mag * falloff;
            yield return null;
        }
        menuCamera.transform.position = basePos;                  // settle exactly back
    }

    private static WaitForSecondsRealtime Wait(float s) => new WaitForSecondsRealtime(Mathf.Max(0f, s));
}
