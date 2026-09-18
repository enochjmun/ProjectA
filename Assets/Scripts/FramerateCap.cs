using UnityEngine;

/// <summary>
/// Caps the framerate for dev/playtest so an uncapped GPU doesn't pin at 100% (heat, fan noise, and
/// battery drain on laptops). Gameplay is framerate-independent (Time.deltaTime + exponential eases
/// everywhere), so this cap only affects performance/comfort — never feel. Persists across scene loads.
///
/// SHIPPING NOTE: the final game should expose this as a player VIDEO SETTING (a VSync toggle + an fps-cap
/// dropdown, saved to PlayerPrefs and re-applied on launch). This component is the dev-time default until
/// that options menu exists.
///
/// SETUP: put on ONE GameObject in your first-loaded scene. It survives scene loads and dedupes itself,
/// so a copy in a later scene won't stack.
/// </summary>
public class FramerateCap : MonoBehaviour
{
    [Tooltip("Target frames per second. 60 is plenty for this game and keeps laptops cool and quiet. " +
             "0 = uncapped (not recommended for a laptop).")]
    [SerializeField] private int targetFps = 60;

    private static FramerateCap _instance;

    private void Awake()
    {
        // Keep one survivor across scenes; a duplicate spawned by a reloaded scene destroys itself.
        if (_instance != null && _instance != this) { Destroy(gameObject); return; }
        _instance = this;
        DontDestroyOnLoad(gameObject);

        Apply();
    }

    private void Apply()
    {
        QualitySettings.vSyncCount = 0;              // MUST be 0 — VSync overrides targetFrameRate
        Application.targetFrameRate = targetFps;     // 0 would mean "platform default" (usually uncapped)
        Debug.Log($"[FramerateCap] applied targetFrameRate={Application.targetFrameRate}, vSync={QualitySettings.vSyncCount}", this);
    }

    // Re-apply live if you scrub targetFps in the Inspector during play (handy while tuning).
    private void OnValidate()
    {
        if (Application.isPlaying && _instance == this) Apply();
    }
}
