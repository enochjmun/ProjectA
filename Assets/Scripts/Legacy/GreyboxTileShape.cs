using Unity.AI.Navigation;
using UnityEngine;

/// <summary>
/// Builds a GREYBOX dungeon tile from primitives at runtime -- floor, walls with doorway gaps,
/// and TileConnectors -- so you can test the DungeonGenerator + the chase without modeling
/// anything in Blender. Put this on a near-empty prefab next to a TileMeta, set the numbers, and
/// it self-builds on Awake (which runs during Instantiate, so the generator sees the connectors).
/// It also auto-fills the TileMeta bounds. Swap these for modeled tiles later -- the generator
/// doesn't care which it gets.
/// </summary>
[RequireComponent(typeof(TileMeta))]
public class GreyboxTileShape : MonoBehaviour
{
    [Header("Footprint")]
    public Vector2Int sizeCells = new Vector2Int(1, 1);
    public float cellSize = 4f;
    public float ceilingHeight = 4f;
    public float wallThickness = 0.3f;
    public bool buildCeiling = true;

    [Header("Doorways (which sides have an opening)")]
    public bool doorNorth = true;   // +Z
    public bool doorEast;           // +X
    public bool doorSouth = true;   // -Z
    public bool doorWest;           // -X

    [Header("Opening size")]
    public float doorWidth = 2f;
    public float doorHeight = 3f;

    [Header("Stair (overrides the flat build)")]
    public bool isStair;
    public float stairRise = 4.5f;   // exit connector ends this high; the ramp climbs south -> north

    [Header("Look")]
    public Material greyboxMaterial;

    private enum Side { North, East, South, West }

    private void Awake()
    {
        if (isStair) BuildStair();
        else BuildFlat();
        AutoBounds();
    }

    private void BuildFlat()
    {
        float w = sizeCells.x * cellSize;
        float d = sizeCells.y * cellSize;
        float t = wallThickness;

        Box("Floor", new Vector3(0, -t * 0.5f, 0), new Vector3(w, t, d), walkable: true);
        if (buildCeiling)
            // Ceiling is an obstacle like the walls (walkable:false) -- no navmesh baked on top of it.
            Box("Ceiling", new Vector3(0, ceilingHeight + t * 0.5f, 0), new Vector3(w, t, d));

        BuildSide(Side.North, doorNorth, w, d);
        BuildSide(Side.South, doorSouth, w, d);
        BuildSide(Side.East, doorEast, w, d);
        BuildSide(Side.West, doorWest, w, d);
    }

    private void BuildSide(Side side, bool hasDoor, float w, float d)
    {
        bool alongX = side == Side.North || side == Side.South;
        float runLen = alongX ? w : d;
        float edge = alongX ? d * 0.5f : w * 0.5f;
        float sign = (side == Side.North || side == Side.East) ? 1f : -1f;
        Vector3 outward = alongX ? new Vector3(0, 0, sign) : new Vector3(sign, 0, 0);
        Vector3 runAxis = alongX ? Vector3.right : Vector3.forward;
        Vector3 edgeCenter = outward * edge;
        float h = ceilingHeight, t = wallThickness;

        if (!hasDoor)
        {
            Vector3 size = alongX ? new Vector3(runLen, h, t) : new Vector3(t, h, runLen);
            Box($"Wall_{side}", edgeCenter + Vector3.up * (h * 0.5f), size);
            return;
        }

        // Two segments either side of the opening.
        float half = doorWidth * 0.5f;
        float segLen = runLen * 0.5f - half;
        if (segLen > 0.01f)
        {
            float off = (runLen * 0.5f + half) * 0.5f;
            Vector3 segSize = alongX ? new Vector3(segLen, h, t) : new Vector3(t, h, segLen);
            Box($"Wall_{side}_L", edgeCenter - runAxis * off + Vector3.up * (h * 0.5f), segSize);
            Box($"Wall_{side}_R", edgeCenter + runAxis * off + Vector3.up * (h * 0.5f), segSize);
        }
        // Lintel above the opening.
        if (h > doorHeight + 0.01f)
        {
            float lh = h - doorHeight;
            Vector3 lintel = alongX ? new Vector3(doorWidth, lh, t) : new Vector3(t, lh, doorWidth);
            Box($"Wall_{side}_Lintel", edgeCenter + Vector3.up * (doorHeight + lh * 0.5f), lintel);
        }
        AddConnector($"Connector_{side}", edgeCenter, outward);
    }

