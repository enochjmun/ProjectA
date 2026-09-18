using UnityEngine;

/// <summary>
/// A DEAD monitor for the security wall -- CRT static, a slow roll bar, hum, and the occasional signal
/// drop. Most of the ~10 screens should be these, not live feeds: they set the "wall of surveillance"
/// mood for almost nothing. Where a live MenuMonitorFeed is a whole second camera per screen, this is pure
/// MATERIAL-PROPERTY animation -- no camera, no RenderTexture, no per-frame texture uploads.
///
/// LOCAL, cosmetic, menu-only (lives under menuRoot). It scrolls a noise texture for static, slides a
/// bright band down the screen for the roll bar, wobbles emission for hum, and now and then dims to a
/// near-dead flicker (lost signal). Each screen seeds itself so the wall doesn't flicker in unison.
///
/// SETUP (manual): put this on a monitor screen mesh (or assign `screenRenderer`). Give the screen a
/// noise/static texture on its `textureProperty` (any grayscale noise; unlit/emissive material reads
/// best). If you leave the texture alone it still hums and rolls via emission + offset. Match the
/// material's property names.
/// </summary>
public class MenuStaticScreen : MonoBehaviour
{
    [Header("Screen surface")]
    [Tooltip("The monitor screen renderer. Defaults to this object's Renderer if left empty.")]
    [SerializeField] private Renderer screenRenderer;
    [SerializeField] private int screenMaterialIndex = 0;
    [Tooltip("Texture property whose UV offset is scrolled for the static crawl (URP = _BaseMap).")]
    [SerializeField] private string textureProperty = "_BaseMap";
    [Tooltip("Emission property wobbled for hum + the roll bar. Blank to skip emission animation.")]
    [SerializeField] private string emissionProperty = "_EmissionColor";

    [Header("Look")]
    [Tooltip("Base emission colour of the dead screen -- dim grey-blue phosphor.")]
    [SerializeField] private Color baseEmission = new Color(0.12f, 0.14f, 0.16f);
    [Tooltip("How fast the static texture crawls (UV offset per second).")]
    [SerializeField] private Vector2 scrollSpeed = new Vector2(0.9f, 2.3f);
    [Tooltip("Amplitude of the emission hum wobble (fraction of base).")]
    [Range(0f, 1f)] [SerializeField] private float humAmount = 0.25f;
    [SerializeField] private float humSpeed = 9f;
    [Tooltip("Extra brightness of the roll bar as it sweeps (added to emission at its peak).")]
    [SerializeField] private float rollBrightness = 0.5f;
    [Tooltip("Seconds for the roll bar to travel the screen once.")]
    [SerializeField] private float rollPeriod = 3.5f;

    [Header("Signal drop")]
    [Tooltip("Expected lost-signal dropouts per second. 0 = never.")]
    [Range(0f, 5f)] [SerializeField] private float dropoutsPerSecond = 0.25f;

    private Material _mat;
    private int _texId, _emitId;
    private bool _hasEmission;
    private Vector2 _offset;
    private float _seed;
    private float _dropUntil;

    private void Awake()
    {
        if (screenRenderer == null) screenRenderer = GetComponent<Renderer>();
        if (screenRenderer == null) { enabled = false; return; }

        int i = Mathf.Clamp(screenMaterialIndex, 0, screenRenderer.materials.Length - 1);
        _mat = screenRenderer.materials[i];
        _texId = Shader.PropertyToID(textureProperty);
        _hasEmission = !string.IsNullOrEmpty(emissionProperty);
        if (_hasEmission)
        {
            _emitId = Shader.PropertyToID(emissionProperty);
            _mat.EnableKeyword("_EMISSION");
        }

        _seed = Random.value * 100f;
        _offset = new Vector2(Random.value, Random.value);   // start out of phase
    }

    private void Update()
    {
        float dt = Time.unscaledDeltaTime;

        // Static crawl: keep nudging the texture offset (wrap keeps it in range).
        _offset += scrollSpeed * dt;
        _offset.x %= 1f; _offset.y %= 1f;
        _mat.SetTextureOffset(_texId, _offset);

        if (!_hasEmission) return;

        // Hum: smooth Perlin wobble around the base brightness.
        float hum = 1f - humAmount * (1f - Mathf.PerlinNoise(_seed, Time.unscaledTime * humSpeed));

        // Roll bar: a bright band whose position sweeps top-to-bottom; peaks briefly each period.
        float rollPhase = (Time.unscaledTime / Mathf.Max(0.01f, rollPeriod) + _seed) % 1f;
        float roll = Mathf.Pow(Mathf.Clamp01(1f - Mathf.Abs(rollPhase - 0.5f) * 2f), 6f) * rollBrightness;

        // Signal drop: occasionally collapse to a near-dead screen for a beat.
        if (Time.unscaledTime < _dropUntil) hum *= 0.1f;
        else if (Random.value < dropoutsPerSecond * dt) _dropUntil = Time.unscaledTime + Random.Range(0.04f, 0.18f);

        _mat.SetColor(_emitId, baseEmission * hum + Color.white * roll);
    }
}
