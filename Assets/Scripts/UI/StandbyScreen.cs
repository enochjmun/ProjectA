using TMPro;
using UnityEngine;

/// <summary>
/// The idle "standby" state for an interactive console screen: a dim CCTV readout that makes the dark
/// screen read as live surveillance equipment sitting dormant -- a channel label, a ticking timestamp,
/// and a blinking red REC. It shows only while the console is NOT focused; when you pan to the screen and
/// its menu UI panel turns on, the standby hides and the terminal takes over. Reinforces the watcher /
/// "the tape recorded you" frame -- the console is always recording, even at the menu.
///
/// LOCAL, cosmetic. Put this on the screen's standby canvas (kept ALWAYS active -- it toggles the readout's
/// visibility, not its own GameObject). The glass underneath stays a plain dark material; standby text and
/// the menu text both just render on it, so no material swapping or opaque backgrounds are needed.
///
/// SETUP:
///   - readout   = a dim TMP (corner of the screen) -> gets "CHANNEL / DATE  HH:MM:SS".
///   - recLabel  = a small red TMP reading e.g. "* REC" (optional) -> blinks.
///   - focusPanel = the menu UI panel the MenuCameraDirector enables for this view. Standby hides when it's on.
///   - Set channel / dateLabel / start time per screen so they read as different inputs.
/// </summary>
public class StandbyScreen : MonoBehaviour
{
    [Header("Focus")]
    [Tooltip("The menu UI panel the director enables when this console is focused. Standby hides while it's active.")]
    [SerializeField] private GameObject focusPanel;

    [Header("Readout")]
    [Tooltip("Dim TMP showing the channel + ticking timestamp.")]
    [SerializeField] private TMP_Text readout;
    [SerializeField] private string channel = "CH 04";
    [SerializeField] private string dateLabel = "14 NOV";
    [Range(0, 23)] [SerializeField] private int startHour = 3;
    [Range(0, 59)] [SerializeField] private int startMinute = 42;

    [Header("REC blink")]
    [Tooltip("Small red TMP (e.g. \"* REC\"). Blinks while on standby. Optional.")]
    [SerializeField] private TMP_Text recLabel;
    [SerializeField] private float recBlinksPerSecond = 0.8f;

    private double _baseSeconds;
    private float _recTimer;
    private bool _recOn = true;

    private void Awake() => _baseSeconds = startHour * 3600.0 + startMinute * 60.0;

    private void Update()
    {
        bool focused = focusPanel != null && focusPanel.activeInHierarchy;
        bool show = !focused;

        // Ticking timestamp (fictional clock: base time + session elapsed, so no real-year leak).
        if (readout != null)
        {
            readout.enabled = show;
            if (show)
            {
                double total = _baseSeconds + Time.unscaledTime;
                int s = (int)(total % 60);
                int m = (int)((total / 60) % 60);
                int h = (int)((total / 3600) % 24);
                readout.text = $"{channel}\n{dateLabel}  {h:00}:{m:00}:{s:00}";
            }
        }

        // Blink the REC only while on standby.
        if (recLabel != null)
        {
            if (show)
            {
                _recTimer += Time.unscaledDeltaTime;
                float interval = 1f / Mathf.Max(0.1f, recBlinksPerSecond * 2f);
                if (_recTimer >= interval) { _recTimer = 0f; _recOn = !_recOn; }
                recLabel.enabled = _recOn;
            }
            else
            {
                recLabel.enabled = false;
            }
        }
    }
}
