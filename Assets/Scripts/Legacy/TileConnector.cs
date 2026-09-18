using UnityEngine;

/// <summary>
/// Marks a doorway socket on a dungeon tile. The generator mates two tiles by aligning one
/// tile's connector onto another's so they FACE opposite directions (see Dungeon_Tile_Spec §2).
///
/// Convention: place this empty at the doorway CENTRE, on the floor, with local +Z (forward)
/// pointing OUTWARD -- into the space where the neighbour tile will attach. Every doorway in
/// every tile uses the same opening size so any connector can mate with any other.
/// </summary>
public class TileConnector : MonoBehaviour
{
    [Tooltip("Universal by default. Give matching ids only if you later want typed connections " +
             "(e.g. 'large' doorways only mate with 'large').")]
    public string connectorType = "default";

    [Header("Opening size (used to seal this doorway if it ends up unused)")]
    [Tooltip("Width/height of the hole this connector sits in, and the wall thickness to plug it " +
             "with. The greybox tile sets these; a modelled tile can too. Lets the generator " +
             "procedurally cap a leftover doorway without a wallCap prefab.")]
    public float openingWidth = 2f;
    public float openingHeight = 3f;
    public float thickness = 0.3f;
    [Tooltip("Material for the procedural cap panel (usually the tile's greybox material).")]
    public Material capMaterial;

    // Set by the generator once this doorway has a neighbour; leftover 'false' ones get capped.
    [HideInInspector] public bool used;

    // Draw the doorway frame + an outward arrow so the facing (+Z outward) is obvious in-editor.
    private void OnDrawGizmos()
    {
        Gizmos.color = used ? new Color(0.4f, 0.4f, 0.4f) : new Color(0.3f, 1f, 0.5f);

        Matrix4x4 old = Gizmos.matrix;
        Gizmos.matrix = Matrix4x4.TRS(
            transform.position + Vector3.up * (openingHeight * 0.5f),
            transform.rotation, Vector3.one);
        Gizmos.DrawWireCube(Vector3.zero, new Vector3(openingWidth, openingHeight, 0.05f));
        Gizmos.matrix = old;

        Gizmos.DrawLine(transform.position, transform.position + transform.forward * 1f);
    }
}
