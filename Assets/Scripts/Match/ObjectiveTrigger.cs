using UnityEngine;

/// <summary>
/// A trigger volume the generator drops into an objective room. The OWNER of a player detects
/// entering it (via ChaseEscapeReporter) and reports its Id to the server, which marks that objective
/// cleared in DungeonObjective. When ALL objective triggers are cleared, the exits unlock for everyone
/// (team unlock -- one player braving the centre frees the whole team).
///
/// Also builds a purely-visual glowing MARKER at runtime so the (otherwise invisible) trigger is
/// something players can see and walk toward -- amber while pending, green once cleared. Placeholder,
/// generated in code so it needs no art.
///
/// The Id is assigned deterministically by the generator (0..objectiveCount-1) and is identical on
/// every client. Give it a trigger collider (Reset() sets isTrigger when you add the component).
/// </summary>
[RequireComponent(typeof(Collider))]
public class ObjectiveTrigger : MonoBehaviour
{
    [Tooltip("Set by the generator (0..objectiveCount-1). Unique within one dungeon.")]
    public int Id;

    [Header("Placeholder marker")]
    [Tooltip("Height above the trigger that the glowing marker floats.")]
    [SerializeField] private float markerHeight = 1.6f;
    [SerializeField] private float markerScale = 0.6f;
    [SerializeField] private Color pendingColor = new Color(1f, 0.55f, 0.1f);   // amber = not done
    [SerializeField] private Color clearedColor = new Color(0.2f, 1f, 0.35f);   // green = done

    private Material _markerMat;
    private Light _markerLight;
    private bool _wasCleared;

    private void Reset()
    {
        var col = GetComponent<Collider>();
        if (col != null) col.isTrigger = true;
    }

    private void Start()
    {
        BuildMarker();
        ApplyVisual(false);
    }

    // A glowing sphere + point light, built in code. Assigns an explicit URP/Lit material -- a runtime
    // CreatePrimitive would otherwise get the built-in Default-Material, which renders pink under URP.
    private void BuildMarker()
    {
        var marker = GameObject.CreatePrimitive(PrimitiveType.Sphere);
        marker.name = "ObjectiveMarker";
        var col = marker.GetComponent<Collider>();
        if (col != null) Destroy(col);   // visual only -- must not block the player or the trigger
        marker.transform.SetParent(transform, false);
        marker.transform.localPosition = Vector3.up * markerHeight;
        marker.transform.localScale = Vector3.one * markerScale;

        _markerMat = new Material(Shader.Find("Universal Render Pipeline/Lit"));
        _markerMat.EnableKeyword("_EMISSION");
        marker.GetComponent<Renderer>().material = _markerMat;

        var lightGo = new GameObject("ObjectiveLight");
        lightGo.transform.SetParent(transform, false);
        lightGo.transform.localPosition = Vector3.up * markerHeight;
        _markerLight = lightGo.AddComponent<Light>();
        _markerLight.type = LightType.Point;
        _markerLight.range = 9f;
    }

    // Poll the replicated cleared-state and swap the marker amber<->green when it changes. Runs on every
    // client (the trigger is a local, deterministic object built by the generator on all peers).
    private void Update()
    {
        var obj = DungeonObjective.Instance;
        bool cleared = obj != null && obj.IsCleared(Id);
        if (cleared != _wasCleared)
        {
            _wasCleared = cleared;
            ApplyVisual(cleared);
        }
    }

    private void ApplyVisual(bool cleared)
    {
        Color col = cleared ? clearedColor : pendingColor;
        if (_markerMat != null)
        {
            _markerMat.color = col;
            _markerMat.SetColor("_EmissionColor", col * (cleared ? 2f : 4f));   // HDR so bloom catches it
        }
        if (_markerLight != null)
        {
            _markerLight.color = col;
            _markerLight.intensity = cleared ? 1.2f : 3f;
        }
    }

    private void OnDestroy()
    {
        if (_markerMat != null) Destroy(_markerMat);
    }

    private void OnDrawGizmos()
    {
        Gizmos.color = new Color(1f, 0.85f, 0.2f, 0.35f);
        var col = GetComponent<Collider>();
        if (col is BoxCollider box) Gizmos.DrawCube(transform.TransformPoint(box.center), box.size);
        else Gizmos.DrawWireSphere(transform.position, 1f);
    }
}
