using System;
using System.Collections;
using UnityEngine;

/// <summary>
/// Projector signal-stutter transition for the lobby MAIN SCREEN. Drives the ProjectorScreen material's
/// _Brightness through a quick irregular flicker down to near-dark and back — the lamp hiccuping, NOT a CRT
/// scanline sweep. Because a projector/terminal CUTS rather than fades, the intended use is to SWAP the
/// screen's content at the dark point so the change is hidden inside the flicker:
///
///   screenTransition.Transition(() => ShowStandings());   // flicker out → swap → flicker in
///
/// Or just Flicker() for an ambient hiccup with no content change (dread punctuation).
///
/// Uses a MaterialPropertyBlock so it never writes to the shared material asset (avoids the stuck-state
/// class of bug). Always restores brightness, even if interrupted.
///
/// SETUP: put on the panel (or assign targetRenderer = the projector panel's Renderer). Make sure its
/// material uses CasinoHorror/ProjectorScreen (so _Brightness exists).
/// </summary>
public class ScreenTransition : MonoBehaviour
{
    [SerializeField] private Renderer targetRenderer;
    [Tooltip("Shader property to drive. ProjectorScreen exposes _Brightness.")]
    [SerializeField] private string brightnessProp = "_Brightness";
    [Tooltip("Normal brightness. 0 = auto-read from the material at Awake.")]
    [SerializeField] private float baseBrightness = 0f;

    [Header("Feel")]
    [Tooltip("Brightness at the darkest point of the transition (0 = black).")]
    [Range(0f, 1f)] [SerializeField] private float darkLevel = 0.06f;
    [Tooltip("Seconds of stuttering on the way down.")]
    [SerializeField] private float outTime = 0.16f;
    [Tooltip("Seconds held dark (content swap happens here).")]
    [SerializeField] private float holdDark = 0.05f;
    [Tooltip("Seconds of stuttering on the way back up.")]
    [SerializeField] private float inTime = 0.18f;
    [Tooltip("How many on/off stutters per phase (the hiccup).")]
    [SerializeField] private int stutters = 4;

    private MaterialPropertyBlock _mpb;
    private int _propId;
    private Coroutine _running;

    private void Awake()
    {
        if (targetRenderer == null) targetRenderer = GetComponent<Renderer>();
        _propId = Shader.PropertyToID(brightnessProp);
        _mpb = new MaterialPropertyBlock();
        if (baseBrightness <= 0f && targetRenderer != null && targetRenderer.sharedMaterial != null
            && targetRenderer.sharedMaterial.HasProperty(_propId))
            baseBrightness = targetRenderer.sharedMaterial.GetFloat(_propId);
        if (baseBrightness <= 0f) baseBrightness = 1.15f;   // ProjectorScreen default
    }

    private void OnDisable()
    {
        if (_running != null) StopCoroutine(_running);
        Set(baseBrightness);   // never leave the screen stuck dark
    }

    /// <summary>Flicker out, run <paramref name="atDark"/> (swap content), flicker back in.</summary>
    public void Transition(Action atDark = null)
    {
        if (_running != null) StopCoroutine(_running);
        _running = StartCoroutine(Run(atDark));
    }

    /// <summary>Ambient hiccup — a quick flicker with no content change.</summary>
    public void Flicker()
    {
        if (_running != null) StopCoroutine(_running);
        _running = StartCoroutine(Run(null, quick: true));
    }

    // ---- Quick tests: enter Play, then right-click this component's header in the Inspector ----
    [ContextMenu("Test/Flicker")]
    private void TestFlicker()
    {
        if (Application.isPlaying) Flicker();
        else Debug.Log("[ScreenTransition] Enter Play mode first.");
    }

    [ContextMenu("Test/Transition (swap point logs)")]
    private void TestTransition()
    {
        if (Application.isPlaying) Transition(() => Debug.Log("[ScreenTransition] content-swap point (dark)"));
        else Debug.Log("[ScreenTransition] Enter Play mode first.");
    }

    private IEnumerator Run(Action atDark, bool quick = false)
    {
        yield return Stutter(baseBrightness, quick ? darkLevel * 2f : darkLevel, quick ? outTime * 0.6f : outTime);

        atDark?.Invoke();                                  // swap the screen content in the dark
        if (!quick && holdDark > 0f) yield return new WaitForSeconds(holdDark);

        yield return Stutter(quick ? darkLevel * 2f : darkLevel, baseBrightness, quick ? inTime * 0.6f : inTime);
        Set(baseBrightness);
        _running = null;
    }

    // Irregular on/off steps between two brightness levels — the projector lamp hiccuping.
    private IEnumerator Stutter(float from, float to, float dur)
    {
        int steps = Mathf.Max(1, stutters);
        float step = dur / steps;
        for (int i = 0; i < steps; i++)
        {
            float t = (i + 1f) / steps;
            float target = Mathf.Lerp(from, to, t);
            // dip past target on odd steps for the stutter, then land
            float dipped = (i % 2 == 0) ? Mathf.Min(from, to) * UnityEngine.Random.Range(0.6f, 1f) : target;
            Set(dipped);
            yield return new WaitForSeconds(step * UnityEngine.Random.Range(0.5f, 1.2f));
            Set(target);
        }
    }

    private void Set(float b)
    {
        if (targetRenderer == null) return;
        targetRenderer.GetPropertyBlock(_mpb);
        _mpb.SetFloat(_propId, b);
        targetRenderer.SetPropertyBlock(_mpb);
    }
}
