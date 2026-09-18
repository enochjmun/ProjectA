using UnityEngine;
#if ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem;
#endif

/// <summary>
/// Escape / pause overlay. On Escape it ramps the PauseOverlay full-screen material (blur + House-OS
/// scanlines + tint) in and shows the pause UI on top — the house's screen powering on over your view.
///
/// IMPORTANT — this does NOT stop the game (multiplayer: the sim keeps running for everyone). You're still
/// vulnerable with the menu open, which is intended. For a single-player build you could add Time.timeScale
/// = 0 in SetPaused, but leave it alone here.
///
/// SETUP: put on a persistent gameplay object. Assign `overlayMaterial` = the material used by the Pause
/// Full Screen Pass Renderer Feature (added AFTER BuckshotPost). Assign `pauseUI` = the House-OS pause menu
/// canvas (RESUME / SETTINGS / LEAVE). Wire the RESUME button to Resume().
/// </summary>
public class PauseMenu : MonoBehaviour
{
    [SerializeField] private Material overlayMaterial;
    [SerializeField] private GameObject pauseUI;
    [Tooltip("How fast the blur/scanlines ease in/out.")]
    [SerializeField] private float fadeSpeed = 6f;
    [Tooltip("How fast the power-on scan band sweeps down once on open.")]
    [SerializeField] private float rollSpeed = 2.5f;
    [Tooltip("Free + show the cursor while paused (relocks on resume). Turn off if another system owns the cursor.")]
    [SerializeField] private bool manageCursor = true;

    private bool _paused;
    private float _amount, _roll = 1f;
    private int _amountId, _rollId;

    private void Awake()
    {
        _amountId = Shader.PropertyToID("_Amount");
        _rollId = Shader.PropertyToID("_ScanRoll");
        SetAmount(0f);
        if (pauseUI != null) pauseUI.SetActive(false);
    }

    private void OnDisable() => SetAmount(0f);   // never leave the overlay stuck on

    private void Update()
    {
        if (EscapePressed()) Toggle();

        _amount = Mathf.MoveTowards(_amount, _paused ? 1f : 0f, fadeSpeed * Time.unscaledDeltaTime);
        SetAmount(_amount);

        if (_paused && _roll < 1f)   // sweep the band down once on open
        {
            _roll = Mathf.MoveTowards(_roll, 1f, rollSpeed * Time.unscaledDeltaTime);
            if (overlayMaterial != null) overlayMaterial.SetFloat(_rollId, _roll);
        }
    }

    public void Toggle() => SetPaused(!_paused);
    public void Resume() => SetPaused(false);

    public void SetPaused(bool p)
    {
        _paused = p;
        if (p) { _roll = 0f; if (overlayMaterial != null) overlayMaterial.SetFloat(_rollId, 0f); }   // restart the sweep
        if (pauseUI != null) pauseUI.SetActive(p);
        if (manageCursor)
        {
            Cursor.lockState = p ? CursorLockMode.None : CursorLockMode.Locked;
            Cursor.visible = p;
        }
    }

    private void SetAmount(float a) { if (overlayMaterial != null) overlayMaterial.SetFloat(_amountId, a); }

    private static bool EscapePressed()
    {
#if ENABLE_INPUT_SYSTEM
        if (Keyboard.current != null) return Keyboard.current.escapeKey.wasPressedThisFrame;
#endif
#if ENABLE_LEGACY_INPUT_MANAGER
        return Input.GetKeyDown(KeyCode.Escape);
#else
        return false;
#endif
    }
}
