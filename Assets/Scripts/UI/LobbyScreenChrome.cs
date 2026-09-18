using TMPro;
using UnityEngine;

/// <summary>
/// Drives the persistent House-OS chrome around the lobby standings: the status-bar clock and the
/// round-progression footer (pips + "03 / 05"). The header and wordmark are static text you set once in
/// the editor; only the live bits live here. Local/cosmetic, no netcode.
///
/// Progression is API-driven: call SetRound(current, total) from your match/phase logic when a round
/// closes. The serialized previewCurrent/previewTotal let you see it laid out in the editor and in the
/// LeaderboardDebug flow before match logic is wired.
///
/// SETUP: put on the leaderboard Canvas (or a Chrome child). Assign clockText, pipsText, progressText.
/// </summary>
public class LobbyScreenChrome : MonoBehaviour
{
    [Header("Status bar")]
    [Tooltip("Right-side clock (HH:mm). The house's system time — ambient chrome, NOT the round timer.")]
    [SerializeField] private TMP_Text clockText;
    [Tooltip("Use real system time. Off = hold the frozen house time below (a casino has no clocks — an eerie fixed time can read better).")]
    [SerializeField] private bool useRealTime = false;
    [Tooltip("Frozen house time shown when Use Real Time is off.")]
    [SerializeField] private string frozenTime = "08:17";

    [Header("Round progression (footer)")]
    [Tooltip("Filled/empty pips, e.g. ●●●○○.")]
    [SerializeField] private TMP_Text pipsText;
    [Tooltip("Numeric progress, e.g. 03 / 05.")]
    [SerializeField] private TMP_Text progressText;
    [SerializeField] private char pipFull = '●';    // ●
    [SerializeField] private char pipEmpty = '○';   // ○

    [Header("Editor preview (also used until match logic calls SetRound)")]
    [SerializeField] private int previewCurrent = 3;
    [SerializeField] private int previewTotal = 5;

    private int _current, _total;
    private float _clockTick;

    private void OnEnable()
    {
        SetRound(previewCurrent, previewTotal);
        UpdateClock();
    }

    private void Update()
    {
        if (!useRealTime) return;
        _clockTick -= Time.unscaledDeltaTime;
        if (_clockTick <= 0f) { _clockTick = 1f; UpdateClock(); }
    }

    /// <summary>Call when a round closes: current = rounds completed, total = rounds in the game.</summary>
    public void SetRound(int current, int total)
    {
        _total = Mathf.Max(1, total);
        _current = Mathf.Clamp(current, 0, _total);

        if (pipsText != null)
        {
            var sb = new System.Text.StringBuilder(_total);
            for (int i = 0; i < _total; i++) sb.Append(i < _current ? pipFull : pipEmpty);
            pipsText.text = sb.ToString();
        }
        if (progressText != null)
            progressText.text = _current.ToString("00") + " / " + _total.ToString("00");
    }

    private void UpdateClock()
    {
        if (clockText == null) return;
        clockText.text = useRealTime ? System.DateTime.Now.ToString("HH:mm") : frozenTime;
    }

#if UNITY_EDITOR
    private void OnValidate()
    {
        if (Application.isPlaying) return;
        SetRound(previewCurrent, previewTotal);
        UpdateClock();
    }
#endif
}
