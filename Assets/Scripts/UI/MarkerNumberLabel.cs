using TMPro;
using UnityEngine;

/// <summary>
/// Writes the player's MARKER NUMBER (= lifetime games played, via PlayerProfile) onto a TMP and keeps it
/// live. Put on the menu's "marker #0447" readout TMP (and/or a runtime overlay on the marker document if
/// you make that number dynamic too). The number climbs each game.
///
/// SETUP: put on the target TMP (or assign `label`). Set `prefix` to whatever precedes the number and
/// `format` for zero-padding.
/// </summary>
public class MarkerNumberLabel : MonoBehaviour
{
    [SerializeField] private TMP_Text label;
    [Tooltip("Text before the number, e.g. \"marker #\" or just \"#\" or \"No. \".")]
    [SerializeField] private string prefix = "marker #";
    [Tooltip("Numeric format — \"0000\" zero-pads to 4 digits.")]
    [SerializeField] private string format = "0000";

    private void Awake() { if (label == null) label = GetComponent<TMP_Text>(); }
    private void OnEnable() { Apply(); PlayerProfile.Changed += Apply; }
    private void OnDisable() { PlayerProfile.Changed -= Apply; }

    private void Apply()
    {
        if (label != null) label.text = prefix + PlayerProfile.MarkerNumber.ToString(format);
    }
}
