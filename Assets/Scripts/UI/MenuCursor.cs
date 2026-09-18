using UnityEngine;

/// <summary>
/// Swaps the OS pointer for a themed cursor while the menu is active — a surveillance/operator RETICLE fits
/// the security-room fantasy better than an arrow. Uses the hardware cursor (Cursor.SetCursor) so it's crisp
/// and lag-free; reverts to the default pointer when the menu hides.
///
/// SETUP: put on menuRoot (so it applies while the menu is shown and reverts on session start). Assign
/// `cursorTexture` = a small (≤32x32) reticle texture, imported as Texture Type = Cursor (or Default with
/// Read/Write ON). Leave `centerHotspot` on so the crosshair centre is the click point.
/// </summary>
public class MenuCursor : MonoBehaviour
{
    [Tooltip("Reticle texture. ≤32x32 for a hardware cursor. Import as Texture Type = Cursor (or Read/Write ON).")]
    [SerializeField] private Texture2D cursorTexture;
    [Tooltip("Use the texture's centre as the click hotspot (right for a crosshair). Off = use the value below.")]
    [SerializeField] private bool centerHotspot = true;
    [SerializeField] private Vector2 hotspot = new Vector2(16f, 16f);

    private void OnEnable() => Apply();
    private void OnDisable() => Cursor.SetCursor(null, Vector2.zero, CursorMode.Auto);   // back to the OS pointer

    private void Apply()
    {
        Vector2 hs = (centerHotspot && cursorTexture != null)
            ? new Vector2(cursorTexture.width * 0.5f, cursorTexture.height * 0.5f)
            : hotspot;
        Cursor.SetCursor(cursorTexture, hs, CursorMode.Auto);
    }

#if UNITY_EDITOR
    private void OnValidate() { if (isActiveAndEnabled) Apply(); }
#endif
}
