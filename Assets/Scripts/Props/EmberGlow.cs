using UnityEngine;

/// <summary>
/// A living coal/ember flicker for the cigarette in the ashtray (or any small glowing point). A static
/// emissive reads as a dead prop; a coal breathes. This layers three motions the way a real ember moves:
///   - a SLOW waver (the coal's baseline pulse as it burns),
///   - a FASTER fine jitter (surface crackle),
///   - occasional BRIGHT swells (a draft catching it), the "alive" beat the eye locks onto.
/// It drives a Light's intensity and, optionally, an emissive material's HDR color so the mesh tip glows
/// and feeds bloom in sync with the light.
///
/// SETUP: put on the cigarette-tip object. Assign `emberLight` (a small warm Point Light at the coal) and
/// optionally `emberRenderer` (the tip mesh) with `emissiveColor` = the coal color. Tune baseIntensity to
/// the light's resting brightness; everything rides around that, so you set one number.
///
/// Cheap: two PerlinNoise calls per frame, no allocations. Uses realtimeSinceStartup so it keeps breathing
/// in the menu regardless of timeScale, and animates in the editor too ([ExecuteAlways]).
/// </summary>
[ExecuteAlways]
public class EmberGlow : MonoBehaviour
{
    [Header("Refs")]
    [Tooltip("Small warm Point Light at the coal. Its intensity is driven; set color/range on the light itself.")]
    [SerializeField] private Light emberLight;
    [Tooltip("Optional: the ember tip mesh. Its emission is pulsed in sync so glow + bloom track the light.")]
    [SerializeField] private Renderer emberRenderer;
    [ColorUsage(false, true)]
    [Tooltip("HDR emissive color of the coal at full glow (only used if emberRenderer is set).")]
    [SerializeField] private Color emissiveColor = new Color(3.0f, 0.9f, 0.18f);

    [Header("Glow")]
    [Tooltip("Resting light intensity. The flicker rides around this value.")]
    [SerializeField] private float baseIntensity = 0.6f;
    [Tooltip("Depth of the slow baseline waver, as a fraction of base (0.25 = +/-25%).")]
    [Range(0f, 0.9f)] [SerializeField] private float waverDepth = 0.25f;
    [SerializeField] private float waverSpeed = 1.3f;
    [Tooltip("Fine fast crackle on top of the waver.")]
    [Range(0f, 0.5f)] [SerializeField] private float crackleDepth = 0.12f;
    [SerializeField] private float crackleSpeed = 9f;

    [Header("Draft swells")]
    [Tooltip("Extra brightness when a draft catches the coal (fraction of base added at peak).")]
    [Range(0f, 1.5f)] [SerializeField] private float swellStrength = 0.5f;
    [Tooltip("Average seconds between draft swells (randomised +/-50%).")]
    [SerializeField] private float swellInterval = 4f;
    [Tooltip("How long a swell takes to rise and fall.")]
    [SerializeField] private float swellDuration = 0.8f;

    private static readonly int EmissionColorId = Shader.PropertyToID("_EmissionColor");
    private MaterialPropertyBlock _mpb;

    private float _seedA, _seedB;        // decorrelate the two noise channels
    private float _nextSwellTime;        // when the next draft swell fires
    private float _swellElapsed;         // progress through the current swell
    private bool  _swelling;

    private float Now => Time.realtimeSinceStartup;   // unscaled, works in edit + play

    private void OnEnable()
    {
        _seedA = Random.value * 100f;
        _seedB = Random.value * 100f;
        ScheduleSwell();
    }

    private void ScheduleSwell()
    {
        _nextSwellTime = Now + swellInterval * Random.Range(0.5f, 1.5f);
        _swelling = false;
        _swellElapsed = 0f;
    }

    private void Update()
    {
        float t = Now;

        // Baseline waver (slow) + fine crackle (fast). Perlin so both are smooth, not steppy.
        float waver   = (Mathf.PerlinNoise(_seedA, t * waverSpeed)   - 0.5f) * 2f * waverDepth;
        float crackle = (Mathf.PerlinNoise(_seedB, t * crackleSpeed) - 0.5f) * 2f * crackleDepth;

        // Occasional draft swell: a soft sine bump 0 -> peak -> 0 on top of everything.
        float swell = 0f;
        if (!_swelling && t >= _nextSwellTime) _swelling = true;
        if (_swelling)
        {
            _swellElapsed += Time.unscaledDeltaTime;
            float p = Mathf.Clamp01(_swellElapsed / Mathf.Max(swellDuration, 1e-3f));
            swell = Mathf.Sin(p * Mathf.PI) * swellStrength;
            if (p >= 1f) ScheduleSwell();
        }

        float mult = Mathf.Max(0f, 1f + waver + crackle + swell);

        if (emberLight != null) emberLight.intensity = baseIntensity * mult;

        if (emberRenderer != null)
        {
            _mpb ??= new MaterialPropertyBlock();
            emberRenderer.GetPropertyBlock(_mpb);
            _mpb.SetColor(EmissionColorId, emissiveColor * mult);
            emberRenderer.SetPropertyBlock(_mpb);
        }
    }
}
