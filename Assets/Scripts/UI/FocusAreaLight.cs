using UnityEngine;

/// <summary>
/// A diegetic area light that strikes to life when its menu station is focused and fades out when you pan
/// away -- so panning to a wing "turns on" that side and reveals its rack/props in a pool, while the rest
/// of the room stays dark. Reads as the surveillance system lighting the zone it's watching (or the
/// operator's work light coming on). One pool at a time keeps the dark, screen-lit look intact.
///
/// Drives one or more real Lights (faded by their authored intensity) plus, optionally, a visible fixture
/// mesh's emission -- so the bulb/tube GLOWS as it strikes, motivating the light instead of it appearing
/// from nowhere. The strike is a period-fixture stutter (a fluorescent/bulb taking a beat to catch), which
/// reads as the machine waking rather than a hard switch.
///
/// LOCAL, cosmetic. Put on the station's light group; drive it from the same focus panel the director
/// toggles (StandbyScreen / ScreenTakeover use the same signal).
///
/// SETUP: assign `focusPanel` = the view's MenuPanel (light on while it's active); `lights` = the light(s)
/// for that side (author each at its target intensity -- this fades 0..that). Optional: `fixtureRenderer`
/// + `fixtureOnEmission` to make the bulb glow with the strike.
/// </summary>
public class FocusAreaLight : MonoBehaviour
{
    [Header("Focus")]
    [Tooltip("The view's MenuPanel (director-toggled). The light is on while this is active.")]
    [SerializeField] private GameObject focusPanel;

    [Header("Lights")]
    [Tooltip("Lights for this station. Each is authored at its ON intensity; this fades it 0..that.")]
    [SerializeField] private Light[] lights;
    [Tooltip("How fast the pool fades in/out once struck (per second).")]
    [SerializeField] private float fadeSpeed = 5f;
    [Tooltip("Idle level when NOT focused (0 = fully off; ~0.15 = a subtle fill so the station never goes black).")]
    [Range(0f, 1f)] [SerializeField] private float offLevel = 0.15f;

    [Header("Strike-flicker warm-up")]
    [Tooltip("Stutter the light on like a period tube/bulb striking, instead of a hard switch.")]
    [SerializeField] private bool strikeFlicker = true;
    [Tooltip("How long the strike stutter lasts before it settles to steady on.")]
    [SerializeField] private float strikeDuration = 0.6f;

    [Header("Fixture glow (optional)")]
    [Tooltip("A visible bulb/tube mesh whose emission glows with the light, so the source reads.")]
    [SerializeField] private Renderer fixtureRenderer;
    [SerializeField] private int fixtureMaterialIndex = 0;
    [SerializeField] private string fixtureEmissionProperty = "_EmissionColor";
    [ColorUsage(true, true)]
    [SerializeField] private Color fixtureOnEmission = new Color(1f, 0.85f, 0.6f);

    private float[] _baseIntensity;
    private float _level;          // 0 off .. 1 full
    private bool _wasFocused;
    private bool _striking;
    private float _strikeT;
    private Material _fixtureMat;
    private int _emitId;

    private void Awake()
    {
        _baseIntensity = new float[lights != null ? lights.Length : 0];
        for (int i = 0; i < _baseIntensity.Length; i++)
            _baseIntensity[i] = lights[i] != null ? lights[i].intensity : 0f;

        if (fixtureRenderer != null)
        {
            int idx = Mathf.Clamp(fixtureMaterialIndex, 0, fixtureRenderer.materials.Length - 1);
            _fixtureMat = fixtureRenderer.materials[idx];
            _emitId = Shader.PropertyToID(fixtureEmissionProperty);
            _fixtureMat.EnableKeyword("_EMISSION");
        }

        _level = offLevel;
        Apply(offLevel);   // start at the idle fill, not fully dark
    }

    private void Update()
    {
        bool focused = focusPanel != null && focusPanel.activeInHierarchy;
        if (focused != _wasFocused)
        {
            _wasFocused = focused;
            if (focused && strikeFlicker) { _striking = true; _strikeT = 0f; }
        }

        if (_striking)
        {
            _strikeT += Time.unscaledDeltaTime;
            float p = Mathf.Clamp01(_strikeT / Mathf.Max(0.01f, strikeDuration));
            // Mostly-off stutter early, catching more often as it settles, then hand off to the smooth fade.
            float flash = (Random.value < Mathf.Lerp(0.25f, 0.95f, p)) ? 1f : Random.Range(0f, 0.25f);
            _level = Mathf.Max(offLevel, flash * p);   // never dip below the idle fill during the strike
            if (_strikeT >= strikeDuration) _striking = false;
        }
        else
        {
            _level = Mathf.MoveTowards(_level, focused ? 1f : offLevel, fadeSpeed * Time.unscaledDeltaTime);
        }

        Apply(_level);
    }

    private void Apply(float level)
    {
        for (int i = 0; i < _baseIntensity.Length; i++)
            if (lights[i] != null) lights[i].intensity = _baseIntensity[i] * level;

        if (_fixtureMat != null)
            _fixtureMat.SetColor(_emitId, fixtureOnEmission * level);
    }
}
