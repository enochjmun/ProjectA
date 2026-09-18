using System.Collections;
using System.Collections.Generic;
using Unity.AI.Navigation;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.AI;

/// <summary>
/// Seeded, deterministic ROOM-GRAPH dungeon generator (single-story).
///
/// MODEL: the map is a 2D grid of cells. ROOMS are the major nodes -- rectangles stamped onto the
/// grid, and may sit against each other. Rooms that TOUCH open directly into each other via a
/// shared-wall doorway; rooms that DON'T are bridged by CORRIDORS -- cell paths carved between their
/// side-doors, reusing each other's cells so junctions and loops form. Geometry is then built from the
/// grid: a wall goes on any cell side whose neighbour is empty, and an opening on any side whose
/// neighbour is another walkable cell. Consequences that fix our three bugs at the source:
///   * a doorway can NEVER face empty space           -> no "open ends into the void"
///   * tiles are grid cells, so they never overlap    -> no mis-snapped geometry
///   * floors are contiguous and grid-aligned         -> the NavMesh bakes as one clean sheet
///
/// DETERMINISM: one System.Random, fixed iteration order, index-only iteration. Every client
/// rebuilds an identical dungeon from the single broadcast seed -- no per-tile NetworkObjects.
///
/// This replaces the old connector-snapping generator. The tile model (TileMeta/TileConnector/
/// GreyboxTileShape) is no longer used here; keep it around for modular ART later, when the grid
/// becomes a tileset the art kit is stamped over.
/// </summary>
[RequireComponent(typeof(NetworkObject))]
public class DungeonGenerator : NetworkBehaviour
{
    [Header("Grid")]
    [Tooltip("Grid size in cells (X by Z). Each cell is `cellSize` metres.")]
    [SerializeField] private Vector2Int gridSize = new Vector2Int(28, 28);
    [SerializeField] private float cellSize = 4f;
    [Tooltip("Corridor ceiling height. Keep >= doorHeight so doorways aren't taller than the corridor.")]
    // 3f matches the modelled Blender kit (3m interior height). Changing this does NOT resize kit
    // pieces -- it drives lamp mount height and the greybox fallback, so it must agree with the art.
    [SerializeField] private float corridorCeilingHeight = 3f;
    [Tooltip("Room ceiling height -- set higher than the corridor so rooms read as grander spaces.")]
    // 4f matches the modelled kit's rooms (taller than the 3m corridors, so a doorway reads as arrival).
    // Also one of the two inputs to the navmesh bake ceiling, which takes min(corridor, room).
    [SerializeField] private float roomCeilingHeight = 4f;
    // 0.4f matches the modelled kit. PlaceLights() derives its wall-face offset from this and
    // corridorWidth, so if these drift from the art, lamps float off walls or sink into them.
    [SerializeField] private float wallThickness = 0.4f;
    [SerializeField] private bool buildCeiling = true;
    [Tooltip("Framed doorway opening where a corridor meets a room (width x height, metres).")]
    [SerializeField] private float doorWidth = 2f;
    [SerializeField] private float doorHeight = 3f;
    [Tooltip("Walkable width of corridors (< cellSize). Match to doorWidth for clean room joins, and " +
             "keep it > 2x your NavMesh agent radius or the agent won't fit down them.")]
    [SerializeField] private float corridorWidth = 3f;

    [Header("Rooms (the major nodes)")]
    [SerializeField] private int roomCount = 7;
    [Tooltip("Smallest / largest room footprint, in cells.")]
    [SerializeField] private Vector2Int roomCellsMin = new Vector2Int(2, 2);
    [SerializeField] private Vector2Int roomCellsMax = new Vector2Int(4, 4);
    [Tooltip("Empty cells forced around every room. 0 = rooms may sit against each other (needed for " +
             "direct room-to-room doorways); higher separates rooms so more links become corridors.")]
    [Min(0)] [SerializeField] private int roomGap = 0;
    [Tooltip("Fraction of TOUCHING room pairs that open directly into each other through a shared-wall " +
             "doorway (the rest stay walled). Higher = more of a connected warren; corridors only ever " +
             "bridge rooms that AREN'T neighbours.")]
    [Range(0f, 1f)] [SerializeField] private float directDoorChance = 0.6f;

    [Header("Corridors / loops")]
    [Tooltip("Fraction of the extra (non-tree) room links added back as LOOPS. 0 = pure tree (dead ends), 1 = very loopy.")]
    [Range(0f, 1f)] [SerializeField] private float loopFactor = 0.2f;
    [Tooltip("Cost added each time a corridor CHANGES direction. Higher = longer straight runs / " +
             "fewer turns; ~0 = shortest path (may stair-step).")]
    [Min(0f)] [SerializeField] private float turnCost = 4f;
    [Tooltip("Extra cost to route THROUGH a room's door cell. Higher = corridors avoid threading " +
             "through a doorway they aren't targeting, so each door stays meaningful.")]
    [Min(0f)] [SerializeField] private float roomHugCost = 3f;
    [Tooltip("Extra cost for a step that would complete a 2x2 block of corridor cells. Those blocks are " +
             "where the modular kit produces free-standing PILLARS (four pieces' corner posts meeting), " +
             "and they widen passages in a game that wants tight sightlines.\n\n" +
             "This is a COST, not a ban, on purpose: set high it eliminates blocks in practice while " +
             "still letting the router squeeze through if a layout leaves no alternative. A hard ban " +
             "could make rooms unroutable and send the connectivity guard into rerolls. Lower it if " +
             "corridors are taking absurd detours; raise it if pillars still appear.")]
    [Min(0f)] [SerializeField] private float openBlockCost = 25f;

    [Header("Spawns -- scattered so players land SEPARATED")]
    [Tooltip("How many landing points to scatter (make this >= your max chase size).")]
    [SerializeField] private int spawnCount = 6;
    [SerializeField] private GameObject spawnPointPrefab;    // DungeonSpawnPoint

    [Header("Objective -- spread across the CENTRAL band of rooms (co-op, team-unlock)")]
    [SerializeField] private int objectiveCount = 3;
    [SerializeField] private GameObject objectivePrefab;     // objective trigger

    [Header("Exits -- fixed count, pushed AWAY from the objective and spread apart")]
    [SerializeField] private int exitCount = 3;
    [SerializeField] private GameObject exitPrefab;          // DungeonExit (single-use, wired next slice)

    [Header("Look / bake")]
    [SerializeField] private Material greyboxMaterial;

    // ---- MODULAR KIT ----------------------------------------------------------------------------
    // Swaps the primitive greybox boxes for modelled Blender pieces. Kept as a TOGGLE rather than a
    // replacement so you can A/B the two instantly and fall back if a piece is wrong mid-session.
    // Any cell the kit can't resolve falls back to greybox automatically and logs a warning, so a
    // missing piece shows up as a message rather than a hole in the level.
    [Header("Modular kit (leave off to use greybox boxes)")]
    [SerializeField] private bool useKitPieces = false;

    // Corridor pieces, named by their OPENINGS (not their walls). Between them these cover every
    // corridor cell the generator can produce except a dead end (one opening), which shouldn't occur
    // because corridors route between room doors -- if one does, you'll see the warning.
    [Tooltip("Two OPPOSITE openings. Modelled canonically as open North+South.")]
    [SerializeField] private GameObject pieceStraight;
    [Tooltip("Two ADJACENT openings. Modelled canonically as open North+East.")]
    [SerializeField] private GameObject pieceCorner;
    [Tooltip("Three openings. Modelled canonically as open North+East+West (solid wall to the South).")]
    [SerializeField] private GameObject pieceTJunction;
    [Tooltip("Four openings. Rotation is irrelevant, but a yaw offset still applies if you want it.")]
    [SerializeField] private GameObject pieceCross;

    [Tooltip("Per-piece yaw correction in 90-degree steps, applied on top of the computed rotation. " +
             "Use this if a piece was exported facing the wrong way -- it saves a Blender round trip. " +
             "Order: Straight, Corner, T-Junction, Cross.")]
    [SerializeField] private int[] pieceYawSteps = new int[4];

    [Tooltip("Room prefabs. Each needs a DungeonKitPiece component declaring its cell footprint and " +
             "opening anchors. A generated room uses the prefab whose sizeInCells matches it exactly; " +
             "rooms with no matching prefab stay greybox, so mixed sizes degrade gracefully.")]
    [SerializeField] private GameObject[] roomPieces;
    [Tooltip("Greybox: bake ONE NavMesh over the whole dungeon at runtime (server only).")]
    [SerializeField] private bool runtimeBakeNavMesh = true;
    [Tooltip("Bake voxel size in metres. The runtime default (~0.17) is too coarse to carve a walkable " +
             "strip through a doorway that's flanked on both sides by jambs/posts, so it drops the " +
             "navmesh connection at the seam even though the space is passable -> room/corridor islands. " +
             "~0.08-0.1 resolves those doorway seams. Smaller = cleaner joins but slower bake.")]
    [SerializeField] private float navMeshVoxelSize = 0.1f;
    [Tooltip("How far BELOW the lowest ceiling the bake volume stops. Keeps the roof's upper surface out " +
             "of the voxeliser, which would otherwise bake a second walkable sheet across the top of the " +
             "dungeon (kit pieces are one mesh, so a Not-Walkable modifier can't target just the ceiling). " +
             "Must stay comfortably above agent height -- with a 3m ceiling, 0.2 leaves 2.8m of headroom.")]
    [SerializeField] private float navMeshCeilingMargin = 0.2f;
    [Tooltip("Connectivity guard: if the baked navmesh leaves a spawn/objective/exit unreachable, reroll " +
             "the seed up to this many times. Rare now every doorway has a link, so it usually runs once.")]
    [SerializeField] private int maxGenAttempts = 6;

    [Header("Debug")]
    [SerializeField] private KeyCode debugRegenKey = KeyCode.G;

