using UnityEngine;

/// <summary>
/// The single House-OS colour palette. Create ONE asset (Assets → Create → House OS → Palette), set the
/// colours, and reference it from every ThemeColor component. Editing a colour here recolours every element
/// that uses that role — one place to retheme the whole UI.
/// </summary>
[CreateAssetMenu(menuName = "House OS/Palette", fileName = "HouseOSPalette")]
public class HouseOSPalette : ScriptableObject
{
    public Color amber = new Color(0.941f, 0.663f, 0.235f);   // F0A93C — body/voice
    public Color glow  = new Color(1f,     0.776f, 0.420f);   // FFC66B — highlight
    public Color brass = new Color(0.788f, 0.635f, 0.294f);   // C9A24B — frame/chrome
    public Color red   = new Color(0.824f, 0.275f, 0.196f);   // D24632 — house/debt/DUE
    public Color felt  = new Color(0.243f, 0.490f, 0.349f);   // 3E7D59 — you/positive
    public Color dim   = new Color(0.486f, 0.431f, 0.337f);   // 7C6E56 — muted labels
    public Color paper = new Color(0.914f, 0.890f, 0.839f);   // E9E3D6 — neutral
    public Color gold  = new Color(0.910f, 0.722f, 0.294f);   // E8B84B — chips/coins

    public Color Get(ThemeRole r)
    {
        switch (r)
        {
            case ThemeRole.Glow:  return glow;
            case ThemeRole.Brass: return brass;
            case ThemeRole.Red:   return red;
            case ThemeRole.Felt:  return felt;
            case ThemeRole.Dim:   return dim;
            case ThemeRole.Paper: return paper;
            case ThemeRole.Gold:  return gold;
            default:              return amber;
        }
    }

#if UNITY_EDITOR
    private void OnValidate() => ThemeColor.RefreshAll();   // live-recolour everything when the palette changes
#endif
}
