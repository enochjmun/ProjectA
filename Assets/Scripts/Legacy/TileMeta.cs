using System.Collections.Generic;
using UnityEngine;

/// <summary>Coarse role of a tile -- drives generation and where special rooms go.</summary>
public enum TileArchetype
{
    Room, Corridor, Junction, DeadEnd,
    StartRoom,       // holds the DungeonSpawnPoints (drop-in)
    ExitRoom,        // holds the DungeonExit
    ObjectiveRoom    // holds the objective trigger
}

/// <summary>
/// Per-tile metadata the generator and monster AIs read instead of raw geometry (see
/// Dungeon_Tile_Spec §5). Archetype + weight scores let the generator bias tile choice per
/// monster; the affordance nodes are what a monster AI queries for cover/hiding/ambush/patrol/
/// sightline points. Connectors and affordances are collected from children on demand, so you
/// just place them in the prefab and forget about wiring lists.
/// </summary>
public class TileMeta : MonoBehaviour
{
    [Header("Classification")]
    public TileArchetype archetype = TileArchetype.Room;

    [Header("Generation")]
    [Tooltip("Relative pick chance (higher = appears more often). Balance corridors vs " +
             "junctions/rooms here to tune linear-vs-branchy.")]
    [Min(0f)] public float weight = 1f;

    [Header("Monster-bias weights (0..1) -- how strongly a monster's map bias favours this tile")]
    [Range(0f, 1f)] public float openness = 0.5f;
    [Range(0f, 1f)] public float coverDensity = 0.5f;

    [Header("Footprint (local space) -- the generator rejects overlapping placements")]
    public Vector3 boundsSize = new Vector3(4f, 4f, 4f);
    public Vector3 boundsCenter = Vector3.zero;

    private TileConnector[] _connectors;
    private AffordanceNode[] _affordances;

    public IReadOnlyList<TileConnector> Connectors =>
        _connectors ??= GetComponentsInChildren<TileConnector>(true);

    public IReadOnlyList<AffordanceNode> Affordances =>
        _affordances ??= GetComponentsInChildren<AffordanceNode>(true);

    /// <summary>World-space affordance points of a given type -- the monster AI's query entry point.</summary>
    public IEnumerable<Transform> GetAffordances(AffordanceType type)
    {
        foreach (var a in Affordances)
            if (a != null && a.type == type)
                yield return a.transform;
    }

    private void OnDrawGizmosSelected()
    {
        Gizmos.matrix = transform.localToWorldMatrix;
        Gizmos.color = new Color(1f, 1f, 0f, 0.25f);
        Gizmos.DrawWireCube(boundsCenter, boundsSize);
    }
}