    [Header("Lighting (concrete biome profile)")]
    [Tooltip("Optional wall-lamp fixture prefab (its own Light + housing). If null, a bare point light is built from the knobs below.")]
    [SerializeField] private GameObject wallLampPrefab;
    [Tooltip("Chance (0-1) that any given exterior wall face gets a lamp. This is your DENSITY dial -- keep it low so there's real dark between the pools.")]
    [Range(0f, 1f)] [SerializeField] private float wallLampChance = 0.06f;
    [Tooltip("Cold lamp colour for the concrete biome.")]
    [SerializeField] private Color lampColor = new Color(0.90f, 0.95f, 1f);
    [Tooltip("Intensity of the CODE-BUILT wall lamps (ignored when a fixture prefab is assigned).")]
    [Range(0f, 20f)] [SerializeField] private float lampIntensity = 2f;
    [Tooltip("Range of the code-built fallback lights.")]
    [SerializeField] private float lampRange = 6f;
    [Tooltip("Mount height of wall lamps above the floor.")]
    [SerializeField] private float wallLampHeight = 2.2f;
    [Tooltip("Add the volumetric-fog package's VolumetricAdditionalLight to code-built lamps so their " +
             "shafts/glow show in fog. (Fixture prefabs should carry the component themselves.)")]
    [SerializeField] private bool addVolumetricToLights = true;
    [Tooltip("Chance (0-1) that a code-built WALL lamp gets a failing-electrics flicker. Fixture prefabs use flickering VARIANTS instead.")]
    [Range(0f, 1f)] [SerializeField] private float lampFlickerChance = 0.25f;

    public static DungeonGenerator Instance { get; private set; }

    /// <summary>World-space box roughly enclosing the generated dungeon (for per-area effects like the post grade).</summary>
    public Bounds WorldBounds => new Bounds(
        transform.position + Vector3.up * (roomCeilingHeight * 0.5f),
        new Vector3(gridSize.x * cellSize, roomCeilingHeight + 4f, gridSize.y * cellSize));

    // Server writes the seed; every client reads it and generates the same dungeon.
    private readonly NetworkVariable<int> _seed = new NetworkVariable<int>(
        0, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);

    private enum Cell { Empty, Room, Corridor }
    private Cell[,] _grid;         // what each cell IS
    private int[,] _cellRoom;      // which room a cell belongs to (-1 = none)
    private bool[,] _adjRoom;      // empty cell that orthogonally touches a room (a "beside a wall" cell)
    private List<Vector2Int>[] _roomDoors;    // per room: its usable door cells (one per open side)
    private HashSet<Vector2Int> _doorCells;   // all door cells -- the ONLY room-adjacent cells a corridor may enter
    private HashSet<(Vector2Int, Vector2Int)> _directDoors;   // shared-wall doorways between TOUCHING rooms
    private readonly List<RectInt> _rooms = new List<RectInt>();
    private Transform _root;
    private System.Random _rng;
    private int _genAttempts;                                   // connectivity-guard reroll counter
    private Coroutine _valRoutine;                              // pending post-bake connectivity check
    private readonly List<Vector3> _specialPoints = new List<Vector3>();   // spawns/objective/exits -> guard checks these are reachable

    private static readonly Vector2Int[] Dirs =
        { new Vector2Int(1, 0), new Vector2Int(-1, 0), new Vector2Int(0, 1), new Vector2Int(0, -1) };

    private void Awake() => Instance = this;

    public override void OnDestroy()
    {
        if (Instance == this) Instance = null;
        base.OnDestroy();
    }

    public override void OnNetworkSpawn()
    {
        _seed.OnValueChanged += OnSeedChanged;
        if (_seed.Value != 0) Generate(_seed.Value);   // a late joiner rebuilds the current dungeon
    }

    public override void OnNetworkDespawn() => _seed.OnValueChanged -= OnSeedChanged;

    private void OnSeedChanged(int _, int now) => Generate(now);

    /// <summary>Server: pick a new seed (or use the given one) and regenerate on every client.</summary>
    public void ServerGenerate(int? seed = null)
    {
        if (!IsServer) return;
        int s = seed ?? new System.Random().Next(1, int.MaxValue);
        if (s == _seed.Value) s++;   // force OnValueChanged even if the same number recurs
        _seed.Value = s;
    }

    private void Update()
    {
        if (IsServer && Input.GetKeyDown(debugRegenKey)) ServerGenerate();
    }

    // Draw the computed WorldBounds when the generator is selected, so you can confirm the box wraps
    // the generated dungeon (PlayerLocalGlow uses this box to decide whether the owner glow is on).
    private void OnDrawGizmosSelected()
    {
        Gizmos.color = new Color(0.3f, 0.8f, 1f, 0.9f);
        var b = WorldBounds;
        Gizmos.DrawWireCube(b.center, b.size);
    }

    // ================================================================ generation

    private void Generate(int seed)
    {
        if (_root != null)
        {
            // DEACTIVATE BEFORE DESTROYING, and the order matters.
            //
            // Destroy() is deferred to the end of the frame, but generation AND the navmesh bake both
            // run inside THIS frame. The bake uses CollectObjects.Volume, which gathers every ACTIVE
            // object inside the bounds on this layer -- so a merely-queued-for-destruction dungeon is
            // still collected, and the new navmesh bakes over both layouts at once. First generation
            // looks fine because there's nothing stale to collect; every regen after is corrupt.
            //
            // SetActive(false) takes effect immediately, so the old geometry drops out of the bake even
            // though the GameObject survives until end of frame. (This couldn't happen under the old
            // CollectObjects.Children, where stale geometry was never a child of the new root.)
            _root.gameObject.SetActive(false);
            Destroy(_root.gameObject);
        }
        _rooms.Clear();
        _rng = new System.Random(seed);

        _grid = new Cell[gridSize.x, gridSize.y];       // defaults to Empty (0)
        _cellRoom = new int[gridSize.x, gridSize.y];
        for (int x = 0; x < gridSize.x; x++)
            for (int y = 0; y < gridSize.y; y++)
                _cellRoom[x, y] = -1;

        // Doors are rebuilt with the geometry, so their ids must restart from 0 or they'd keep climbing
        // across regens and stop matching the freshly-built doors.
        DungeonDoors.Instance?.ResetForGenerate();

        _root = new GameObject($"Dungeon_{seed}").transform;
        _root.SetPositionAndRotation(transform.position, Quaternion.identity);

        PlaceRooms();               // 1) scatter the major nodes, non-adjacent
        ComputeRoomAdjacency();     //     flag cells beside a room so corridors don't skim room walls
        ComputeDoors();             //     one door slot per room side -- corridors may only touch a room here
        CarveCorridors();           // 2+3) room graph (MST + loops), each link routed as a corridor
        BuildGeometry();            // 4) floors/walls/ceilings from the grid (walls-by-construction)
        PlaceLights(seed);          // 4b) sparse cold wall lamps (deterministic, local)
        PlaceSpecialContent();      // 5) start / exit / objective in tagged rooms

        if (runtimeBakeNavMesh && IsServer)
        {
            BakeNavMesh();
            BuildDoorwayLinks();
            if (_valRoutine != null) StopCoroutine(_valRoutine);
            _valRoutine = StartCoroutine(ValidateConnectivityRoutine(seed));   // reroll if anything's stranded
        }
    }

    // ---- 1) Rooms: rejection-sample rectangles with a forced empty gap around each ----
    private void PlaceRooms()
    {
        int attempts = roomCount * 40;
        while (_rooms.Count < roomCount && attempts-- > 0)
        {
            int w = _rng.Next(roomCellsMin.x, roomCellsMax.x + 1);
            int h = _rng.Next(roomCellsMin.y, roomCellsMax.y + 1);
            int maxX = gridSize.x - w - roomGap;
            int maxY = gridSize.y - h - roomGap;
            if (maxX <= roomGap || maxY <= roomGap) continue;   // room bigger than the grid allows
            int x = _rng.Next(roomGap, maxX);
            int y = _rng.Next(roomGap, maxY);
            var rect = new RectInt(x, y, w, h);
            if (AreaIsEmpty(rect, roomGap)) StampRoom(rect);
        }
    }

    // rect expanded by `pad` on all sides must be in-bounds and entirely Empty.
    private bool AreaIsEmpty(RectInt rect, int pad)
    {
        for (int x = rect.xMin - pad; x < rect.xMax + pad; x++)
            for (int y = rect.yMin - pad; y < rect.yMax + pad; y++)
            {
                if (x < 0 || y < 0 || x >= gridSize.x || y >= gridSize.y) return false;
                if (_grid[x, y] != Cell.Empty) return false;
            }
        return true;
    }

    private void StampRoom(RectInt rect)
    {
        int id = _rooms.Count;
        for (int x = rect.xMin; x < rect.xMax; x++)
            for (int y = rect.yMin; y < rect.yMax; y++)
            {
                _grid[x, y] = Cell.Room;
                _cellRoom[x, y] = id;
            }
        _rooms.Add(rect);
    }

    // Flag every empty cell that orthogonally touches a room, so routing can avoid skimming room walls.
    private void ComputeRoomAdjacency()
    {
        _adjRoom = new bool[gridSize.x, gridSize.y];
        for (int x = 0; x < gridSize.x; x++)
            for (int y = 0; y < gridSize.y; y++)
            {
                if (_cellRoom[x, y] != -1) continue;   // only non-room cells matter
                for (int di = 0; di < Dirs.Length; di++)
                {
                    var nb = new Vector2Int(x + Dirs[di].x, y + Dirs[di].y);
                    if (InBounds(nb) && _cellRoom[nb.x, nb.y] != -1) { _adjRoom[x, y] = true; break; }
                }
            }
    }

