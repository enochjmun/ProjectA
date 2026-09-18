using UnityEngine;

/// <summary>The kind of thing a monster AI can do at an affordance point.</summary>
public enum AffordanceType
{
    CoverPoint,     // break line of sight (chasers, ranged)
    HidingSpot,     // crouch/hide into (stealth / sound hunters)
    AmbushPerch,    // where a lurker waits
    PatrolAnchor,   // patrol-route waypoint (territorial monsters)
    SightlineNode   // long-view vantage (relentless / ranged)
}

/// <summary>
/// A tagged point in a tile that a monster AI queries -- so the MAP just advertises affordances
/// and each monster uses the ones its playstyle needs, without the map knowing about specific
/// monsters (see Dungeon_Tile_Spec §5). Place these empties around a tile; TileMeta collects them.
/// </summary>
public class AffordanceNode : MonoBehaviour
{
    public AffordanceType type = AffordanceType.CoverPoint;

    [Tooltip("If true, transform.forward is a meaningful facing (e.g. an ambusher's look " +
             "direction or a cover normal); otherwise only the position matters.")]
    public bool useForward = false;

    private void OnDrawGizmos()
    {
        Gizmos.color = ColorFor(type);
        Gizmos.DrawWireSphere(transform.position + Vector3.up * 0.5f, 0.25f);
        if (useForward)
            Gizmos.DrawLine(transform.position, transform.position + transform.forward * 0.8f);
    }

    private static Color ColorFor(AffordanceType t)
    {
        switch (t)
        {
            case AffordanceType.CoverPoint:    return new Color(0.3f, 0.7f, 1f);
            case AffordanceType.HidingSpot:    return new Color(0.4f, 1f, 0.4f);
            case AffordanceType.AmbushPerch:   return new Color(1f, 0.4f, 0.3f);
            case AffordanceType.PatrolAnchor:  return new Color(1f, 0.9f, 0.3f);
            case AffordanceType.SightlineNode: return new Color(0.8f, 0.5f, 1f);
            default:                           return Color.white;
        }
    }
}
