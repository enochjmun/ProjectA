using TMPro;
using UnityEngine;

/// <summary>
/// One row of the House-OS leaderboard — LEDGER style: rank · name · value, no bar. The value COUNTS toward
/// the new amount when it changes (the house tallying), and an optional delta pop (▲ +300 / ▼ -100) flashes
/// and fades on a change. Put this on the row prefab; LeaderboardView calls Set() each refresh.
/// </summary>
public class LeaderboardRow : MonoBehaviour
{
    [SerializeField] private TMP_Text rankText;
    [SerializeField] private TMP_Text nameText;
    [SerializeField] private TMP_Text valueText;
    [Tooltip("Optional: shows the change (▲ +300 / ▼ -100) briefly when points move. Leave null to skip.")]
    [SerializeField] private TMP_Text deltaText;
    [Tooltip("Optional: chip glyphs (◉) — count = stack depth vs the leader, colour = denomination tier (casino read).")]
    [SerializeField] private TMP_Text chipsText;
    [Tooltip("Shown when the player is DUE (caught / benched). Optional.")]
    [SerializeField] private GameObject dueTag;
    [Tooltip("Enabled only on the #1 row — a solid amber row fill for the inverted 'house marked this one' look. Optional.")]
    [SerializeField] private GameObject leadHighlight;
    [Tooltip("House mark / crown glyph shown only on the #1 row (ties to the in-world leader crown). Optional.")]
    [SerializeField] private GameObject leaderMark;
    [Tooltip("Texts recoloured for the leader (rank/name/value) — dark on the amber fill for the inverted look. Leave empty to skip recolouring.")]
    [SerializeField] private TMP_Text[] leaderTint;
    [Tooltip("Body text (amber terminal). Set on the row prefab: F0A93C.")]
    [SerializeField] private Color normalColor = new Color(0.941f, 0.663f, 0.235f);   // amber F0A93C
    [Tooltip("Leader text — glows (ASCII terminal uses » + glow, not an inverted bar). Set: FFC66B.")]
    [SerializeField] private Color leaderColor = new Color(1f, 0.776f, 0.420f);       // glow FFC66B

    [Header("Chip tiers (colour by points)")]
    [Tooltip("Point thresholds for chip colour: below x = white, x..y = red, y..z = green, above z = gold.")]
    [SerializeField] private Vector3 chipThresholds = new Vector3(1000f, 2500f, 4000f);
    [SerializeField] private Color chipWhite = new Color(0.914f, 0.890f, 0.839f);
    [SerializeField] private Color chipRed   = new Color(0.776f, 0.255f, 0.184f);   // C6412F
    [SerializeField] private Color chipGreen = new Color(0.243f, 0.490f, 0.349f);   // 3E7D59
    [SerializeField] private Color chipGold  = new Color(0.910f, 0.722f, 0.294f);   // E8B84B

    [Header("Feel")]
    [Tooltip("How fast the ledger value counts toward the new amount.")]
    [SerializeField] private float countSpeed = 6f;
    [Tooltip("Seconds the delta pop holds before it fades out.")]
    [SerializeField] private float deltaHold = 1.6f;
    [Tooltip("How fast the row slides to its new rank slot when players overtake. Higher = snappier.")]
    [SerializeField] private float slideSpeed = 9f;
    [SerializeField] private Color gainColor = new Color(0.24f, 0.49f, 0.35f);   // felt green #3E7D59
    [SerializeField] private Color lossColor = new Color(0.82f, 0.27f, 0.20f);   // red #D24632

    private float _shownPoints, _targetPoints;
    private bool _due, _initialised;
    private float _deltaTimer;
    private RectTransform _rt;
    private float _targetY;

    private void Awake() => _rt = (RectTransform)transform;

    public void Set(int rank, string playerName, int points, bool due, bool isLead, int leaderPoints)
    {
        if (rankText != null) rankText.text = rank.ToString("00");
        if (nameText != null) nameText.text = playerName;

        // chips: stack depth relative to the leader, coloured by denomination tier (the casino read)
        if (chipsText != null)
        {
            int n = due ? 0 : Mathf.Clamp(Mathf.RoundToInt(points / (float)Mathf.Max(1, leaderPoints) * 6f), 1, 6);
            chipsText.text = new string('◉', n);
            chipsText.color = ChipColor(points);
        }

        // Delta pop when the amount changes — but not on the first populate (that would flash everyone's
        // starting total in as a "gain").
        if (_initialised && deltaText != null)
        {
            int d = points - Mathf.RoundToInt(_targetPoints);
            if (d != 0) ShowDelta(d);
        }

        _targetPoints = points;
        _due = due;
        _initialised = true;
        if (dueTag != null) dueTag.SetActive(due);

        // Leader treatment — the house marks the leader: inverted amber row + house mark + dark text.
        if (leadHighlight != null) leadHighlight.SetActive(isLead);
        if (leaderMark != null) leaderMark.SetActive(isLead);
        if (leaderTint != null)
            foreach (var t in leaderTint)
                if (t != null) t.color = isLead ? leaderColor : normalColor;
    }

    /// <summary>The Y this row should slide to (its rank slot). The view computes it from row height/spacing.</summary>
    public void SetTargetY(float y) => _targetY = y;

    /// <summary>Jump to the target slot instantly — used when a row is (re)assigned to a player, so it
    /// doesn't slide in from a stale pooled position; and on the first populate.</summary>
    public void SnapPosition()
    {
        if (_rt == null) _rt = (RectTransform)transform;
        var p = _rt.anchoredPosition; p.y = _targetY; p.x = 0f; _rt.anchoredPosition = p;
    }

    /// <summary>Show the value instantly (first populate) — no count, no delta.</summary>
    public void Snap()
    {
        _shownPoints = _targetPoints;
        ApplyValue();
        _deltaTimer = 0f;
        SetDeltaAlpha(0f);
    }

    private void Update()
    {
        float t = 1f - Mathf.Exp(-countSpeed * Time.deltaTime);
        _shownPoints = Mathf.Lerp(_shownPoints, _targetPoints, t);
        ApplyValue();

        // slide toward the rank slot — this is the overtake animation
        if (_rt != null)
        {
            float s = 1f - Mathf.Exp(-slideSpeed * Time.deltaTime);
            var p = _rt.anchoredPosition;
            p.y = Mathf.Lerp(p.y, _targetY, s);
            _rt.anchoredPosition = p;
        }

        if (deltaText != null && _deltaTimer > 0f)
        {
            _deltaTimer -= Time.deltaTime;
            SetDeltaAlpha(Mathf.Clamp01(_deltaTimer / 0.6f));   // hold, then fade over the last 0.6s
        }
    }

    private Color ChipColor(int v)
    {
        if (v >= chipThresholds.z) return chipGold;
        if (v >= chipThresholds.y) return chipGreen;
        if (v >= chipThresholds.x) return chipRed;
        return chipWhite;
    }

    private void ApplyValue()
    {
        if (valueText != null)
            valueText.text = _due ? "DUE" : "$" + Mathf.RoundToInt(_shownPoints).ToString("N0");
    }

    private void ShowDelta(int d)
    {
        deltaText.text = (d >= 0 ? "▲ +" : "▼ -") + Mathf.Abs(d).ToString("N0");
        deltaText.color = d >= 0 ? gainColor : lossColor;
        _deltaTimer = deltaHold;
        SetDeltaAlpha(1f);
    }

    private void SetDeltaAlpha(float a)
    {
        if (deltaText == null) return;
        Color c = deltaText.color;
        c.a = a;
        deltaText.color = c;
    }
}