    // ---- 2+3) Room graph. Rooms that TOUCH open directly into each other via a shared-wall doorway;
    //          rooms that don't are bridged by a corridor. Nearest pairs are linked first (Kruskal),
    //          so touching rooms connect directly and a spanning tree guarantees connectivity; then
    //          extra links add loops -- more direct doors between neighbours, more corridors elsewhere.
    private void CarveCorridors()
    {
        int n = _rooms.Count;
        _directDoors = new HashSet<(Vector2Int, Vector2Int)>();
        if (n < 2) return;

        // every room pair, sorted by centre distance (touching rooms are the closest, so they link first)
        var pairs = new List<(int a, int b, float d)>();
        for (int i = 0; i < n; i++)
            for (int j = i + 1; j < n; j++)
                pairs.Add((i, j, Vector2.Distance(RoomCentreCellF(_rooms[i]), RoomCentreCellF(_rooms[j]))));
        pairs.Sort((p, q) =>
        {
            int c = p.d.CompareTo(q.d);
            if (c != 0) return c;
            c = p.a.CompareTo(q.a);
            return c != 0 ? c : p.b.CompareTo(q.b);
        });

        var uf = new int[n];
        for (int i = 0; i < n; i++) uf[i] = i;
        foreach (var e in pairs)
        {
            bool adjacent  = RoomsAdjacent(e.a, e.b);
            bool connected = Find(uf, e.a) == Find(uf, e.b);

            if (!connected)
            {
                // needed to connect the map: a direct doorway if the rooms touch, else a corridor
                if (adjacent) CarveDirectDoor(e.a, e.b);
                else Route(e.a, e.b);
                Union(uf, e.a, e.b);
            }
            else if (adjacent)
            {
                if (_rng.NextDouble() < directDoorChance) CarveDirectDoor(e.a, e.b);   // extra room-to-room door
            }
            else if (_rng.NextDouble() < loopFactor)
            {
                Route(e.a, e.b);                                                        // extra corridor loop
            }
        }
    }

    // Do two rooms share a wall (orthogonally adjacent rects)? Then they can open directly into each other.
    private bool RoomsAdjacent(int ai, int bi)
    {
        var a = _rooms[ai]; var b = _rooms[bi];
        bool vert  = (a.xMax == b.xMin || b.xMax == a.xMin) && a.yMin < b.yMax && b.yMin < a.yMax;
        bool horiz = (a.yMax == b.yMin || b.yMax == a.yMin) && a.xMin < b.xMax && b.xMin < a.xMax;
        return vert || horiz;
    }

    // Record ONE doorway at the middle of the wall two touching rooms share.
    private void CarveDirectDoor(int ai, int bi)
    {
        var d = PickSharedDoor(ai, bi);
        if (d != null) _directDoors.Add(Canon(d.Value.a, d.Value.b));
    }

    // The (room-A cell, room-B cell) pair at the centre of the shared wall, or null if they don't touch.
    private (Vector2Int a, Vector2Int b)? PickSharedDoor(int ai, int bi)
    {
        var a = _rooms[ai]; var b = _rooms[bi];
        if (a.xMax == b.xMin && a.yMin < b.yMax && b.yMin < a.yMax)   // A right | B left
        {
            int my = (Mathf.Max(a.yMin, b.yMin) + Mathf.Min(a.yMax, b.yMax) - 1) / 2;
            return (new Vector2Int(a.xMax - 1, my), new Vector2Int(a.xMax, my));
        }
        if (b.xMax == a.xMin && a.yMin < b.yMax && b.yMin < a.yMax)   // B right | A left
        {
            int my = (Mathf.Max(a.yMin, b.yMin) + Mathf.Min(a.yMax, b.yMax) - 1) / 2;
            return (new Vector2Int(a.xMin, my), new Vector2Int(a.xMin - 1, my));
        }
        if (a.yMax == b.yMin && a.xMin < b.xMax && b.xMin < a.xMax)   // A top | B bottom
        {
            int mx = (Mathf.Max(a.xMin, b.xMin) + Mathf.Min(a.xMax, b.xMax) - 1) / 2;
            return (new Vector2Int(mx, a.yMax - 1), new Vector2Int(mx, a.yMax));
        }
        if (b.yMax == a.yMin && a.xMin < b.xMax && b.xMin < a.xMax)   // B top | A bottom
        {
            int mx = (Mathf.Max(a.xMin, b.xMin) + Mathf.Min(a.xMax, b.xMax) - 1) / 2;
            return (new Vector2Int(mx, a.yMin), new Vector2Int(mx, a.yMin - 1));
        }
        return null;
    }

    // Order a cell-pair canonically so (A,B) and (B,A) hash to the same doorway entry.
    private static (Vector2Int, Vector2Int) Canon(Vector2Int a, Vector2Int b)
        => (a.x < b.x || (a.x == b.x && a.y <= b.y)) ? (a, b) : (b, a);

    private bool IsDirectDoor(Vector2Int a, Vector2Int b) => _directDoors.Contains(Canon(a, b));

    private static int Find(int[] uf, int i) { while (uf[i] != i) { uf[i] = uf[uf[i]]; i = uf[i]; } return i; }
    private static void Union(int[] uf, int a, int b) { uf[Find(uf, a)] = Find(uf, b); }

    // Route one corridor between two rooms, DOOR-to-DOOR. Each room has at most one door per SIDE
    // (its side-centre cell); we try A's doors nearest B against B's doors nearest A until one routes.
    // Because every OTHER room-adjacent cell is solid to the pathfinder (see Passable), corridors can
    // only touch a room at a door -- so multiple connections to a room funnel to a shared door and
    // MERGE into a junction outside it, instead of each punching its own hole.
    private void Route(int aIdx, int bIdx)
    {
        var aDoors = DoorsSorted(aIdx, RoomCentreCellF(_rooms[bIdx]));
        var bDoors = DoorsSorted(bIdx, RoomCentreCellF(_rooms[aIdx]));
        for (int ai = 0; ai < aDoors.Count; ai++)
            for (int bi = 0; bi < bDoors.Count; bi++)
            {
                var path = FindPath(aDoors[ai], bDoors[bi]);
                if (path == null) continue;
                foreach (var c in path)
                    if (_grid[c.x, c.y] == Cell.Empty) _grid[c.x, c.y] = Cell.Corridor;
                return;   // the first (most-facing) door pair that connects wins
            }
    }

    // One candidate door per side: the open cell just outside the centre of each of a room's four
    // edges. Computed once; Passable() lets corridors touch a room ONLY at these cells.
    private void ComputeDoors()
    {
        _roomDoors = new List<Vector2Int>[_rooms.Count];
        _doorCells = new HashSet<Vector2Int>();
        for (int i = 0; i < _rooms.Count; i++)
        {
            var r = _rooms[i];

            // If a modelled room prefab covers this footprint, the PREFAB decides where the doors are and
            // corridors route to those cells. Otherwise fall back to the old convention (one door at the
            // middle-ish cell of each side) which is all a greybox room needs.
            //
            // This inverts the dependency deliberately. The convention below is `width / 2` with integer
            // division -- for a 2x2 room that's cell 1 on every side, so all four doors bias to the same
            // corner. Making the art match that is both unintuitive and fragile, and it breaks the moment
            // a room is a different size. Reading the openings instead means any room, any dimensions,
            // any door placement works as long as each opening sits on a cell edge.
            var kit = RoomKitFor(r);
            Vector2Int[] candidates = (kit != null && kit.openings != null && kit.openings.Count > 0)
                ? DoorCellsFromKit(r, kit)
                : DefaultDoorCells(r);

            var doors = new List<Vector2Int>();
            foreach (var c in candidates)
                if (InBounds(c) && _cellRoom[c.x, c.y] == -1)   // that side is open (no room blocking it)
                {
                    doors.Add(c);
                    _doorCells.Add(c);
                }
            _roomDoors[i] = doors;
        }
    }

    /// <summary>
    /// The DungeonKitPiece on whichever room prefab matches this footprint, or null for none. Read off
    /// the PREFAB ASSET -- this runs during ComputeDoors, long before any room is instantiated.
    /// </summary>
    private DungeonKitPiece RoomKitFor(RectInt rect)
    {
        if (!useKitPieces || roomPieces == null) return null;
        foreach (var p in roomPieces)
        {
            if (p == null) continue;
            var k = p.GetComponent<DungeonKitPiece>();
            if (k != null && k.sizeInCells.x == rect.width && k.sizeInCells.y == rect.height) return k;
        }
        return null;
    }

    /// <summary>
    /// Door cells derived from a room prefab's actual openings: for each anchor, the cell just OUTSIDE
    /// the room on that side. That outside cell is what a corridor routes to, so the corridor always
    /// arrives at a modelled hole rather than a wall.
    /// </summary>
    private Vector2Int[] DoorCellsFromKit(RectInt rect, DungeonKitPiece kit)
    {
        var list = new List<Vector2Int>();
        foreach (var anchor in kit.openings)
        {
            if (anchor == null) continue;
            Vector2Int side = kit.SideOf(anchor);
            Vector2Int off = kit.CellOffsetOf(anchor, cellSize);

            // Clamp into the footprint: an anchor a little outside (or a mismatched prefab) shouldn't
            // produce a door cell floating away from the room.
            int px = Mathf.Clamp(rect.xMin + off.x, rect.xMin, rect.xMax - 1);
            int py = Mathf.Clamp(rect.yMin + off.y, rect.yMin, rect.yMax - 1);

            var door = new Vector2Int(px + side.x, py + side.y);
            if (!list.Contains(door)) list.Add(door);
        }
        return list.ToArray();
    }

    /// <summary>Fallback for greybox rooms: one door at the middle-ish cell of each side.</summary>
    private Vector2Int[] DefaultDoorCells(RectInt r)
    {
        int cx = r.xMin + r.width / 2;
        int cy = r.yMin + r.height / 2;
        return new[]
        {
            new Vector2Int(cx, r.yMax),       // north
            new Vector2Int(cx, r.yMin - 1),   // south
            new Vector2Int(r.xMax, cy),       // east
            new Vector2Int(r.xMin - 1, cy),   // west
        };
    }

