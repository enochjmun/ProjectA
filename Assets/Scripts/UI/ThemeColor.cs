using UnityEngine;
using UnityEngine.UI;
using TMPro;

/// <summary>Named colour roles in the House-OS palette.</summary>
public enum ThemeRole { Amber, Glow, Brass, Red, Felt, Dim, Paper, Gold }

/// <summary>
/// Colours a TMP text or UGUI Image from the shared HouseOSPalette by ROLE — so changing one colour in the
/// palette asset recolours every element that uses it. Put this on each themed UI element, assign the
/// palette, pick a role (and optional alpha). Runs in edit mode, so the Scene view updates live.
///
/// For rich-text spans (e.g. an inline coloured word), the tag still wins per-character; this sets the
/// element's BASE colour.
/// </summary>
[ExecuteAlways]
public class ThemeColor : MonoBehaviour
{
    public HouseOSPalette palette;
    public ThemeRole role = ThemeRole.Amber;
    [Range(0f, 1f)] public float alpha = 1f;

    private void OnEnable() => Apply();
    private void OnValidate() => Apply();

    public void Apply()
    {
        if (palette == null) return;
        Color c = palette.Get(role);
        c.a = alpha;

        var tmp = GetComponent<TMP_Text>();
        if (tmp != null) { tmp.color = c; return; }

        var g = GetComponent<Graphic>();
        if (g != null) g.color = c;
    }

    /// <summary>Re-apply every ThemeColor in the loaded scenes — called by the palette when it changes.</summary>
    public static void RefreshAll()
    {
        var all = Object.FindObjectsByType<ThemeColor>(FindObjectsInactive.Include, FindObjectsSortMode.None);
        foreach (var t in all) t.Apply();
    }
}
