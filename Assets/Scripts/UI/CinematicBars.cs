using System.Collections;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// Letterbox (cinematic) bars that slide in for a cutscene and out after. Used by MarkerStampSequence to
/// frame the stamp beat as a cutscene -- a deliberate break from the interactive menu that says "this is
/// happening TO you now." Self-contained: it builds its own screen-space overlay canvas + two black bars at
/// runtime, so there's no UI to wire. Just drop it on a menu object and call Show()/Hide().
///
/// Each bar is a fixed fraction of screen height (`barFraction`), so it's a consistent thickness on every
/// resolution and aspect. Runs on unscaled time (menu-safe).
///
/// SETUP: put on a menu object (under menuRoot, so it hides with the menu). Assign it to
/// MarkerStampSequence.cinematicBars. That's it.
/// </summary>
public class CinematicBars : MonoBehaviour
{
    // Pure black letterbox bars (per request). Note: overlay bars render AFTER BuckshotPost, so against the
    // warm-graded image a clinical #000 can read as a digital hole rather than film — if that bothers you,
    // nudge back toward a near-black warm charcoal (~#0B0A08). Kept black by default now.
    [SerializeField] private Color barColor = new Color(0f, 0f, 0f, 1f);  // #000000
    [Tooltip("Height of EACH bar as a fraction of screen height. Fixed fraction (not aspect-based) so the " +
             "bars are a consistent thickness on every resolution and aspect. ~0.11 = a filmic letterbox.")]
    [Range(0f, 0.25f)] [SerializeField] private float barFraction = 0.11f;
    [Tooltip("Canvas sorting order — high so the bars sit over everything else.")]
    [SerializeField] private int sortingOrder = 500;

    private RectTransform _top, _bottom;
    private Coroutine _co;

    private void Awake()
    {
        Build();
        SetHeight(0f);   // start hidden
    }

    private void Build()
    {
        var go = new GameObject("CinematicBarsCanvas");
        go.transform.SetParent(transform, false);
        var canvas = go.AddComponent<Canvas>();
        canvas.renderMode = RenderMode.ScreenSpaceOverlay;
        canvas.sortingOrder = sortingOrder;
        go.AddComponent<CanvasScaler>();

        _top    = MakeBar(go.transform, "TopBar",    new Vector2(0, 1), new Vector2(1, 1), new Vector2(0.5f, 1));
        _bottom = MakeBar(go.transform, "BottomBar", new Vector2(0, 0), new Vector2(1, 0), new Vector2(0.5f, 0));
    }

    private RectTransform MakeBar(Transform parent, string n, Vector2 aMin, Vector2 aMax, Vector2 pivot)
    {
        var g = new GameObject(n, typeof(Image));
        g.transform.SetParent(parent, false);
        var img = g.GetComponent<Image>();
        img.color = barColor;
        img.raycastTarget = false;                       // never eat clicks
        var rt = g.GetComponent<RectTransform>();
        rt.anchorMin = aMin; rt.anchorMax = aMax; rt.pivot = pivot;
        rt.anchoredPosition = Vector2.zero;
        rt.sizeDelta = Vector2.zero;                     // full width (stretched), 0 height
        return rt;
    }

    // Height of ONE bar = a fixed fraction of screen height, so it's the same thickness on any aspect.
    private float BarHeight() => barFraction * Screen.height;

    private void SetHeight(float px)
    {
        if (_top != null)    _top.sizeDelta    = new Vector2(0f, px);
        if (_bottom != null) _bottom.sizeDelta = new Vector2(0f, px);
    }

    // Play-mode sanity check: right-click the component header -> "Test: Show / Hide Bars".
    [ContextMenu("Test: Show Bars")] private void TestShow() => Show(0.4f);
    [ContextMenu("Test: Hide Bars")] private void TestHide() => Hide(0.4f);

    /// <summary>Slide the bars IN over `dur` seconds (0 = instant).</summary>
    public void Show(float dur) => Animate(BarHeight(), dur);

    /// <summary>Slide the bars OUT over `dur` seconds (0 = instant).</summary>
    public void Hide(float dur) => Animate(0f, dur);

    private void Animate(float target, float dur)
    {
        if (_co != null) StopCoroutine(_co);
        _co = StartCoroutine(Run(target, dur));
    }

    private IEnumerator Run(float target, float dur)
    {
        if (dur <= 0f) { SetHeight(target); yield break; }
        float start = _top != null ? _top.sizeDelta.y : 0f;
        float t = 0f;
        while (t < 1f)
        {
            t += Time.unscaledDeltaTime / dur;
            SetHeight(Mathf.Lerp(start, target, Mathf.SmoothStep(0f, 1f, Mathf.Clamp01(t))));
            yield return null;
        }
        SetHeight(target);
    }
}