    // Room i's door cells, nearest `target` first, so the door facing the neighbour is tried first.
    private List<Vector2Int> DoorsSorted(int i, Vector2 target)
    {
        var doors = new List<Vector2Int>(_roomDoors[i]);
        doors.Sort((a, b) =>
        {
            float da = (new Vector2(a.x, a.y) - target).sqrMagnitude;
            float db = (new Vector2(b.x, b.y) - target).sqrMagnitude;
            int c = da.CompareTo(db);
            if (c != 0) return c;
            c = a.x.CompareTo(b.x);
            return c != 0 ? c : a.y.CompareTo(b.y);   // total order -> deterministic
        });
        return doors;
    }

    // A* whose search STATE is (cell, arrival-direction), so it can charge `turnCost` every time the
    // corridor changes heading. Result: long straight runs that only turn when they must (to reach the
    // target or route around a room) -- "straight, with turns" -- instead of a cell-by-cell wander.
    private List<Vector2Int> FindPath(Vector2Int start, Vector2Int goal)
    {
        var startNode = (cell: start, dir: -1);   // dir -1 = no heading yet
        var open = new List<(Vector2Int cell, int dir)> { startNode };
        var came = new Dictionary<(Vector2Int, int), (Vector2Int, int)>();
        var g = new Dictionary<(Vector2Int, int), float> { [startNode] = 0f };
        var f = new Dictionary<(Vector2Int, int), float> { [startNode] = Heuristic(start, goal) };

        while (open.Count > 0)
        {
            int best = 0;   // lowest-f node (linear scan is fine at this grid size)
            for (int i = 1; i < open.Count; i++)
                if (f[open[i]] < f[open[best]]) best = i;
            var cur = open[best];
            if (cur.cell == goal) return Reconstruct(came, cur);
            open.RemoveAt(best);

            for (int di = 0; di < Dirs.Length; di++)
            {
                var nb = cur.cell + Dirs[di];
                if (!InBounds(nb) || !Passable(nb)) continue;

                float step = EnterCost(nb);
                if (cur.dir != -1 && di != cur.dir) step += turnCost;   // changed heading -> turn penalty
                var next = (cell: nb, dir: di);

                float tentative = g[cur] + step;
                if (!g.TryGetValue(next, out float known) || tentative < known)
                {
                    came[next] = cur;
                    g[next] = tentative;
                    f[next] = tentative + Heuristic(nb, goal);
                    if (!open.Contains(next)) open.Add(next);
                }
            }
        }
        return null;   // no route (shouldn't happen on a connected grid)
    }

    // Rooms are SOLID to the pathfinder, and a room-adjacent cell is passable ONLY if it's a door
    // cell. So corridors can never run along a room wall or enter it except at one of its side doors
    // -- which is what caps a room at one doorway per side and forces extra links to merge outside.
    private bool Passable(Vector2Int c)
    {
        if (_cellRoom[c.x, c.y] != -1) return false;                       // rooms are solid
        if (_adjRoom[c.x, c.y] && !_doorCells.Contains(c)) return false;   // touch a room ONLY at a door
        return true;
    }

    private float EnterCost(Vector2Int c)
    {
        if (_grid[c.x, c.y] == Cell.Corridor) return 0.3f;                // reuse existing corridors -> loops
        float cost = 1f + (_adjRoom[c.x, c.y] ? roomHugCost : 0f);       // open cell; +cost if it hugs a room
        if (WouldFormCorridorBlock(c)) cost += openBlockCost;            // avoid 2x2 blocks -> no pillars
        return cost;
    }

    /// <summary>
    /// Would carving this cell complete a 2x2 block of corridor cells?
    ///
    /// WHY THIS MATTERS: a 2x2 open block is where the modular kit produces free-standing PILLARS. Each
    /// piece bakes its corner posts into its mesh, so when four open cells meet, four corner posts
    /// converge in the middle. The greybox path could dodge this by testing the diagonal before emitting
    /// each post (see CornerOpen in BuildCorridorCell) -- a kit piece can't, because its corners are
    /// geometry, not a decision.
    ///
    /// It's also a LAYOUT improvement independent of the art: 2x2 blocks are two-cell-wide passages,
    /// which open up sightlines. The chase design wants tight corridors and frequent line-of-sight
    /// breaks, so a corridor running alongside another corridor is arguably a routing bug in itself.
    ///
    /// Only CORRIDOR cells count. Room cells are excluded deliberately -- a corridor legitimately has to
    /// reach a room's door cell, and counting room cells here would penalise every approach.
    /// </summary>
    private bool WouldFormCorridorBlock(Vector2Int c)
    {
        // The cell sits in four possible 2x2 blocks, one per diagonal quadrant. A block is completed
        // when both orthogonal neighbours AND the diagonal between them are already corridor.
        for (int dx = -1; dx <= 1; dx += 2)
            for (int dy = -1; dy <= 1; dy += 2)
                if (IsCorridorCell(c.x + dx, c.y) &&
                    IsCorridorCell(c.x, c.y + dy) &&
                    IsCorridorCell(c.x + dx, c.y + dy))
                    return true;
        return false;
    }

    private bool IsCorridorCell(int x, int y) =>
        x >= 0 && y >= 0 && x < gridSize.x && y < gridSize.y && _grid[x, y] == Cell.Corridor;

    private static float Heuristic(Vector2Int a, Vector2Int b) =>
        Mathf.Abs(a.x - b.x) + Mathf.Abs(a.y - b.y);

    private static List<Vector2Int> Reconstruct(
        Dictionary<(Vector2Int, int), (Vector2Int, int)> came, (Vector2Int cell, int dir) cur)
    {
        var path = new List<Vector2Int> { cur.cell };
        while (came.TryGetValue(cur, out var prev)) { cur = prev; path.Add(cur.cell); }
        path.Reverse();
        return path;
    }

    // Doorway crossings recorded during BuildGeometry so we can drop a NavMeshLink across each after
    // baking. The runtime bake won't stitch the narrow, wall-flanked doorway seams, so links guarantee
    // the two sides connect. pos = crossing centre (local to _root, floor level); alongZ = the wall runs
    // along Z, so you cross it in X (else the wall runs along X and you cross in Z).
    private readonly List<(Vector3 pos, bool alongZ)> _doorways = new List<(Vector3, bool)>();

    // ---- 4) Geometry: one pass over the grid. Floor+ceiling per walkable cell; a wall on any
    //         side whose neighbour is Empty/off-grid, an opening on any side that's walkable. ----
    private void BuildGeometry()
    {
        _doorways.Clear();
        _kitCells.Clear();
        float t = wallThickness;

        // Rooms FIRST: a room prefab covers several cells at once, and those cells must then be skipped
        // by the per-cell loop below or we'd build greybox floors and walls inside the modelled room.
        if (useKitPieces) BuildKitRooms();

        for (int x = 0; x < gridSize.x; x++)
            for (int y = 0; y < gridSize.y; y++)
            {
                if (_grid[x, y] == Cell.Empty) continue;
                if (_kitCells.Contains(new Vector2Int(x, y))) continue;   // already covered by a room prefab
                Vector3 c = CellLocal(x, y);
                float h = CellHeight(x, y);   // rooms get a higher ceiling than corridors

                // A corridor kit piece brings its OWN floor, walls and ceiling, so it replaces the whole
                // cell -- hence the early continue before any Box() call. Returns false when no piece
                // matches this cell's opening mask, and we fall through to greybox.
                if (useKitPieces && _grid[x, y] == Cell.Corridor && TryPlaceCorridorPiece(x, y, c)) continue;

                Box("Floor", new Vector3(c.x, -t * 0.5f, c.z), new Vector3(cellSize, t, cellSize), walkable: true);
                if (buildCeiling)
                    Box("Ceiling", new Vector3(c.x, h + t * 0.5f, c.z), new Vector3(cellSize, t, cellSize), walkable: false);

                if (_grid[x, y] == Cell.Room) BuildRoomWalls(x, y, c, h);   // full-cell walls + framed doorways
                else BuildCorridorCell(x, y, c, h);                         // a NARROW walled passage
            }
    }

    // ---- MODULAR KIT PLACEMENT --------------------------------------------------------------------
    // Cells consumed by a multi-cell room prefab, so the per-cell loop skips them.
    private readonly HashSet<Vector2Int> _kitCells = new HashSet<Vector2Int>();

    // Direction bits, matching the order of Dirs: 0 = +X (East), 1 = -X (West), 2 = +Z (North),
    // 3 = -Z (South). A cell's "opening mask" sets a bit per side that leads somewhere walkable.
    private const int BitE = 1 << 0, BitW = 1 << 1, BitN = 1 << 2, BitS = 1 << 3;

    // Canonical masks -- the orientation each piece is MODELLED in. The rotation solver below finds how
    // far to spin the piece so its canonical mask lands on the cell's actual mask.
    private const int MaskStraight = BitN | BitS;          // open North+South
    private const int MaskCorner   = BitN | BitE;          // open North+East
    private const int MaskT        = BitN | BitE | BitW;   // open on three sides, solid to the South
    private const int MaskCross    = BitN | BitE | BitS | BitW;

    /// <summary>
    /// Rotate an opening mask 90 degrees clockwise about Y, once per step. Under a +90 yaw, what faced
    /// North now faces East: N->E, E->S, S->W, W->N. Rotating the MASK is much cheaper and less
    /// error-prone than rotating geometry and re-deriving which sides are open.
    /// </summary>
    private static int RotateMask(int mask, int steps)
    {
        for (int i = 0; i < ((steps % 4) + 4) % 4; i++)
        {
            int r = 0;
            if ((mask & BitN) != 0) r |= BitE;
            if ((mask & BitE) != 0) r |= BitS;
            if ((mask & BitS) != 0) r |= BitW;
            if ((mask & BitW) != 0) r |= BitN;
            mask = r;
        }
        return mask;
    }

    /// <summary>
    /// Which sides of this cell lead somewhere walkable. Uses the SAME OpenDir the greybox path uses,
    /// so a kit cell and a greybox cell always agree about where the passage exits -- that's what stops
    /// a modelled corridor opening into a greybox wall at the boundary between the two.
    /// </summary>
    private int OpeningMask(int x, int y)
    {
        int m = 0;
        if (OpenDir(x, y, 1, 0)) m |= BitE;
        if (OpenDir(x, y, -1, 0)) m |= BitW;
        if (OpenDir(x, y, 0, 1)) m |= BitN;
        if (OpenDir(x, y, 0, -1)) m |= BitS;
        return m;
    }

