using UnityEngine;

/// <summary>
/// Marks a trigger volume as the dungeon escape. A fallen player who walks into it escapes the chase
/// (detected by ChaseEscapeReporter on the player, which tells the server). Give it a trigger collider --
/// Reset() sets isTrigger automatically when you add the component.
///
/// Also builds a purely-visual glowing MARKER: dim RED while the exit is locked (objective not done),
/// switching to bright CYAN once DungeonObjective.IsComplete is true -- so the exits visibly "come alive"
/// the moment the team finishes the objective. Placeholder, generated in code (no art), a different
/// colour family from the amber/green objective markers so exits read distinctly.
/// </summary>
[RequireComponent(typeof(Collider))]
public class DungeonExit : MonoBehaviour
{
    [Tooltip("Set by the generator (0..exitCount-1). Each exit is single-use, tracked by this Id.")]
    public int Id;

    [Header("Placeholder marker")]
    [SerializeField] private float markerHeight = 1.4f;
    [SerializeField] private Vector3 markerSize = new Vector3(0.35f, 1.4f, 0.35f);   // a pillar/beam
    [SerializeField] private Color lockedColor = new Color(0.9f, 0.15f, 0.1f);        // red = locked
    [SerializeField] private Color openColor = new Color(0.2f, 0.9f, 1f);            // cyan = go!

    private Material _markerMat;
    private Light _markerLight;
    private bool _wasOpen;

    private void Reset()
    {
        var col = GetComponent<Collider>();
        if (col != null)
            col.isTrigger = true;
    }

    private void Start()
    {
        BuildMarker();
        ApplyVisual(false);
    }

    // A glowing pillar + point light, built in code with an explicit URP/Lit material (a runtime
    // CreatePrimitive would otherwise get the built-in Default-Material, which renders pink under URP).
    private void BuildMarker()
    {
        var marker = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
        marker.name = "ExitMarker";
        var col = marker.GetComponent<Collider>();
        if (col != null) Destroy(col);   // visual only
        marker.transform.SetParent(transform, false);
        marker.transform.localPosition = Vector3.up * markerHeight;
        marker.transform.localScale = markerSize;

        _markerMat = new Material(Shader.Find("Universal Render Pipeline/Lit"));
        _markerMat.EnableKeyword("_EMISSION");
        marker.GetComponent<Renderer>().material = _markerMat;

        var lightGo = new GameObject("ExitLight");
        lightGo.transform.SetParent(transform, false);
        lightGo.transform.localPosition = Vector3.up * markerHeight;
        _markerLight = lightGo.AddComponent<Light>();
        _markerLight.type = LightType.Point;
        _markerLight.range = 10f;
    }

    // Poll the replicated objective-complete flag and swap red<->cyan when the exits unlock. Runs on
    // every client (the exit is a local, deterministic object the generator builds on all peers).
    private void Update()
    {
        var obj = DungeonObjective.Instance;
        bool open = obj != null && obj.IsComplete.Value;
        if (open != _wasOpen)
        {
            _wasOpen = open;
            ApplyVisual(open);
        }
    }

    private void ApplyVisual(bool open)
    {
        Color col = open ? openColor : lockedColor;
        if (_markerMat != null)
        {
            _markerMat.color = col;
            _markerMat.SetColor("_EmissionColor", col * (open ? 5f : 2f));   // brighter when open
        }
        if (_markerLight != null)
        {
            _markerLight.color = col;
            _markerLight.intensity = open ? 4f : 1.2f;
        }
    }

    private void OnDestroy()
    {
        if (_markerMat != null) Destroy(_markerMat);
    }

    private void OnDrawGizmos()
    {
        Gizmos.color = new Color(0.3f, 1f, 0.4f, 0.35f);
        var col = GetComponent<Collider>();
        if (col is BoxCollider box)
            Gizmos.DrawCube(transform.TransformPoint(box.center), box.size);
        else
            Gizmos.DrawWireSphere(transform.position, 1f);
    }
}
