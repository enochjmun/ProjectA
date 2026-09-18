using TMPro;
using UnityEngine;

/// <summary>
/// Writes the player's name onto the marker's blank signature line and keeps it live. Put this on a
/// TextMeshPro (3D) object positioned just above the marker's signature line, using a HANDWRITING font so
/// it reads as an actual signature rather than typed text. The lore payoff: the player signs their own
/// marker while believing they're just clocking in -- the "watched-was-you" reveal, sitting in plain sight.
///
/// SETUP:
///   - Add a TextMeshPro (3D, NOT UI) object as a child of the marker mesh, over the signature line.
///   - Assign it a handwriting TMP Font Asset (Window > TextMeshPro > Font Asset Creator from a free OFL
///     handwriting TTF like Caveat). Ink-dark colour, small size to fit the line.
///   - Put this component on it (or assign `signature`). Done -- it mirrors PlayerName automatically.
/// </summary>
public class MarkerSignature : MonoBehaviour
{
    [SerializeField] private TMP_Text signature;
    [Tooltip("Shown when no name has been entered yet (keeps the line from looking empty/broken).")]
    [SerializeField] private string fallback = "";

    private void Awake()
    {
        if (signature == null) signature = GetComponent<TMP_Text>();
    }

    private void OnEnable()
    {
        Apply(PlayerName.Get());
        PlayerName.Changed += Apply;      // update live as they type
    }

    private void OnDisable()
    {
        PlayerName.Changed -= Apply;
    }

    private void Apply(string name)
    {
        if (signature != null)
            signature.text = string.IsNullOrEmpty(name) ? fallback : name;
    }
}