    private void BuildStair()
    {
        // A ramp corridor climbing south (Y0) -> north (Y+rise). Entry doorway low, exit high.
        float w = sizeCells.x * cellSize;
        float run = sizeCells.y * cellSize;
        float t = wallThickness;

        float slopeLen = Mathf.Sqrt(run * run + stairRise * stairRise);
        float angle = Mathf.Atan2(stairRise, run) * Mathf.Rad2Deg;

        var ramp = Box("Ramp", new Vector3(0, stairRise * 0.5f - t * 0.5f, 0), new Vector3(w, t, slopeLen), walkable: true);
        ramp.localRotation = Quaternion.Euler(-angle, 0, 0);   // +Z end tilts up

        float wallH = stairRise + ceilingHeight;
        Box("Wall_W", new Vector3(-w * 0.5f, wallH * 0.5f, 0), new Vector3(t, wallH, run));
        Box("Wall_E", new Vector3(w * 0.5f, wallH * 0.5f, 0), new Vector3(t, wallH, run));

        AddConnector("Connector_Entry", new Vector3(0, 0, -run * 0.5f), new Vector3(0, 0, -1));
        AddConnector("Connector_Exit", new Vector3(0, stairRise, run * 0.5f), new Vector3(0, 0, 1));
    }

    // walkable=true  -> a surface the agent stands ON (floor, ramp): contributes navmesh.
    // walkable=false -> an obstacle (wall, ceiling, lintel). It STILL carves the navmesh -- a
    //   Not-Walkable box is a solid obstruction, so the floor navmesh gets a hole where it stands
    //   and only bridges at doorways -- but the bake NEVER lays navmesh on TOP of it. This is what
    //   keeps navmesh strictly on floors/ramps and never on wall tops or the roof.
    private Transform Box(string name, Vector3 localPos, Vector3 size, bool walkable = false)
    {
        var go = GameObject.CreatePrimitive(PrimitiveType.Cube);   // gives MeshRenderer + BoxCollider
        go.name = name;
        go.transform.SetParent(transform, false);
        go.transform.localPosition = localPos;
        go.transform.localScale = size;
        go.layer = gameObject.layer;
        if (greyboxMaterial != null) go.GetComponent<MeshRenderer>().sharedMaterial = greyboxMaterial;
        if (!walkable)
        {
            var mod = go.AddComponent<NavMeshModifier>();
            mod.overrideArea = true;
            mod.area = 1;   // 1 = built-in "Not Walkable" area
        }
        return go.transform;
    }

    private void AddConnector(string name, Vector3 localPos, Vector3 outward)
    {
        var go = new GameObject(name);
        go.transform.SetParent(transform, false);
        go.transform.localPosition = localPos;
        go.transform.localRotation = Quaternion.LookRotation(outward, Vector3.up);
        go.layer = gameObject.layer;   // so a procedural cap on this doorway inherits the tile's (env) layer
        var conn = go.AddComponent<TileConnector>();
        // Tell the connector how big its doorway is, so the generator can plug it if it's left open.
        conn.openingWidth = doorWidth;
        conn.openingHeight = doorHeight;
        conn.thickness = wallThickness;
        conn.capMaterial = greyboxMaterial;
    }

    private void AutoBounds()
    {
        var meta = GetComponent<TileMeta>();
        if (meta == null) return;
        float w = sizeCells.x * cellSize;
        float d = sizeCells.y * cellSize;
        float h = isStair ? stairRise + ceilingHeight : ceilingHeight;
        meta.boundsSize = new Vector3(w, h, d);
        meta.boundsCenter = new Vector3(0, h * 0.5f, 0);
    }
}
