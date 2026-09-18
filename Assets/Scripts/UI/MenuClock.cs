using TMPro;
using UnityEngine;

/// <summary>
/// Drives the House-OS terminal header clock on a menu facet (the "◈ SYS 7.14 … ● ONLINE  08:17" line).
/// Local/cosmetic. Frozen time by default — a casino has no clocks, and a fixed eerie time reads better than
/// a real one; flip useRealTime on if you want it live.
///
/// SETUP: put on the header's clock TMP (or assign clockText). The "◈ SYS 7.14" and "● ONLINE" are static
/// text you set once in the editor; only the clock ticks (if live).
/// </summary>
public class MenuClock : MonoBehaviour
{
    [SerializeField] private TMP_Text clockText;
    [SerializeField] private bool useRealTime = true;
    [SerializeField] private string frozenTime = "08:17";
    [Tooltip(".NET time format. \"HH:mm\" = 14:07; \"HH:mm:ss\" to show seconds.")]
    [SerializeField] private string timeFormat = "HH:mm";
    [Tooltip("Text placed before the time (rich text OK). e.g. '<color=#3E7D59>● ONLINE</color>   ' to put the status + clock in one TMP.")]
    [SerializeField] private string prefix = "";

    private float _tick;

    private void Awake() { if (clockText == null) clockText = GetComponent<TMP_Text>(); }
    private void OnEnable() { Apply(); }

    private void Update()
    {
        if (!useRealTime) return;
        _tick -= Time.unscaledDeltaTime;
        if (_tick <= 0f) { _tick = 1f; Apply(); }
    }

    private void Apply()
    {
        if (clockText == null) return;
        string t = useRealTime ? System.DateTime.Now.ToString(timeFormat) : frozenTime;
        clockText.text = prefix + t;
    }
}
