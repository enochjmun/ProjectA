using UnityEngine;

/// <summary>
/// A LIVE security-camera feed on one menu monitor. The security-room menu previews the game's core
/// "watch each other on screens" motif, so a couple of monitors show the ACTUAL game spaces -- a fixed
/// angle on the lobby, a table, a corridor -- rendered locally to the screen prop. Same render-texture
/// trick as ChaseTVFeed, but this cam is PARKED at a security angle (optionally a slow roving drift)
/// instead of following the prey.
///
/// LOCAL, no netcode: the lobby/casino geometry is already in this single scene, so a second camera just
/// renders it. It only runs while the menu is up (this lives under menuRoot -> OnDisable stops it the
/// instant a session starts, so we pay nothing in-game).
///
/// PERF: live feeds are the expensive part -- each is another camera. So keep them few (1-2), render at a
/// low CRT resolution, and THROTTLE: the camera is disabled and we drive Camera.Render() by hand every
/// `renderInterval` seconds. Across a dark room at a distance the low frame-rate reads as period CCTV, not
/// a bug. The rest of the wall should be MenuStaticScreen (free) rather than more of these.
///
/// SETUP (manual): drop this on an empty placed at the security ANGLE you want to show (its transform IS
/// the camera pose; parent it under menuRoot). Assign `screenRenderer` = the monitor's screen mesh, and
/// match the material property names (unlit/emissive screen reads best -- it isn't re-graded by
/// BuckshotPost). Optional gentle sway sells a motorised cam.
/// </summary>
public class MenuMonitorFeed : MonoBehaviour
{
    [Header("Screen surface")]
    [Tooltip("Renderer of this monitor's screen mesh. Its material instance receives the live feed.")]
    [SerializeField] private Renderer screenRenderer;
    [SerializeField] private int screenMaterialIndex = 0;
    [Tooltip("Texture property on the screen material (URP Lit = _BaseMap; unlit/emissive may use " +
             "_MainTex / _EmissionMap). Must match the material.")]
    [SerializeField] private string textureProperty = "_BaseMap";
    [Tooltip("Optional emission colour property so the screen glows. Blank if the material has no emission.")]
    [SerializeField] private string emissionProperty = "_EmissionColor";
    [Tooltip("Emission tint of a live screen -- slightly cool CCTV phosphor.")]
    [SerializeField] private Color liveEmission = new Color(0.75f, 0.85f, 0.9f);

    [Header("Feed render")]
    [Tooltip("RenderTexture size. Small = a lo-fi CRT look AND cheap.")]
    [SerializeField] private Vector2Int resolution = new Vector2Int(320, 240);
    [SerializeField] private float fieldOfView = 60f;
    [Tooltip("Seconds between manual renders. ~0.1 (10 fps) reads as CCTV and cuts cost ~6x vs every frame.")]
    [SerializeField] private float renderInterval = 0.1f;
    [Tooltip("Solid clear colour behind the scene (the spaces are dark anyway).")]
    [SerializeField] private Color cameraBackground = Color.black;
    [Tooltip("Cull mask for the feed camera. Leave as Everything, or drop the menu-room layer so the cam " +
             "doesn't film the security room itself.")]
    [SerializeField] private LayerMask cullingMask = ~0;

    [Header("Optional roving-cam sway")]
    [Tooltip("Degrees the cam slowly oscillates in yaw. 0 = a dead-fixed angle.")]
    [SerializeField] private float swayYawDegrees = 0f;
    [SerializeField] private float swaySpeed = 0.15f;

    private Camera _cam;
    private RenderTexture _rt;
    private Material _screenMat;
    private int _texId, _emitId;
    private bool _hasEmission;
    private float _nextRender;
    private Quaternion _baseRot;

    private void Awake()
    {
        _rt = new RenderTexture(resolution.x, resolution.y, 16) { name = "MenuMonitorFeed_RT" };
        _rt.Create();

        // The camera sits AT this object's pose. Disabled -- we render it manually on the throttle.
        var camGo = new GameObject("MenuFeedCamera");
        camGo.transform.SetPositionAndRotation(transform.position, transform.rotation);
        camGo.transform.SetParent(transform, worldPositionStays: true);
        _cam = camGo.AddComponent<Camera>();
        _cam.targetTexture = _rt;
        _cam.fieldOfView = fieldOfView;
        _cam.clearFlags = CameraClearFlags.SolidColor;
        _cam.backgroundColor = cameraBackground;
        _cam.cullingMask = cullingMask;
        _cam.enabled = false;   // manual Render() only

        _baseRot = camGo.transform.rotation;   // WORLD rotation -> sway yaws around world up, not the tilted local axis

        if (screenRenderer != null)
        {
            int i = Mathf.Clamp(screenMaterialIndex, 0, screenRenderer.materials.Length - 1);
            _screenMat = screenRenderer.materials[i];
            _texId = Shader.PropertyToID(textureProperty);
            _hasEmission = !string.IsNullOrEmpty(emissionProperty);
            if (_hasEmission)
            {
                _emitId = Shader.PropertyToID(emissionProperty);
                _screenMat.EnableKeyword("_EMISSION");
                _screenMat.SetColor(_emitId, liveEmission);
            }
            _screenMat.SetTexture(_texId, _rt);
        }
    }

    private void OnDestroy()
    {
        if (_cam != null) Destroy(_cam.gameObject);
        if (_rt != null) { _rt.Release(); Destroy(_rt); }
    }

    private void OnDisable() => _nextRender = 0f;   // re-render immediately next time the menu opens

    private void Update()
    {
        if (_cam == null) return;
        _cam.fieldOfView = fieldOfView;   // live-tunable so you can frame it in Play mode

        if (swayYawDegrees > 0f)
        {
            float yaw = Mathf.Sin(Time.unscaledTime * swaySpeed * Mathf.PI * 2f) * swayYawDegrees;
            _cam.transform.rotation = Quaternion.AngleAxis(yaw, Vector3.up) * _baseRot;   // pan horizontally, keep the tilt
        }

        if (Time.unscaledTime < _nextRender) return;
        _nextRender = Time.unscaledTime + Mathf.Max(0.0001f, renderInterval);
        _cam.Render();
    }
}
