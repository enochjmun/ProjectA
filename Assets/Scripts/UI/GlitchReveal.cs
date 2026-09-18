using System.Collections;
using TMPro;
using UnityEngine;

/// <summary>
/// The shoddy-facade CRACK: the polished front-end screen momentarily glitches and reveals a raw back-end
/// line beneath it — "DUE  PLAYER_04 // BAL -3200 // COLLECT" in red monospace — then snaps back. The house's
/// machine showing through its facade (see House_OS_UI_DesignSystem.md). Local/cosmetic, no netcode.
///
/// Two ways it fires:
///  - AUTO: spontaneous glitches on a randomised interval, for ambient dread. Keep it RARE.
///  - Trigger(line): on demand, for a narrative beat (e.g. the moment a player is marked DUE / collected).
///
/// SETUP: put on the House OS Canvas. Assign `rawOverlay` = a normally-HIDDEN overlay object styled as the
/// raw back-end (red monospace, chromatic offset, per the mockup crack) covering the screen, and `rawLine`
/// = its TMP text. Fill `lines` with raw system strings. Sparse is scary; don't overuse.
/// </summary>
public class GlitchReveal : MonoBehaviour
{
    [Tooltip("The raw back-end overlay, hidden by default. Shown in flickers during a glitch.")]
    [SerializeField] private GameObject rawOverlay;
    [Tooltip("TMP on the overlay whose text is set to the flashed line. Optional.")]
    [SerializeField] private TMP_Text rawLine;
    [Tooltip("Raw back-end lines to flash. One is picked at random per glitch.")]
    [SerializeField] private string[] lines = {
        "DUE  PLAYER_04 // BAL -3200 // COLLECT",
        "HOUSE.EDGE = 1.000 // GUARANTEED",
        "SUBJECT RETAINED // NO EXIT",
        "TABLE 3 // MARK ACTIVE // SIGNED"
    };

    [Header("Auto glitches")]
    [Tooltip("Spontaneous glitches on their own. Turn off if you only want event-triggered cracks.")]
    [SerializeField] private bool auto = true;
    [Tooltip("Seconds between spontaneous glitches (random in this range). Keep it LONG — rare is scary.")]
    [SerializeField] private Vector2 intervalRange = new Vector2(20f, 45f);

    [Header("Feel")]
    [Tooltip("Total length of one crack.")]
    [SerializeField] private float duration = 0.18f;
    [Tooltip("How many on/off flickers within that duration (the crackle).")]
    [SerializeField] private int flickers = 3;

    private Coroutine _autoLoop;

    private void Awake()
    {
        if (rawOverlay != null) rawOverlay.SetActive(false);
    }

    private void OnEnable()
    {
        if (auto) _autoLoop = StartCoroutine(AutoLoop());
    }

    private void OnDisable()
    {
        if (_autoLoop != null) StopCoroutine(_autoLoop);
        if (rawOverlay != null) rawOverlay.SetActive(false);
    }

    /// <summary>Fire a crack now. Pass a specific line, or null to pick a random one — use for narrative beats.</summary>
    public void Trigger(string line = null)
    {
        if (!isActiveAndEnabled || rawOverlay == null) return;
        StartCoroutine(DoGlitch(line));
    }

    private IEnumerator AutoLoop()
    {
        while (true)
        {
            yield return new WaitForSeconds(Random.Range(intervalRange.x, intervalRange.y));
            yield return DoGlitch(null);
        }
    }

    private IEnumerator DoGlitch(string line)
    {
        if (rawLine != null)
            rawLine.text = line ?? (lines.Length > 0 ? lines[Random.Range(0, lines.Length)] : "");

        int steps = Mathf.Max(1, flickers);
        float on = duration / (steps * 2f);
        for (int i = 0; i < steps; i++)
        {
            rawOverlay.SetActive(true);
            yield return new WaitForSeconds(on * Random.Range(0.6f, 1.4f));   // irregular = glitchy
            rawOverlay.SetActive(false);
            yield return new WaitForSeconds(on * Random.Range(0.3f, 1.0f));
        }
        rawOverlay.SetActive(false);
    }
}
