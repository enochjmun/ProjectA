using System.Collections.Generic;
using System.Linq;
using Dissonance.Integrations.Unity_NFGO;
using UnityEngine;

/// <summary>
/// Fills the House-OS leaderboard from live match standings — the front-end "standings" channel content.
/// Local / no netcode: reads the already-replicated PlayerState.Points (score) + BenchRoundsRemaining
/// (caught → DUE) and the networked name off NfgoPlayer.PlayerId, sorts by points, and drives the rows.
///
/// ROWS ARE KEYED BY PLAYER, not by rank slot. Each player owns one row; when their rank changes the row
/// SLIDES to the new slot (the overtake animation), and because the row persists per player its count-up
/// and delta compare the player's OWN previous total (correct across reorders). The view computes each
/// row's target Y from rank; rows are positioned by code, so the container must NOT have a Layout Group.
///
/// DEBUG: flip `debugMode` on and drive a FAKE roster via the Debug* methods (see LeaderboardDebug) to test
/// sorting / leader / DUE / count / delta / the overtake slide without a multiplayer session. OFF for real play.
///
/// SETUP: put on the leaderboard Canvas. Assign a row container (a plain RectTransform, NO Vertical Layout
/// Group / Content Size Fitter) + a LeaderboardRow prefab. Set rowHeight to the prefab's height.
/// </summary>
public class LeaderboardView : MonoBehaviour
{
    [SerializeField] private RectTransform rowContainer;
    [SerializeField] private LeaderboardRow rowPrefab;
    [Tooltip("Max rows shown. Big-tier lobby screen wants ~4.")]
    [SerializeField] private int maxRows = 4;
    [Tooltip("Seconds between refreshes; rows ease between values/positions so it still looks smooth.")]
    [SerializeField] private float refreshInterval = 0.5f;

    [Header("Layout (rows are positioned by code, not a Layout Group)")]
    [Tooltip("Height of one row, in Canvas units. Match your row prefab's height.")]
    [SerializeField] private float rowHeight = 64f;
    [Tooltip("Gap between rows, in Canvas units.")]
    [SerializeField] private float rowSpacing = 4f;

    [Header("Debug — test without networking")]
    [Tooltip("ON = read a FAKE roster driven by the Debug* methods (LeaderboardDebug) instead of live PlayerState. OFF for real play.")]
    [SerializeField] private bool debugMode = false;

    private struct Entry
    {
        public string key, name; public int points; public bool due;
        public Entry(string k, string n, int p, bool d) { key = k; name = n; points = p; due = d; }
    }

    private readonly List<Entry> _debug = new List<Entry>();
    private readonly Dictionary<string, LeaderboardRow> _active = new Dictionary<string, LeaderboardRow>();
    private readonly List<LeaderboardRow> _pool = new List<LeaderboardRow>();
    private readonly List<string> _stale = new List<string>();
    private float _tick;

    private void OnEnable() { _tick = 0f; Refresh(true); }

    private void Update()
    {
        _tick -= Time.deltaTime;
        if (_tick <= 0f) { _tick = refreshInterval; Refresh(false); }
    }

    /// <summary>Rebuild the standings. `snap` shows values/positions instantly (first populate) rather than easing.</summary>
    public void Refresh(bool snap)
    {
        List<Entry> entries = debugMode
            ? _debug.OrderByDescending(e => e.points).ToList()
            : Object.FindObjectsByType<PlayerState>(FindObjectsSortMode.None)
                    .OrderByDescending(p => p.Points.Value)
                    .Select(p => new Entry(p.OwnerClientId.ToString(), ResolveName(p),
                                           p.Points.Value, p.BenchRoundsRemaining.Value > 0))
                    .ToList();

        int shown = Mathf.Min(maxRows, entries.Count);
        int leaderPoints = entries.Count > 0 ? Mathf.Max(1, entries[0].points) : 1;   // top score drives chip stacks

        // Any row whose player is no longer in the top `shown` gets recycled.
        _stale.Clear();
        _stale.AddRange(_active.Keys);

        for (int i = 0; i < shown; i++)
        {
            Entry e = entries[i];
            bool isNew = !_active.ContainsKey(e.key);
            LeaderboardRow row = GetRow(e.key);

            row.SetTargetY(-(i * (rowHeight + rowSpacing)));      // slot Y from rank (0 at top, downward)
            row.Set(i + 1, e.name, e.points, e.due, i == 0, leaderPoints);
            if (isNew || snap) row.SnapPosition();                // don't slide in from a stale pool spot
            if (snap) row.Snap();

            _stale.Remove(e.key);
        }

        foreach (string key in _stale) Recycle(key);
    }

    // Get the row bound to this player key, reusing a pooled one or spawning a new one.
    private LeaderboardRow GetRow(string key)
    {
        if (_active.TryGetValue(key, out var existing)) return existing;

        LeaderboardRow row;
        if (_pool.Count > 0)
        {
            row = _pool[_pool.Count - 1];
            _pool.RemoveAt(_pool.Count - 1);
            row.gameObject.SetActive(true);
        }
        else
        {
            row = Instantiate(rowPrefab, rowContainer);
            var rt = (RectTransform)row.transform;               // stretch full width, fixed height, top-anchored
            rt.anchorMin = new Vector2(0f, 1f);
            rt.anchorMax = new Vector2(1f, 1f);
            rt.pivot = new Vector2(0.5f, 1f);
            rt.sizeDelta = new Vector2(0f, rowHeight);
        }
        _active[key] = row;
        return row;
    }

    private void Recycle(string key)
    {
        if (!_active.TryGetValue(key, out var row)) return;
        _active.Remove(key);
        row.gameObject.SetActive(false);
        _pool.Add(row);
    }

    private static string ResolveName(PlayerState p)
    {
        var nfgo = p.GetComponent<NfgoPlayer>();
        string id = nfgo != null ? nfgo.PlayerId : null;
        if (!string.IsNullOrEmpty(id)) return id.ToUpperInvariant();
        return "P" + p.OwnerClientId;
    }

    // ---- Debug API (needs debugMode ON; driven by LeaderboardDebug). Index is INSERTION order, not rank. ----
    public int DebugCount => _debug.Count;

    public void DebugAddPlayer(string name, int points = 0)
    {
        // key must be stable per player across refreshes, so the row follows them as they re-sort
        string key = "dbg:" + name + ":" + _debug.Count;
        _debug.Add(new Entry(key, name, points, false));
        Refresh(false);
    }

    public void DebugAddPoints(int index, int delta)
    {
        if (index < 0 || index >= _debug.Count) return;
        Entry e = _debug[index];
        e.points = Mathf.Max(0, e.points + delta);
        _debug[index] = e;
        Refresh(false);
    }

    public void DebugToggleDue(int index)
    {
        if (index < 0 || index >= _debug.Count) return;
        Entry e = _debug[index];
        e.due = !e.due;
        _debug[index] = e;
        Refresh(false);
    }

    public void DebugClear()
    {
        _debug.Clear();
        foreach (var kv in _active) { kv.Value.gameObject.SetActive(false); _pool.Add(kv.Value); }
        _active.Clear();
    }
}
