using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Scene singleton that knows the dungeon's landing points. For now it just returns a
/// RANDOM spawn. This is the single seam where the smarter placement algorithm
/// (far-apart for multi-loser, §5.1) and a procedural dungeon generator will plug in
/// later -- the fall logic in MatchController only ever asks for "a spawn", so none of
/// that changes how dropping works.
/// </summary>
public class DungeonManager : MonoBehaviour
{
    public static DungeonManager Instance { get; private set; }

    [Tooltip("Optional explicit list. If empty, every DungeonSpawnPoint in the scene is used.")]
    [SerializeField] private List<Transform> spawnPoints = new List<Transform>();

    private void Awake()
    {
        Instance = this;
    }

    private void OnDestroy()
    {
        if (Instance == this)
            Instance = null;
    }

    /// <summary>Returns a random dungeon landing transform, or null if none exist.</summary>
    public Transform GetRandomSpawn()
    {
        var points = ResolvePoints();
        if (points.Count == 0)
            return null;
        return points[Random.Range(0, points.Count)];
    }

    /// <summary>Returns up to `count` DISTINCT spawns spread far apart, so dropped players land
    /// separated. Greedy farthest-point: seed one, then repeatedly add the point farthest from those
    /// already chosen. If there are fewer points than `count`, returns all of them.</summary>
    public List<Transform> GetDistinctSpawns(int count)
    {
        var remaining = new List<Transform>(ResolvePoints());
        var chosen = new List<Transform>();
        if (remaining.Count == 0 || count <= 0) return chosen;

        int seed = Random.Range(0, remaining.Count);
        chosen.Add(remaining[seed]);
        remaining.RemoveAt(seed);

        while (chosen.Count < count && remaining.Count > 0)
        {
            int best = 0; float bestMin = -1f;
            for (int i = 0; i < remaining.Count; i++)
            {
                float md = float.MaxValue;
                for (int j = 0; j < chosen.Count; j++)
                    md = Mathf.Min(md, (remaining[i].position - chosen[j].position).sqrMagnitude);
                if (md > bestMin) { bestMin = md; best = i; }
            }
            chosen.Add(remaining[best]);
            remaining.RemoveAt(best);
        }
        return chosen;
    }

    /// <summary>The spawn farthest from every position in `avoid` (e.g. the players) -- used to drop
    /// the monster in with distance between it and its prey. `exclude` spawns are removed from the
    /// running (e.g. the exact points players landed on, so the monster never spawns ON a player) unless
    /// that would leave nothing. Falls back to random if `avoid` is empty.</summary>
    public Transform GetSpawnFarFrom(IList<Vector3> avoid, ICollection<Transform> exclude = null)
    {
        var points = ResolvePoints();
        if (points.Count == 0) return null;

        // Drop excluded spawns -- unless excluding leaves nothing, in which case keep them all rather
        // than returning null.
        List<Transform> candidates = points;
        if (exclude != null && exclude.Count > 0)
        {
            var filtered = new List<Transform>();
            foreach (var p in points)
                if (!exclude.Contains(p)) filtered.Add(p);
            if (filtered.Count > 0) candidates = filtered;
        }

        if (avoid == null || avoid.Count == 0)
            return candidates[Random.Range(0, candidates.Count)];

        Transform best = null; float bestMin = -1f;
        foreach (var p in candidates)
        {
            float md = float.MaxValue;
            for (int i = 0; i < avoid.Count; i++)
                md = Mathf.Min(md, (p.position - avoid[i]).sqrMagnitude);
            if (md > bestMin) { bestMin = md; best = p; }
        }
        return best;
    }

    private List<Transform> ResolvePoints()
    {
        if (spawnPoints != null && spawnPoints.Count > 0)
            return spawnPoints;

        var found = FindObjectsByType<DungeonSpawnPoint>(FindObjectsSortMode.None);
        var list = new List<Transform>(found.Length);
        foreach (var p in found)
            list.Add(p.transform);
        return list;
    }
}