    /// <summary>
    /// Place the corridor piece matching this cell's opening mask. False = no piece fits, caller falls
    /// back to greybox.
    /// </summary>
    private bool TryPlaceCorridorPiece(int x, int y, Vector3 c)
    {
        int mask = OpeningMask(x, y);
        int bits = CountBits(mask);

        GameObject prefab; int canonical; int yawIndex;
        switch (bits)
        {
            case 4: prefab = pieceCross;     canonical = MaskCross;    yawIndex = 3; break;
            case 3: prefab = pieceTJunction; canonical = MaskT;        yawIndex = 2; break;
            case 2:
                // The two 2-opening cases are DIFFERENT pieces and neither derives from the other:
                // opposite openings give a straight run, adjacent give a corner.
                bool opposite = mask == (BitN | BitS) || mask == (BitE | BitW);
                prefab    = opposite ? pieceStraight : pieceCorner;
                canonical = opposite ? MaskStraight  : MaskCorner;
                yawIndex  = opposite ? 0 : 1;
                break;
            default:
                // 1 opening = dead end, 0 = isolated cell. No piece for either.
                Debug.LogWarning($"[DungeonGenerator] Corridor cell ({x},{y}) has {bits} opening(s) " +
                                 $"(mask {mask}) -- no kit piece for that. Falling back to greybox.", this);
                return false;
        }

        if (prefab == null) return false;   // piece not assigned yet -> greybox, silently

        // Find the rotation that maps the modelled orientation onto this cell.
        int steps = -1;
        for (int r = 0; r < 4; r++)
            if (RotateMask(canonical, r) == mask) { steps = r; break; }
        if (steps < 0) return false;        // unreachable if the canonical masks above are right

        int extra = (pieceYawSteps != null && yawIndex < pieceYawSteps.Length) ? pieceYawSteps[yawIndex] : 0;

        var go = Instantiate(prefab, _root);
        go.transform.localPosition = c;
        go.transform.localRotation = Quaternion.Euler(0f, (steps + extra) * 90f, 0f);
        SetLayerRecursively(go, gameObject.layer);
        return true;
    }

    private static int CountBits(int v)
    {
        int n = 0;
        while (v != 0) { n += v & 1; v >>= 1; }
        return n;
    }

    /// <summary>
    /// Set the layer on a whole prefab instance, children included.
    ///
    /// GameObject.layer is NOT recursive -- assigning it on a prefab root leaves every child on whatever
    /// layer the prefab was authored with. That matters because an imported FBX puts its mesh renderers
    /// on CHILD objects, and the navmesh bake is layer-masked to this generator's layer: root-only
    /// assignment means the bake collects nothing and the dungeon has no navmesh at all.
    /// </summary>
    private static void SetLayerRecursively(GameObject go, int layer)
    {
        go.layer = layer;
        foreach (Transform child in go.transform) SetLayerRecursively(child.gameObject, layer);
    }

    /// <summary>
    /// Place a modelled prefab for every generated room whose footprint matches one, plug the openings
    /// no corridor connects to, and record the doorway crossings the NavMeshLink pass needs.
    ///
    /// Rooms with no matching prefab are simply left out of _kitCells, so the per-cell loop greyboxes
    /// them as before -- mixed room sizes degrade gracefully instead of leaving holes.
    /// </summary>
    private void BuildKitRooms()
    {
        if (roomPieces == null || roomPieces.Length == 0) return;

        for (int i = 0; i < _rooms.Count; i++)
        {
            var rect = _rooms[i];

            // Same lookup ComputeDoors used, so the doors that were routed to and the room that gets
            // built can never disagree about which prefab this footprint is.
            var kitAsset = RoomKitFor(rect);
            if (kitAsset == null) continue;   // no prefab this size -> greybox handles it
            GameObject prefab = kitAsset.gameObject;

            // Room centre in _root-local space. RectInt is in cells; CellLocal centres a single cell, so
            // the room centre is the same expression with the room's half-extent instead of 0.5.
            Vector3 centre = new Vector3(
                (rect.x + rect.width * 0.5f) * cellSize - gridSize.x * cellSize * 0.5f, 0f,
                (rect.y + rect.height * 0.5f) * cellSize - gridSize.y * cellSize * 0.5f);

            var room = Instantiate(prefab, _root);
            room.transform.localPosition = centre;
            room.transform.localRotation = Quaternion.identity;
            SetLayerRecursively(room, gameObject.layer);

            for (int x = rect.xMin; x < rect.xMax; x++)
                for (int y = rect.yMin; y < rect.yMax; y++)
                    _kitCells.Add(new Vector2Int(x, y));

            RecordRoomDoorways(rect);

            // Read the component off the INSTANCE, not the prefab asset. The prefab's opening Transforms
            // live in asset space -- their .position is the authored offset, not where the room actually
            // got placed -- so plugging from the prefab's anchors puts every plug near the world origin
            // instead of on the room.
            PlugUnusedOpenings(rect, room, room.GetComponent<DungeonKitPiece>());
        }
    }

    /// <summary>
    /// The greybox path records a doorway crossing inside BuildRoomWalls. A prefab room skips that, but
    /// the NavMeshLinks still have to be dropped or the runtime bake won't stitch the narrow doorway
    /// seams and the room becomes unreachable. So walk the perimeter and record the same crossings.
    /// </summary>
    private void RecordRoomDoorways(RectInt rect)
    {
        for (int x = rect.xMin; x < rect.xMax; x++)
            for (int y = rect.yMin; y < rect.yMax; y++)
            {
                if (x != rect.xMin && x != rect.xMax - 1 && y != rect.yMin && y != rect.yMax - 1) continue;
                Vector3 c = CellLocal(x, y);
                foreach (var d in Dirs)
                {
                    var nb = new Vector2Int(x + d.x, y + d.y);
                    if (ClassifyEdge(x, y, nb) != EdgeKind.Doorway) continue;
                    Vector3 mid = new Vector3(c.x + d.x * cellSize * 0.5f, 0f, c.z + d.y * cellSize * 0.5f);
                    _doorways.Add((mid, d.x != 0));
                }
            }
    }

    /// <summary>
    /// The room prefab is modelled with an opening on every side. Any side no corridor actually connects
    /// to has to be filled or the room opens into the void. Fills with plugPrefab when one is assigned,
    /// otherwise a greybox box -- so the layout is walkable and sealed before the plug art exists.
    /// </summary>
    private void PlugUnusedOpenings(RectInt rect, GameObject room, DungeonKitPiece kit)
    {
        if (kit == null || kit.openings == null) return;

        int plugged = 0, total = 0;

        foreach (var anchor in kit.openings)
        {
            total++;
            if (anchor == null) continue;
            Vector2Int side = kit.SideOf(anchor);

            // Resolve via the SAME CellOffsetOf that ComputeDoors used to route corridors here. Using a
            // different calculation in the two places is how you end up plugging the very opening a
            // corridor was routed to -- they must agree by construction, not by coincidence.
            Vector2Int off = kit.CellOffsetOf(anchor, cellSize);
            var cell = new Vector2Int(
                Mathf.Clamp(rect.xMin + off.x, rect.xMin, rect.xMax - 1),
                Mathf.Clamp(rect.yMin + off.y, rect.yMin, rect.yMax - 1));

            var outward = new Vector2Int(cell.x + side.x, cell.y + side.y);
            if (ClassifyEdge(cell.x, cell.y, outward) == EdgeKind.Doorway) continue;   // corridor uses it

            plugged++;

            if (kit.plugPrefab != null)
            {
                var plug = Instantiate(kit.plugPrefab, room.transform);
                plug.transform.localPosition = anchor.localPosition;
                plug.transform.localRotation = anchor.localRotation;
                SetLayerRecursively(plug, gameObject.layer);
                continue;
            }

            // Temporary greybox plug, sized from the declared opening and thickened to the wall depth.
            // The anchor marks the hole's true 3D centre, so the plug goes exactly there -- including its
            // HEIGHT. Deriving the height from openingSize instead would assume every hole starts at the
            // floor, which breaks the moment you model a window, a vent, or a raised opening.
            Vector3 local = anchor.position - _root.position;   // _root has identity rotation
            bool alongZ = side.x != 0;                          // opening faces +/-X -> wall runs along Z
            Vector3 size = alongZ
                ? new Vector3(wallThickness, kit.openingSize.y, kit.openingSize.x)
                : new Vector3(kit.openingSize.x, kit.openingSize.y, wallThickness);
            Box("RoomPlug", local, size, walkable: false);
        }

        // A room with every opening plugged is WALLED IN -- unreachable by construction, and the single
        // most likely reason the connectivity guard rerolls. Worth naming loudly, because from inside the
        // level it looks identical to a navmesh problem and you'd go hunting in the wrong place.
        if (total > 0 && plugged == total)
            Debug.LogError($"[Dungeon] Room at cells {rect} was SEALED -- all {total} openings plugged, so " +
                           $"nothing can enter it. Either no corridor reached this room, or the opening " +
                           $"anchors aren't resolving to the perimeter cells the doorways are on.", this);
    }

    /// <summary>Inverse of CellLocal: which grid cell contains this _root-local position.</summary>
    private Vector2Int CellFromLocal(Vector3 local) => new Vector2Int(
        Mathf.RoundToInt((local.x + gridSize.x * cellSize * 0.5f) / cellSize - 0.5f),
        Mathf.RoundToInt((local.z + gridSize.y * cellSize * 0.5f) / cellSize - 0.5f));

