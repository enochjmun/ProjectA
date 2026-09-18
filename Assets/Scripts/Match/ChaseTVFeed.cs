using System.Linq;
using UnityEngine;

/// <summary>
/// The lobby TV: a live camera feed of the dungeon chase, shown on the `Screen` prop so survivors still
/// at the table can watch the prey run (the GDD's "watch the TV" paused-chase option, and diegetic
/// spectating that complements SpectatorController).
///
/// LOCAL, no netcode. The dungeon geometry and every player/monster transform are already replicated, so
/// a second camera placed in the dungeon renders the SAME scene on every client with no extra traffic --
/// each client's TV shows its own local render. It only runs during ChaseInProgress; otherwise the
/// screen is dark.
///
/// SETUP (manual): put this on (or near) the Screen prop and assign `screenRenderer` = the TV's screen
/// mesh renderer. Set the material property names to match that screen's material (a lo-fi/unlit or
/// emissive material reads best -- the feed is not re-graded by BuckshotPost, which lives on the main
/// camera). It builds its own camera + RenderTexture at runtime; nothing else to wire.
/// </summary>
public class ChaseTVFeed : MonoBehaviour
{
    [Header("Screen surface")]
    [Tooltip("Renderer of the TV's screen mesh. Its material instance gets the live RenderTexture.")]
    [SerializeField] private Renderer screenRenderer;
    [Tooltip("Which material slot on that renderer is the screen face.")]
    [SerializeField] private int screenMaterialIndex = 0;
    [Tooltip("Texture property to drive on the screen material (URP Lit = _BaseMap; an unlit/emissive " +
             "screen shader may use _MainTex or _EmissionMap). Must match the material.")]
    [SerializeField] private string textureProperty = "_BaseMap";
    [Tooltip("Optional emission colour property, set bright while live so the screen glows and dark when " +
             "off. Leave blank if the screen material has no emission.")]
    [SerializeField] private string emissionProperty = "_EmissionColor";
    [SerializeField] private Color liveEmission = new Color(0.9f, 0.95f, 1f);
    [Tooltip("Screen tint while OFF (no chase). Near-black = a dead CRT.")]
    [SerializeField] private Color offColor = new Color(0.015f, 0.015f, 0.02f);

    [Header("Feed render")]
    [Tooltip("RenderTexture size. Keep it small -- a lo-fi CRT look AND it's a second camera every frame " +
             "during the chase. 4:3 suits a period TV.")]
    [SerializeField] private Vector2Int resolution = new Vector2Int(384, 288);
    [SerializeField] private float fieldOfView = 55f;
    [Tooltip("Solid colour the feed camera clears to behind the dungeon (the dungeon is dark anyway).")]
    [SerializeField] private Color cameraBackground = Color.black;

    [Header("Chase cam framing (follows the active prey)")]
    [SerializeField] private float followDistance = 5f;
    [SerializeField] private float followHeight = 2.4f;
    [SerializeField] private float lookHeight = 1.2f;
    [Tooltip("How fast the cam eases to its framing -- low = a lazy, security-cam drift.")]
    [SerializeField] private float followLerp = 3.5f;

    private Camera _cam;
    private RenderTexture _rt;
    private Material _screenMat;      // instance (screenRenderer.materials[i])
    private int _texId, _emitId;
    private bool _hasEmission;
    private Transform _target;
    private bool _live;

    private void Awake()
    {
        _rt = new RenderTexture(resolution.x, resolution.y, 16) { name = "ChaseTVFeed_RT" };
        _rt.Create();

        // Dedicated feed camera -- no AudioListener, disabled until a chase runs so we pay nothing idle.
        var camGo = new GameObject("ChaseTVCamera");
        _cam = camGo.AddComponent<Camera>();
        _cam.targetTexture = _rt;
        _cam.fieldOfView = fieldOfView;
        _cam.clearFlags = CameraClearFlags.SolidColor;
        _cam.backgroundColor = cameraBackground;
        _cam.enabled = false;

        if (screenRenderer != null)
        {
            int i = Mathf.Clamp(screenMaterialIndex, 0, screenRenderer.materials.Length - 1);
            _screenMat = screenRenderer.materials[i];   // .materials = per-renderer instances (safe to edit)
            _texId = Shader.PropertyToID(textureProperty);
            _hasEmission = !string.IsNullOrEmpty(emissionProperty);
            if (_hasEmission)
            {
                _emitId = Shader.PropertyToID(emissionProperty);
                _screenMat.EnableKeyword("_EMISSION");
            }
            _screenMat.SetTexture(_texId, _rt);
        }

        SetLive(false);
    }

    private void OnDestroy()
    {
        if (_cam != null) Destroy(_cam.gameObject);
        if (_rt != null) { _rt.Release(); Destroy(_rt); }
    }

    private void LateUpdate()
    {
        var mc = MatchController.Instance;
        bool chase = mc != null && mc.CurrentPhase == MatchController.Phase.ChaseInProgress;

        if (chase) AcquireTarget(mc);
        else _target = null;

        bool live = chase && _target != null;
        if (live != _live) SetLive(live);
        if (live) FrameTarget();
    }

    // Keep the current prey if it's still being chased; otherwise pick the first live one.
    private void AcquireTarget(MatchController mc)
    {
        if (_target != null)
        {
            var ps = _target.GetComponent<PlayerState>();
            if (ps != null && mc.FallenPlayers.Contains(ps) && !mc.HasChaseOutcome(ps))
                return;   // still valid
        }

        _target = null;
        foreach (var p in mc.FallenPlayers)
        {
            if (p == null || mc.HasChaseOutcome(p)) continue;
            _target = p.transform;
            return;
        }
    }

    private void FrameTarget()
    {
        // Behind + above the prey, looking at them -- a chase cam. Eased so it drifts rather than snaps.
        Vector3 behind = _target.position - _target.forward * followDistance + Vector3.up * followHeight;
        _cam.transform.position = Vector3.Lerp(_cam.transform.position, behind, followLerp * Time.deltaTime);

        Vector3 look = _target.position + Vector3.up * lookHeight;
        Quaternion want = Quaternion.LookRotation(look - _cam.transform.position, Vector3.up);
        _cam.transform.rotation = Quaternion.Slerp(_cam.transform.rotation, want, followLerp * Time.deltaTime);
    }

    private void SetLive(bool live)
    {
        _live = live;
        if (_cam != null) _cam.enabled = live;

        if (_screenMat == null) return;
        if (live)
        {
            // Snap the cam onto the target immediately so the feed doesn't open mid-pan from the origin.
            if (_target != null)
            {
                _cam.transform.position = _target.position - _target.forward * followDistance + Vector3.up * followHeight;
                _cam.transform.LookAt(_target.position + Vector3.up * lookHeight);
            }
            if (_hasEmission) _screenMat.SetColor(_emitId, liveEmission);
        }
        else
        {
            // Dead CRT: keep the (now-static) RT but kill the glow, or tint dark if there's no emission.
            if (_hasEmission) _screenMat.SetColor(_emitId, offColor);
            else _screenMat.SetColor(_texId == 0 ? _emitId : _texId, offColor);
        }
    }
}
