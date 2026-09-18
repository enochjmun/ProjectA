using UnityEngine;

/// <summary>
/// Cosmetic light flicker for dungeon lamps -- failing-electrics unease. Purely LOCAL/visual: not
/// networked and it never touches gameplay (the lamps aren't a player light stimulus), so each client
/// can flicker independently. Captures the light's authored intensity, then modulates it with smooth
/// Perlin noise plus occasional brief hard stutters (a dying-tube blink).
///
/// Reusable on ANY Light: the DungeonGenerator adds it to a fraction of the code-built greybox lamps
/// now, and a "flickering" prefab VARIANT can carry it pre-attached once the modular art lands.
/// </summary>
[RequireComponent(typeof(Light))]
public class LightFlicker : MonoBehaviour
{
    [Tooltip("Deepest the intensity dips on the smooth wobble (0 = none, 1 = down to zero).")]
    [Range(0f, 1f)] public float flickerAmount = 0.35f;
    [Tooltip("Speed of the smooth underlying wobble.")]
    public float flickerSpeed = 8f;
    [Tooltip("Expected number of brief hard stutters (near-blackout blinks) per second. 0 = none.")]
    [Range(0f, 10f)] public float stutterPerSecond = 0.6f;

    private Light _light;
    private float _base;
    private float _noiseSeed;
    private float _stutterUntil;

    private void Awake()
    {
        _light = GetComponent<Light>();
        _noiseSeed = Random.value * 100f;   // each lamp flickers out of phase
    }

    // Captured in Start so it reads the intensity AFTER the generator (or the prefab) has set it.
    private void Start() => _base = _light.intensity;

    private void Update()
    {
        // Smooth underlying wobble: Perlin noise in [0,1] -> intensity factor.
        float n = Mathf.PerlinNoise(_noiseSeed, Time.time * flickerSpeed);
        float factor = 1f - flickerAmount * (1f - n);

        // Occasional brief hard stutter overlaid on the wobble.
        if (Time.time < _stutterUntil)
            factor *= 0.15f;
        else if (Random.value < stutterPerSecond * Time.deltaTime)
            _stutterUntil = Time.time + Random.Range(0.03f, 0.12f);

        _light.intensity = _base * factor;
    }
}
