using UnityEngine;

/// <summary>
/// Swaps a monitor's screen material between its idle look (static / feed) and a plain black "console"
/// material while a menu view is active -- so a wing screen shows static normally, then goes black the
/// moment you pan to it and its UI panel appears over the (now black, curved) glass. Because the swap
/// happens on the SCREEN MESH itself, the black follows the screen's curve exactly, so a flat UI canvas
/// on a curved CRT no longer leaks static at the corners.
///
/// Put this on the wing's UI panel -- the GameObject the MenuCameraDirector enables/disables per view.
/// OnEnable (view becomes active) swaps the glass to black; OnDisable (you leave) restores the idle
/// material. The UI canvas can then be transparent white-on-black like the hero.
///
/// SETUP: assign `screenRenderer` = the wing monitor's screen renderer, `materialIndex` = the screen slot,
/// and `activeMaterial` = a plain opaque black (or very dark) material. The idle material is captured
/// automatically from whatever is on the slot at start (your DeadCRTScreen or feed material).
/// </summary>
public class ScreenTakeover : MonoBehaviour
{
    [Tooltip("Renderer of the wing monitor's screen.")]
    [SerializeField] private Renderer screenRenderer;
    [Tooltip("Which material slot is the screen glass.")]
    [SerializeField] private int materialIndex = 0;
    [Tooltip("Material shown while this view is active -- a plain opaque black, so the curved glass reads " +
             "as a dark console behind the UI.")]
    [SerializeField] private Material activeMaterial;

    private Material _idleMaterial;   // captured from the slot at start (static / feed)
    private bool _captured;

    private void Awake() => Capture();

    private void Capture()
    {
        if (_captured || screenRenderer == null) return;
        var mats = screenRenderer.sharedMaterials;
        if (materialIndex >= 0 && materialIndex < mats.Length)
        {
            _idleMaterial = mats[materialIndex];
            _captured = true;
        }
    }

    private void OnEnable()
    {
        Capture();          // in case OnEnable runs before Awake on first show
        Swap(activeMaterial);
    }

    private void OnDisable() => Swap(_idleMaterial);

    // Point the screen slot at a different material asset (doesn't modify either asset).
    private void Swap(Material m)
    {
        if (screenRenderer == null || m == null) return;
        var mats = screenRenderer.sharedMaterials;
        if (materialIndex < 0 || materialIndex >= mats.Length) return;
        mats[materialIndex] = m;
        screenRenderer.sharedMaterials = mats;
    }
}