    // ---- 4b) Lighting: sparse cold wall lamps, the CONCRETE-biome profile. (Grate shafts moved to the
    //         Blender kit -- authored as a ceiling module with its own light, per the modular plan.)
    //         Placed deterministically from the seed (its own RNG stream, so it never perturbs the
    //         objective/exit placement) so every client builds identical lighting locally -- no
    //         networking, exactly like the geometry. Parented under _root, so a regen clears them too. ----
    private void PlaceLights(int seed)
    {
        var rng = new System.Random(seed ^ 0x5F37);   // independent of the main _rng stream

        // Wall lamps: any floor-cell edge facing Empty/off-grid is an exterior wall face -> a candidate.
        for (int x = 0; x < gridSize.x; x++)
            for (int y = 0; y < gridSize.y; y++)
            {
                if (_grid[x, y] == Cell.Empty) continue;

                // Skip cells covered by a modelled ROOM prefab. Those rooms carry their own authored
                // lighting -- fixtures placed with the geometry, per the "the light belongs to the
                // piece" principle -- so scattering lamps into them would double up and wreck a
                // deliberately composed space.
                //
                // Only room cells are in _kitCells, so kit CORRIDORS still get scattered lamps. That's
                // intended for now: corridor pieces have no baked lighting yet. When they do, they'll
                // want excluding too.
                if (_kitCells.Contains(new Vector2Int(x, y))) continue;

                bool isRoom = _grid[x, y] == Cell.Room;
                // Rooms wall at the cell edge; corridors wall at the inner face of their narrow strip.
                float faceOffset = isRoom
                    ? cellSize * 0.5f
                    : Mathf.Min(corridorWidth, cellSize - 0.2f) * 0.5f;
                float mount = Mathf.Min(wallLampHeight, CellHeight(x, y) - 0.3f);
                Vector3 c = CellLocal(x, y);

                foreach (var d in Dirs)
                {
                    var nb = new Vector2Int(x + d.x, y + d.y);
                    bool wall = !InBounds(nb) || _grid[nb.x, nb.y] == Cell.Empty;
                    if (!wall) continue;
                    if (rng.NextDouble() >= wallLampChance) continue;

                    Vector3 pos = c + new Vector3(d.x, 0f, d.y) * (faceOffset - 0.15f) + Vector3.up * mount;
                    Vector3 inward = new Vector3(-d.x, 0f, -d.y);
                    var rot = Quaternion.LookRotation((inward + Vector3.down * 0.5f).normalized, Vector3.up);
                    PlaceLamp(wallLampPrefab, pos, rot, flicker: rng.NextDouble() < lampFlickerChance);
                }
            }
        // NOTE: ceiling grate shafts were removed here -- they'll be authored as a kit piece (grate
        // ceiling module + its light) in the Blender stage, per the modular-lighting plan.
    }

    // Instantiate a fixture prefab if one is assigned, else build a bare light from the profile knobs.
    // Everything is parented under _root (local-deterministic, cleared on regen with the rest).
    private void PlaceLamp(GameObject prefab, Vector3 localPos, Quaternion localRot, bool flicker)
    {
        if (prefab != null)
        {
            var go = Instantiate(prefab, _root);
            go.transform.localPosition = localPos;
            go.transform.localRotation = localRot;
            return;   // prefabs bring their own flicker via flickering VARIANTS, not this flag
        }

        var lgo = new GameObject("WallLamp");
        lgo.transform.SetParent(_root, false);
        lgo.transform.localPosition = localPos;
        lgo.transform.localRotation = localRot;
        lgo.layer = gameObject.layer;

        var l = lgo.AddComponent<Light>();
        l.type = LightType.Point;
        l.color = lampColor;
        l.range = lampRange;
        l.intensity = lampIntensity;
        l.shadows = LightShadows.None;   // sparse lights, keep them cheap

        AddVolumetric(lgo);   // opt the code-built light into the volumetric fog so its glow shows
        if (flicker) lgo.AddComponent<LightFlicker>();   // failing-electrics unease (cosmetic, local)
    }

    // The volumetric-fog package's per-light component, resolved by NAME via reflection so this generator
    // needs no hard reference (or asmdef ref) to the package and still compiles if it isn't installed.
    // Same approach as LobbyGenerator. Fixture PREFABS should carry the component themselves instead.
    private static System.Type _volLightType;
    private static bool _volLightSearched;

    private void AddVolumetric(GameObject lightGo)
    {
        if (!addVolumetricToLights) return;

        if (!_volLightSearched)
        {
            _volLightSearched = true;
            foreach (var asm in System.AppDomain.CurrentDomain.GetAssemblies())
            {
                System.Type[] types;
                try { types = asm.GetTypes(); }
                catch { continue; }
                foreach (var tt in types)
                    if (tt.Name == "VolumetricAdditionalLight") { _volLightType = tt; break; }
                if (_volLightType != null) break;
            }
            if (_volLightType == null)
                Debug.LogWarning("[DungeonGenerator] 'VolumetricAdditionalLight' type not found -- is the " +
                                 "volumetric fog package installed? Lamps built without fog scattering.", this);
        }

        if (_volLightType != null && lightGo.GetComponent(_volLightType) == null)
            lightGo.AddComponent(_volLightType);
    }

    // Room perimeter: a wall on every side facing empty, a framed doorway toward a corridor, open inside.
    private void BuildRoomWalls(int x, int y, Vector3 c, float h)
    {
        foreach (var d in Dirs)
        {
            var nb = new Vector2Int(x + d.x, y + d.y);
            EdgeKind kind = ClassifyEdge(x, y, nb);
            if (kind == EdgeKind.Open) continue;

            // edge centre at floor level (grid X -> world X, grid Y -> world Z)
            Vector3 mid = new Vector3(c.x + d.x * cellSize * 0.5f, 0f, c.z + d.y * cellSize * 0.5f);
            bool alongZ = d.x != 0;   // edge faces +/-X -> the wall runs along world Z
            if (kind == EdgeKind.Doorway) _doorways.Add((mid, alongZ));   // -> a NavMeshLink after baking
            WallPanel(mid, alongZ, kind == EdgeKind.Doorway ? doorWidth : 0f, h);
        }
    }

    // A corridor cell is a NARROW passage: keep a `corridorWidth` strip down the middle toward each
    // walkable neighbour and FILL the margins with solid wall. The four corners are always solid; a
    // CLOSED side also fills its central strip -> a full wall there. Neighbouring corridors share the
    // same centred width, so strips line up into continuous passages and clean plus/T/L junctions.
    private void BuildCorridorCell(int x, int y, Vector3 c, float h)
    {
        float hw = Mathf.Min(corridorWidth, cellSize - 0.2f) * 0.5f;   // half the corridor width
        float m = cellSize * 0.5f - hw;                                // margin from strip edge to cell edge
        if (m <= 0.01f) return;                                        // as wide as the cell -> nothing to wall
        float off = hw + m * 0.5f;                                     // centre of a corner/strip band
        float w = 2f * hw;                                             // the strip width

        // four corner posts -- but SKIP any corner that sits INSIDE an open block (both its orthogonal
        // neighbours and the diagonal are walkable), which would otherwise be a free-standing pillar.
        if (!CornerOpen(x, y, -1, -1)) Box("CorrWall", new Vector3(c.x - off, h * 0.5f, c.z - off), new Vector3(m, h, m), false);
        if (!CornerOpen(x, y, -1,  1)) Box("CorrWall", new Vector3(c.x - off, h * 0.5f, c.z + off), new Vector3(m, h, m), false);
        if (!CornerOpen(x, y,  1, -1)) Box("CorrWall", new Vector3(c.x + off, h * 0.5f, c.z - off), new Vector3(m, h, m), false);
        if (!CornerOpen(x, y,  1,  1)) Box("CorrWall", new Vector3(c.x + off, h * 0.5f, c.z + off), new Vector3(m, h, m), false);

        // each CLOSED side fills its central strip -> with the corners, a full wall on that side
        if (!OpenDir(x, y, 0, 1))  Box("CorrWall", new Vector3(c.x, h * 0.5f, c.z + off), new Vector3(w, h, m), false);
        if (!OpenDir(x, y, 0, -1)) Box("CorrWall", new Vector3(c.x, h * 0.5f, c.z - off), new Vector3(w, h, m), false);
        if (!OpenDir(x, y, 1, 0))  Box("CorrWall", new Vector3(c.x + off, h * 0.5f, c.z), new Vector3(m, h, w), false);
        if (!OpenDir(x, y, -1, 0)) Box("CorrWall", new Vector3(c.x - off, h * 0.5f, c.z), new Vector3(m, h, w), false);
    }

    // True if the neighbour in grid direction (dx,dy) is walkable, so the corridor's strip exits there.
    private bool OpenDir(int x, int y, int dx, int dy)
    {
        var nb = new Vector2Int(x + dx, y + dy);
        return InBounds(nb) && _grid[nb.x, nb.y] != Cell.Empty;
    }

    // A corner is "open" (its post would be a free-standing pillar) when both orthogonal neighbours
    // AND the diagonal between them are walkable -- i.e. the corner is the interior of an open block.
    private bool CornerOpen(int x, int y, int dx, int dy) =>
        OpenDir(x, y, dx, 0) && OpenDir(x, y, 0, dy) && OpenDir(x, y, dx, dy);

    // Room cells rise to roomCeilingHeight, corridor cells to corridorCeilingHeight -- the step at
    // each doorway is what makes a room read as a grander space than the passage feeding it.
    private float CellHeight(int x, int y) =>
        _grid[x, y] == Cell.Room ? roomCeilingHeight : corridorCeilingHeight;

    private enum EdgeKind { Open, Solid, Doorway }

    // Solid facing empty; a framed doorway where a room meets a corridor, or where two DIFFERENT
    // touching rooms have a designated direct doorway; a plain wall between touching rooms otherwise;
    // open inside a room, between corridors, and on the non-room side of a doorway. (Both touching
    // rooms build the same edge, but the two panels are coincident, so it reads as one wall/doorway.)
    private EdgeKind ClassifyEdge(int x, int y, Vector2Int nb)
    {
        if (!InBounds(nb) || _grid[nb.x, nb.y] == Cell.Empty) return EdgeKind.Solid;
        Cell me = _grid[x, y], other = _grid[nb.x, nb.y];
        if (me == Cell.Room && other == Cell.Corridor) return EdgeKind.Doorway;
        if (me == Cell.Room && other == Cell.Room)
        {
            if (_cellRoom[x, y] == _cellRoom[nb.x, nb.y]) return EdgeKind.Open;   // same room interior
            return IsDirectDoor(new Vector2Int(x, y), nb) ? EdgeKind.Doorway : EdgeKind.Solid;
        }
        return EdgeKind.Open;   // corridor-corridor, or the non-room side of a room->corridor doorway
    }

