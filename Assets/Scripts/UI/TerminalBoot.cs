using System.Collections;
using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.Events;

/// <summary>
/// Types the security-room menu's hero screen to life like a period terminal booting. On enable it clears
/// the text, prints a few fake POST/boot lines character-by-character (the machine waking), then fires
/// OnBootComplete -- wire that to switch the menu buttons on, so the options only appear after the boot.
/// A block cursor blinks throughout and after.
///
/// This is the diegetic hand-off in miniature: a dead-looking screen that comes alive with mechanical
/// certainty (the "sinister polish" over the shoddy facade). Skippable -- any key/click completes it
/// instantly for returning players. LOCAL, cosmetic; it drives one TMP text field, nothing networked.
///
/// SETUP: put on the hero screen's world-space canvas (or a child). Assign `target` = a TMP text set up
/// big and chunky (readable under BuckshotPost). Fill `bootLines` with the lines to print. Wire
/// OnBootComplete -> your buttons' GameObject SetActive(true) (or a fade-in). Keep the buttons disabled in
/// the editor so they appear on cue.
/// </summary>
public class TerminalBoot : MonoBehaviour
{
    [Header("Target")]
    [Tooltip("The TMP text this types into. Defaults to a TMP_Text on this object.")]
    [SerializeField] private TMP_Text target;

    [Header("Boot lines (printed in order)")]
    [TextArea]
    [SerializeField] private string[] bootLines =
    {
        "HOUSE SURVEILLANCE SYS",
        "CH 01..16 .......... OK",
        "AUDIO BUS ........... OK",
        "MARKER LEDGER ....... OK",
        "",
        "OPERATOR ON DUTY.",
    };

    [Header("Timing")]
    [Tooltip("Characters printed per second.")]
    [SerializeField] private float charsPerSecond = 34f;
    [Tooltip("Pause after each completed line (seconds).")]
    [SerializeField] private float lineGap = 0.22f;
    [Tooltip("Pause before the first line, so the screen sits dark a beat first.")]
    [SerializeField] private float startDelay = 0.4f;

    [Header("Cursor")]
    [SerializeField] private string cursorGlyph = "█";   // solid block
    [SerializeField] private float cursorBlinksPerSecond = 2.2f;

    [Header("Skippable")]
    [Tooltip("Any key / mouse click completes the boot instantly. Off = it always plays in full.")]
    [SerializeField] private bool skippable = true;

    [Tooltip("Boot only the FIRST time this is shown; on later re-shows (e.g. panning back to this " +
             "screen) it appears already-booted with no retype. HERO screen = true. Wing 'stations' that " +
             "should power on each visit = false.")]
    [SerializeField] private bool bootOncePerLoad = true;

    [Header("Hooks")]
    [Tooltip("Fires once the boot finishes. Wire to enable the menu buttons / fade them in.")]
    public UnityEvent OnBootComplete;
    [Tooltip("Optional: fires on each printed character (wire a soft key/relay click SFX).")]
    public UnityEvent OnCharPrinted;

    private readonly System.Text.StringBuilder _printed = new System.Text.StringBuilder(256);
    private bool _done;
    private bool _hasBooted;
    private bool _cursorOn = true;
    private Coroutine _routine;

    private void Awake()
    {
        if (target == null) target = GetComponent<TMP_Text>();
    }

    private void OnEnable()
    {
        StartCoroutine(BlinkCursor());

        // Already booted once and set to boot-once: show the finished text instantly, no retype.
        if (bootOncePerLoad && _hasBooted)
        {
            _printed.Clear();
            _printed.Append(string.Join("\n", bootLines));
            Finish();
            return;
        }

        _done = false;
        _printed.Clear();
        if (target != null) target.text = "";
        _routine = StartCoroutine(Boot());
    }

    private void OnDisable()
    {
        StopAllCoroutines();
        _routine = null;
    }

    private void Update()
    {
        if (skippable && !_done && (Input.anyKeyDown))
            Skip();
    }

    private IEnumerator Boot()
    {
        if (target == null) { Finish(); yield break; }

        yield return new WaitForSecondsRealtime(startDelay);

        float perChar = 1f / Mathf.Max(1f, charsPerSecond);
        for (int i = 0; i < bootLines.Length; i++)
        {
            if (i > 0) { _printed.Append('\n'); Render(); }   // newline BETWEEN lines, none trailing
            string line = bootLines[i];
            for (int c = 0; c < line.Length; c++)
            {
                _printed.Append(line[c]);
                Render();
                if (line[c] != ' ') OnCharPrinted?.Invoke();
                yield return new WaitForSecondsRealtime(perChar);
            }
            yield return new WaitForSecondsRealtime(lineGap);
        }
        Finish();
    }

    // Instantly print everything and complete.
    private void Skip()
    {
        StopCoroutine(_routine);
        _printed.Clear();
        _printed.Append(string.Join("\n", bootLines));
        Finish();
    }

    private void Finish()
    {
        _done = true;
        _hasBooted = true;
        Render();
        OnBootComplete?.Invoke();
    }

    private IEnumerator BlinkCursor()
    {
        var wait = new WaitForSecondsRealtime(1f / Mathf.Max(0.1f, cursorBlinksPerSecond * 2f));
        while (true)
        {
            _cursorOn = !_cursorOn;
            Render();
            yield return wait;
        }
    }

    // Draw the printed text plus the blinking cursor on the end.
    private void Render()
    {
        if (target == null) return;
        target.text = _cursorOn ? _printed.ToString() + cursorGlyph : _printed.ToString();
    }
}
