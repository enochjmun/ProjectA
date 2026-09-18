using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Goes on a MODULAR ROOM prefab to tell the generator two things it cannot infer from the mesh:
/// how many cells the room occupies, and where its wall openings are.
///
/// WHY A COMPONENT AND NOT GENERATOR FIELDS: rooms are going to have unique dimensions (2x2 now,
/// others later). If the generator hard-coded "the room is 2x2 with openings at the side midpoints",
/// every new room shape would mean editing the generator. Here the PREFAB describes itself, so adding
/// a 3x4 room with openings in odd places is purely an art task -- drop the component on, position the
/// anchors, assign it. The generator stays unchanged.
///
/// Corridor pieces do NOT need this: their footprint is always one cell and their openings are always
/// the four cell edges, both of which the generator already knows.
/// </summary>
public class DungeonKitPiece : MonoBehaviour
{
    [Tooltip("Footprint in grid cells. A 2x2 room covers 4 cells. Must match the modelled size or the " +
             "piece will not line up with the grid.")]
    public Vector2Int sizeInCells = new Vector2Int(2, 2);

    [Tooltip("ONE ANCHOR PER PERIMETER CELL, not one per side. A 2x2 room has two cells on each side, " +
             "so it needs 8 anchors -- the generator picks ONE cell per side as the door and drops the " +
             "NavMeshLink at THAT cell's edge midpoint. An opening at the side's midpoint (on the " +
             "boundary between two cells) leaves the link inside solid wall, the room never connects, " +
             "and the connectivity guard rerolls until it gives up.\n\n" +
             "Put each anchor at the TRUE 3D CENTRE of its hole -- centred across the opening AND " +
             "halfway up it, not at floor level. The generator plugs every opening no corridor uses.")]
    public List<Transform> openings = new List<Transform>();

    [Tooltip("The hole's actual size: X = width across the opening, Y = height. MATCH THE MODELLED HOLE. " +
             "Too small leaves visible gaps; too large embeds the plug in the surrounding wall, where it " +
             "ends up coplanar with the wall faces and z-fights.")]
    public Vector2 openingSize = new Vector2(2f, 2.5f);

    [Tooltip("Optional modelled plug. Left empty, the generator fills unused openings with a greybox " +
             "box so you can see the layout immediately without waiting on the art.")]
    public GameObject plugPrefab;

    /// <summary>
    /// Which side of the room an opening sits on, as a grid direction. Derived from the anchor's local
    /// position rather than authored by hand: whichever axis the anchor is furthest from centre along
    /// wins, and its sign gives the direction. This means you can move an opening in Blender and the
    /// generator follows -- there is no second place to keep in sync.
    ///
    /// Grid X maps to world X and grid Y maps to world Z, matching DungeonGenerator.CellLocal.
    /// </summary>
    /// <summary>
    /// Which cell of the room's footprint an opening belongs to, as an offset from the room's
    /// bottom-left cell (0,0). The generator adds the room's grid origin to get an absolute cell.
    ///
    /// This is what lets the ART decide where doors go. The generator reads these offsets when routing
    /// corridors, instead of assuming a fixed convention the prefab then has to match.
    ///
    /// Steps half a cell INWARD first, because the anchor sits on the outer wall face -- exactly on a
    /// cell boundary. Resolving that directly rounds a value sitting precisely on .5 and lands on an
    /// arbitrary neighbour, sometimes the cell outside the room entirely.
    /// </summary>
    public Vector2Int CellOffsetOf(Transform opening, float cellSize)
    {
        Vector2Int side = SideOf(opening);
        Vector3 p = transform.InverseTransformPoint(opening.position)
                    - new Vector3(side.x, 0f, side.y) * (cellSize * 0.5f);

        return new Vector2Int(
            Mathf.RoundToInt((p.x + sizeInCells.x * cellSize * 0.5f) / cellSize - 0.5f),
            Mathf.RoundToInt((p.z + sizeInCells.y * cellSize * 0.5f) / cellSize - 0.5f));
    }

    public Vector2Int SideOf(Transform opening)
    {
        // InverseTransformPoint, NOT localPosition. localPosition is relative to the anchor's IMMEDIATE
        // parent, so the moment you nest an anchor under a sub-object (a "Walls" group, say) it stops
        // being measured from the room centre and the side comes out wrong. This measures from the piece
        // root wherever the anchor sits in the hierarchy.
        Vector3 p = transform.InverseTransformPoint(opening.position);
        return Mathf.Abs(p.x) >= Mathf.Abs(p.z)
            ? new Vector2Int(p.x >= 0f ? 1 : -1, 0)
            : new Vector2Int(0, p.z >= 0f ? 1 : -1);
    }
}
