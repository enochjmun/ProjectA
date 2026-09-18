using TMPro;
using UnityEngine;

/// <summary>
/// Draws the current remaining debt onto the marker's (now blank) AMOUNT DUE line and keeps it live.
/// SEPARATE from the texture on purpose: Marker.png no longer bakes an amount, so the balance can change
/// as it's serviced. The exact same pattern as MarkerSignature — a 3D TMP over the paper, live-updated.
///
/// SETUP:
///   - Add a TextMeshPro (3D, NOT UI) object as a child of the marker mesh, centred over the AMOUNT DUE
///     line (where "$50,000" used to be). Use the marker's serif font, ink-dark, large/bold to match.
///   - Put this component on it (or assign `amount`). It mirrors PlayerDebt automatically.
/// </summary>
public class MarkerDebt : MonoBehaviour
{
    [SerializeField] private TMP_Text amount;
    [Tooltip("Numeric format. \"$#,0\" → $1,284,490.")]
    [SerializeField] private string format = "$#,0";

    private void Awake()
    {
        if (amount == null) amount = GetComponent<TMP_Text>();
    }

    private void OnEnable()
    {
        Apply();
        PlayerDebt.Changed += Apply;      // update live when the marker is serviced
    }

    private void OnDisable()
    {
        PlayerDebt.Changed -= Apply;
    }

    private void Apply()
    {
        if (amount != null) amount.text = PlayerDebt.Balance.ToString(format);
    }
}