    // A wall panel on a cell edge. gap<=0 -> solid; gap>0 -> two jambs + a lintel leaving a
    // gap x doorHeight opening (a framed doorway). alongZ = the wall runs along world Z.
    private void WallPanel(Vector3 edgeMid, bool alongZ, float gap, float h)
    {
        float t = wallThickness;
        if (gap <= 0f)
        {
            Vector3 solid = alongZ ? new Vector3(t, h, cellSize) : new Vector3(cellSize, h, t);
            Box("Wall", new Vector3(edgeMid.x, h * 0.5f, edgeMid.z), solid, walkable: false);
            return;
        }

        float g = Mathf.Min(gap, cellSize - 0.2f);        // keep a sliver of jamb each side
        float dh = Mathf.Min(doorHeight, h - 0.1f);
        float segLen = (cellSize - g) * 0.5f;             // each jamb's length
        float segOff = (cellSize + g) * 0.25f;            // jamb centre offset along the run
        float lintelH = h - dh;

        if (alongZ)   // wall runs along Z, thickness along X
        {
            if (segLen > 0.01f)
            {
                Box("Jamb", new Vector3(edgeMid.x, h * 0.5f, edgeMid.z - segOff), new Vector3(t, h, segLen), false);
                Box("Jamb", new Vector3(edgeMid.x, h * 0.5f, edgeMid.z + segOff), new Vector3(t, h, segLen), false);
            }
            if (lintelH > 0.01f)
                Box("Lintel", new Vector3(edgeMid.x, dh + lintelH * 0.5f, edgeMid.z), new Vector3(t, lintelH, g), false);
        }
        else          // wall runs along X, thickness along Z
        {
            if (segLen > 0.01f)
            {
                Box("Jamb", new Vector3(edgeMid.x - segOff, h * 0.5f, edgeMid.z), new Vector3(segLen, h, t), false);
                Box("Jamb", new Vector3(edgeMid.x + segOff, h * 0.5f, edgeMid.z), new Vector3(segLen, h, t), false);
            }
            if (lintelH > 0.01f)
                Box("Lintel", new Vector3(edgeMid.x, dh + lintelH * 0.5f, edgeMid.z), new Vector3(g, lintelH, t), false);
        }
    }

    // walkable=true -> floor (contributes NavMesh). walkable=false -> wall/ceiling: a Not-Walkable
    // obstacle that CARVES the NavMesh but never gets NavMesh baked on top of it.
    private void Box(string name, Vector3 localPos, Vector3 size, bool walkable)
    {
        var go = GameObject.CreatePrimitive(PrimitiveType.Cube);   // MeshRenderer + BoxCollider
        go.name = name;
        go.transform.SetParent(_root, false);
        go.transform.localPosition = localPos;
        go.transform.localScale = size;
        go.layer = gameObject.layer;   // put the DungeonGenerator object on your environment layer
        if (greyboxMaterial != null) go.GetComponent<MeshRenderer>().sharedMaterial = greyboxMaterial;
        if (!walkable)
        {
            var mod = go.AddComponent<NavMeshModifier>();
            mod.overrideArea = true;
            mod.area = 1;   // 1 = built-in "Not Walkable"
        }
    }

    // ---- 5) Scatter spawns, cluster the objective centrally, push exits out -- all spread apart ----
    private void PlaceSpecialContent()
    {
        _specialPoints.Clear();
        if (_rooms.Count == 0) return;
        var used = new HashSet<int>();

        // OBJECTIVE: central band, spread apart. Each trigger gets a deterministic Id (0..n-1) so an
        // owner-side "I cleared it" report means the same thing on every client.
        var objRooms = PickSpread(CentralBand(), objectiveCount);
        for (int k = 0; k < objRooms.Count; k++)
        {
            var go = SpawnAt(objectivePrefab, objRooms[k]);
            var trig = go != null ? go.GetComponent<ObjectiveTrigger>() : null;
            if (trig != null) trig.Id = k;
            used.Add(objRooms[k]);
        }

        // EXITS: farthest from the objective, spread apart. Each single-use exit gets its own Id.
        var exitRooms = PickSpread(RoomsSortedFarFrom(objRooms, used), exitCount);
        for (int k = 0; k < exitRooms.Count; k++)
        {
            var go = SpawnAt(exitPrefab, exitRooms[k]);
            var exit = go != null ? go.GetComponent<DungeonExit>() : null;
            if (exit != null) exit.Id = k;
            used.Add(exitRooms[k]);
        }

        // SPAWNS: scattered across the rest of the map so players land SEPARATED.
        var spawnRooms = PickSpread(RoomsExcept(used), spawnCount);
        foreach (int i in spawnRooms) SpawnAt(spawnPointPrefab, i);

        // Server: how many objective triggers must be cleared to unlock the exits this run.
        if (IsServer) DungeonObjective.Instance?.ServerConfigure(objRooms.Count);
    }

    private GameObject SpawnAt(GameObject prefab, int roomIndex)
    {
        if (prefab == null) return null;
        Vector3 p = RoomCentreWorld(roomIndex);
        _specialPoints.Add(p);   // recorded so the connectivity guard can verify this point is reachable
        return Instantiate(prefab, p, Quaternion.identity, _root);
    }

    // Room indices sorted by closeness to the grid centre; keep the closer ~half (the "central band").
    private List<int> CentralBand()
    {
        Vector2 gc = new Vector2(gridSize.x * 0.5f, gridSize.y * 0.5f);
        var idx = new List<int>();
        for (int i = 0; i < _rooms.Count; i++) idx.Add(i);
        idx.Sort((a, b) =>
        {
            float da = (RoomCentreCellF(_rooms[a]) - gc).sqrMagnitude;
            float db = (RoomCentreCellF(_rooms[b]) - gc).sqrMagnitude;
            int c = da.CompareTo(db);
            return c != 0 ? c : a.CompareTo(b);   // total order -> deterministic
        });
        int band = Mathf.Clamp(Mathf.CeilToInt(idx.Count * 0.5f), Mathf.Min(objectiveCount, idx.Count), idx.Count);
        return idx.GetRange(0, band);
    }

    // Unused rooms sorted so the ones FARTHEST from any objective room come first.
    private List<int> RoomsSortedFarFrom(List<int> objRooms, HashSet<int> used)
    {
        var idx = new List<int>();
        for (int i = 0; i < _rooms.Count; i++) if (!used.Contains(i)) idx.Add(i);
        idx.Sort((a, b) =>
        {
            int c = NearestDist(b, objRooms).CompareTo(NearestDist(a, objRooms));   // descending
            return c != 0 ? c : a.CompareTo(b);
        });
        return idx;
    }

    private List<int> RoomsExcept(HashSet<int> used)
    {
        var idx = new List<int>();
        for (int i = 0; i < _rooms.Count; i++) if (!used.Contains(i)) idx.Add(i);
        return idx;
    }

    // Greedy farthest-point sampling: from `candidates` take up to k rooms that are spread far apart.
    private List<int> PickSpread(List<int> candidates, int k)
    {
        var chosen = new List<int>();
        if (candidates.Count == 0 || k <= 0) return chosen;
        chosen.Add(candidates[0]);   // deterministic seed (candidates are meaningfully pre-ordered)
        while (chosen.Count < k && chosen.Count < candidates.Count)
        {
            int best = -1; float bestMin = -1f;
            for (int ci = 0; ci < candidates.Count; ci++)
            {
                int c = candidates[ci];
                if (chosen.Contains(c)) continue;
                float md = float.MaxValue;
                for (int j = 0; j < chosen.Count; j++) md = Mathf.Min(md, RoomDist(c, chosen[j]));
                if (md > bestMin) { bestMin = md; best = c; }   // strictly greater -> first wins
            }
            if (best == -1) break;
            chosen.Add(best);
        }
        return chosen;
    }

    private float NearestDist(int room, List<int> set)
    {
        if (set.Count == 0) return 0f;
        float m = float.MaxValue;
        for (int i = 0; i < set.Count; i++) m = Mathf.Min(m, RoomDist(room, set[i]));
        return m;
    }

    private float RoomDist(int a, int b) =>
        Vector2.Distance(RoomCentreCellF(_rooms[a]), RoomCentreCellF(_rooms[b]));

    private void BakeNavMesh()
    {
        var surface = _root.gameObject.AddComponent<NavMeshSurface>();

        // BOUND THE BAKE IN Y, don't just collect everything.
        //
        // With CollectObjects.Children the bake voxelises every surface under _root -- including the TOP
        // of the ceiling, which is a large flat upward-facing plane and therefore perfectly walkable as
        // far as the voxeliser is concerned. You get a second navmesh sheet across the roof.
        //
        // The greybox path dodged this because Box() tagged ceiling cubes with a NavMeshModifier set to
        // "Not Walkable". That cannot work for the kit: a modifier applies per OBJECT, and a kit piece is
        // one mesh containing floor, walls and ceiling together.
        //
        // Volume mode clips the voxelisation to the given bounds, so a ceiling above the box is never
        // considered at all. One change here beats splitting the ceiling out of every prefab, and it
        // keeps working automatically for pieces that don't exist yet.
        float top = Mathf.Min(corridorCeilingHeight, roomCeilingHeight) - navMeshCeilingMargin;
        const float bottom = -0.5f;   // a little below floor level, to catch the floor slab itself
        surface.collectObjects = CollectObjects.Volume;
        surface.center = new Vector3(0f, (top + bottom) * 0.5f, 0f);   // local to _root
        surface.size = new Vector3(gridSize.x * cellSize, top - bottom, gridSize.y * cellSize);

        // Volume mode collects from the WHOLE SCENE within those bounds, not just this object's
        // children -- so without a layer mask the bake also swallows any lobby geometry that happens to
        // sit inside a 112m box. Restricting to this generator's own layer scopes it back to the
        // dungeon, which is why everything the generator spawns is moved onto that layer RECURSIVELY.
        surface.layerMask = 1 << gameObject.layer;
        // Force a finer voxel size than the runtime default (~0.17). The default can't represent the
        // narrow walkable strip left between a doorway's flanking jambs/posts, so it bakes the room and
        // corridor as SEPARATE navmesh islands even though a player walks through freely. Halving the
        // voxel size lets the bake carve through the seam and weld the two sides into one mesh.
        surface.overrideVoxelSize = true;
        surface.voxelSize = navMeshVoxelSize;
        surface.BuildNavMesh();
    }

