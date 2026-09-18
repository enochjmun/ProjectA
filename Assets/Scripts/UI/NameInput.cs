using TMPro;
using UnityEngine;

/// <summary>
/// Relays a TMP_InputField into PlayerName (a static store the inspector can't call directly). Put this on
/// the name-entry field in the menu; it loads the current saved name on start and pushes every edit into
/// PlayerName, so the marker signature updates live as the player types.
///
/// SETUP: put on the GameObject with the TMP_InputField (or assign `field`). Give the field a placeholder
/// like "MEMBER NAME" / "OPERATOR ID". That's it.
/// </summary>
public class NameInput : MonoBehaviour
{
    [SerializeField] private TMP_InputField field;
    [Tooltip("Max characters accepted (keeps a signature that fits the marker line).")]
    [SerializeField] private int maxLength = 18;

    private void Awake()
    {
        if (field == null) field = GetComponent<TMP_InputField>();
    }

    private void Start()
    {
        if (field == null) return;
        if (maxLength > 0) field.characterLimit = maxLength;
        field.text = PlayerName.Get();                 // show whatever was entered before
        field.onValueChanged.AddListener(PlayerName.Set);
    }

    private void OnDestroy()
    {
        if (field != null) field.onValueChanged.RemoveListener(PlayerName.Set);
    }

    /// <summary>Also callable directly (e.g. from an onEndEdit event) if you prefer manual wiring.</summary>
    public void SetName(string s) => PlayerName.Set(s);
}