    // Drop a NavMeshLink across every doorway recorded during BuildGeometry, so the two sides connect
    // even when the runtime bake refuses to stitch the narrow, wall-flanked seam. Each link's ends sit
    // at floor level a short reach into each cell -- same height, centimetres apart -- so the agent just
    // walks across it (no teleport). Server-only: only the server bakes and uses the navmesh.
    private void BuildDoorwayLinks()
    {
        float reach = cellSize * 0.3f;                 // how far each end reaches into its own cell
        float w = Mathf.Max(1f, doorWidth - 0.3f);     // link width, kept just inside the opening
        foreach (var (pos, alongZ) in _doorways)
        {
            var go = new GameObject("DoorLink");
            go.transform.SetParent(_root, false);
            go.transform.localPosition = pos;
            var link = go.AddComponent<NavMeshLink>();
            Vector3 cross = alongZ ? Vector3.right : Vector3.forward;   // direction you cross the doorway
            link.startPoint = -cross * reach;          // one end reaches back into cell A
            link.endPoint   =  cross * reach;          // the other into cell B (width spans perpendicular)
            link.width = w;
            link.bidirectional = true;
        }
    }

    // Server: one frame after baking (so the doorway links register and the previous dungeon finishes
    // destroying), check that every special point is reachable. If not, reroll the seed -- up to
    // maxGenAttempts -- so a bad bake can never silently strand the monster or an objective.
    private IEnumerator ValidateConnectivityRoutine(int seed)
    {
        yield return null;

        if (ValidateConnectivity())
        {
            _genAttempts = 0;
            yield break;
        }

        if (_genAttempts < maxGenAttempts)
        {
            _genAttempts++;
            Debug.LogWarning($"[Dungeon] seed {seed} left a special point unreachable " +
                             $"(attempt {_genAttempts}/{maxGenAttempts}) -- rerolling.", this);
            ServerGenerate();   // new seed -> full rebuild -> this routine runs again on the new layout
        }
        else
        {
            Debug.LogError("[Dungeon] No fully-connected layout after max attempts; shipping the last one.", this);
            _genAttempts = 0;
        }
    }

    // True if every placed special point is on the navmesh AND reachable from the first one. A doorway
    // link can occasionally fail to snap onto a heavily-eroded patch, which would leave an island.
    //
    // Reports WHAT failed, not just that something did. The three failure modes have completely
    // different causes and the old bare bool couldn't tell them apart:
    //   NO NAVMESH AT ALL -> nothing was collected. Layer mask, missing colliders, or meshes without
    //                        Read/Write Enabled. Nothing to do with layout.
    //   OFF-MESH POINT    -> that spot has no walkable floor under it. Usually a piece with no collider,
    //                        or a special point placed inside geometry.
    //   UNREACHABLE POINT -> on the navmesh but no path to it: an ISLAND. Almost always a doorway whose
    //                        opening doesn't line up with where the generator dropped its NavMeshLink.
    private bool ValidateConnectivity()
    {
        if (_specialPoints.Count < 2) return true;

        // Cheapest, most decisive check first: did the bake produce anything at all? Without this, a
        // completely empty navmesh looks identical to a badly-connected one and you debug the layout
        // when the real problem is that nothing was ever collected.
        var tri = NavMesh.CalculateTriangulation();
        if (tri.vertices == null || tri.vertices.Length == 0)
        {
            Debug.LogError("[Dungeon] NO NAVMESH WAS BAKED AT ALL. Layout is not the problem. Check, in " +
                           "order: (1) the DungeonGenerator's layer matches what the bake masks to, and " +
                           "kit prefabs get it recursively; (2) every kit prefab has a MeshCollider; " +
                           "(3) every kit mesh has Read/Write Enabled in its import settings.", this);
            return false;
        }

        if (!NavMesh.SamplePosition(_specialPoints[0], out var origin, 4f, NavMesh.AllAreas))
        {
            Debug.LogError($"[Dungeon] The reference special point at {_specialPoints[0]} isn't on the " +
                           $"navmesh, so nothing can be checked against it. Navmesh exists " +
                           $"({tri.vertices.Length} verts), so this spot specifically has no walkable " +
                           $"floor -- likely a piece missing its collider.", this);
            return false;
        }

        var path = new NavMeshPath();
        int offMesh = 0, unreachable = 0;
        Vector3 firstOff = Vector3.zero, firstUnreachable = Vector3.zero;

        for (int i = 1; i < _specialPoints.Count; i++)
        {
            if (!NavMesh.SamplePosition(_specialPoints[i], out var hit, 4f, NavMesh.AllAreas))
            {
                if (offMesh++ == 0) firstOff = _specialPoints[i];
                continue;
            }
            NavMesh.CalculatePath(origin.position, hit.position, NavMesh.AllAreas, path);
            if (path.status != NavMeshPathStatus.PathComplete)
            {
                if (unreachable++ == 0) firstUnreachable = _specialPoints[i];
            }
        }

        if (offMesh == 0 && unreachable == 0) return true;

        Debug.LogWarning($"[Dungeon] Connectivity failed on {offMesh + unreachable}/{_specialPoints.Count - 1} " +
                         $"points. OFF-MESH: {offMesh}" + (offMesh > 0 ? $" (first at {firstOff})" : "") +
                         $" · UNREACHABLE: {unreachable}" + (unreachable > 0 ? $" (first at {firstUnreachable})" : "") +
                         (unreachable > 0
                             ? " -- unreachable means ISLANDS: a doorway opening isn't lining up with the " +
                               "NavMeshLink the generator dropped at that cell's edge midpoint."
                             : " -- off-mesh means no walkable floor there: check colliders on that piece."),
                         this);
        return false;
    }

    // ---- small helpers: grid <-> local/world space (grid centred on the generator origin) ----
    private bool InBounds(Vector2Int c) => c.x >= 0 && c.y >= 0 && c.x < gridSize.x && c.y < gridSize.y;

    private static Vector2 RoomCentreCellF(RectInt r) => new Vector2(r.x + r.width * 0.5f, r.y + r.height * 0.5f);

    // local position of a cell centre, with the grid centred on the generator's origin.
    private Vector3 CellLocal(int x, int y) => new Vector3(
        (x + 0.5f) * cellSize - gridSize.x * cellSize * 0.5f, 0f,
        (y + 0.5f) * cellSize - gridSize.y * cellSize * 0.5f);

    private Vector3 RoomCentreWorld(int i)
    {
        var r = _rooms[i];
        Vector3 local = new Vector3(
            (r.x + r.width * 0.5f) * cellSize - gridSize.x * cellSize * 0.5f, 0f,
            (r.y + r.height * 0.5f) * cellSize - gridSize.y * cellSize * 0.5f);
        return _root.position + local;   // _root has identity rotation
    }

    /// <summary>
    /// World centre of the room NEAREST to a world position. This is how the Stalker's re-acquire
    /// resolves "the general area you're in": your current room, or the closest one if you're in a
    /// corridor. The monster heads to the right room, not your exact spot -- so you get room to have
    /// repositioned. Falls back to the given position if no rooms exist yet.
    /// </summary>
    public Vector3 NearestRoomCentre(Vector3 worldPos)
    {
        if (_rooms.Count == 0) return worldPos;

        int best = 0;
        float bestSq = float.MaxValue;
        for (int i = 0; i < _rooms.Count; i++)
        {
            float sq = (RoomCentreWorld(i) - worldPos).sqrMagnitude;
            if (sq < bestSq) { bestSq = sq; best = i; }
        }
        return RoomCentreWorld(best);
    }

    /// <summary>
    /// "Openness" of the built dungeon at a world position, for room-driven reverb: ~0.15 in a tight
    /// corridor (short slap), 0.5–1.0 across small→large rooms (longer, more cavernous tail). Returns
    /// -1 when the position isn't inside a built dungeon cell, which the caller treats as DRY (e.g. a
    /// listener up in the lobby). Pure read off the grid — NO per-room tagging or authoring needed.
    /// </summary>
    public float LocalReverbScale(Vector3 worldPos)
    {
        if (_root == null || _grid == null) return -1f;
        Vector2Int c = CellFromLocal(worldPos - _root.position);   // same world→local basis as RoomCentreWorld
        if (!InBounds(c)) return -1f;

        switch (_grid[c.x, c.y])
        {
            case Cell.Corridor:
                return 0.15f;   // tight passage: full-ish level but short decay (set by the driver)

            case Cell.Room:
                int id = _cellRoom[c.x, c.y];
                if (id < 0 || id >= _rooms.Count) return 0.5f;
                var r = _rooms[id];
                int area = r.width * r.height;
                int minA = roomCellsMin.x * roomCellsMin.y;
                int maxA = roomCellsMax.x * roomCellsMax.y;
                float t = maxA > minA ? Mathf.InverseLerp(minA, maxA, area) : 0.5f;
                return Mathf.Lerp(0.5f, 1f, t);   // rooms occupy the OPEN half; biggest room = 1

            default:
                return -1f;     // Empty cell inside the grid = not a walkable space
        }
    }
}
