using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

/// <summary>
/// The security-room menu's camera director. The menu isn't a screen-space UI -- it's a parked camera
/// looking at a wall of CCTV monitors, and each menu "page" (Main, Options, Credits, ...) lives on its
/// OWN monitor in the room. Selecting a sub-menu doesn't swap a canvas; it PANS this camera to the view
/// parked in front of that monitor. This component owns those named view targets and the pan between them.
///
/// Diegetic, LOCAL, no netcode -- it only runs while the menu is up (it sits under menuRoot, so it's
/// disabled the instant a session starts; see MainMenuController). Buttons on the world-space monitor
/// canvases call GoTo("Options") / Back(); MainMenuController calls SnapHome() when the menu re-appears
/// after a disconnect so you never glide in from a stale angle.
///
/// PAN, not cut: the brief wants a cinematic security-cam glide, but sub-menus open rarely so a quick eased
/// move (duration-based, distance-independent) stays snappy rather than irritating. Set panDuration = 0 for
/// an instant cut if a view ever needs to be fast.
///
/// SETUP (manual):
///   - Put this on the menu camera (or its parent under menuRoot). Assign `menuCamera`.
///   - For each monitor that carries a menu page, drop an empty at the spot the camera should sit
///     (position + rotation framing that screen) and add it as a View: give it a Name, the Point (that
///     empty), and optionally the Panel (the page's canvas/root GameObject) so only the current page is
///     active/interactive.
///   - Name the main view (default "Main") so SnapHome() / Back() know where home is.
///   - Wire buttons: Options button -> GoTo (string "Options"); Back button -> Back().
/// </summary>
public class MenuCameraDirector : MonoBehaviour
{
    [System.Serializable]
    public class View
    {
        [Tooltip("Identifier a button passes to GoTo(), e.g. \"Main\", \"Options\", \"Credits\".")]
        public string name;
        [Tooltip("Empty transform marking where the menu camera sits + how it's rotated to frame this " +
                 "monitor.")]
        public Transform point;
        [Tooltip("Optional: this page's UI root. Enabled when this view is current, disabled otherwise, so " +
                 "only the monitor you're looking at is interactive. Leave null if you manage panels " +
                 "elsewhere.")]
        public GameObject panel;
        [Tooltip("DoF focus distance for THIS pose. Each station's screen is a different distance from " +
                 "the lens, so focus must travel with the view or the side screens fall out of focus. " +
                 "Set to the camera->screen distance of this pose. Ignored if no DoF Volume is assigned.")]
        public float focusDistance = 2.5f;
    }

    [Header("Camera")]
    [Tooltip("The parked menu camera this director moves. Usually a child of menuRoot.")]
    [SerializeField] private Camera menuCamera;

    [Header("Views (one per menu monitor)")]
    [SerializeField] private List<View> views = new List<View>();
    [Tooltip("Which view is home / the first shown. Must match one View's Name.")]
    [SerializeField] private string homeViewName = "Main";

    [Header("Pan")]
    [Tooltip("Seconds for the eased glide between two views, regardless of distance. Small = snappy. " +
             "0 = instant cut.")]
    [SerializeField] private float panDuration = 0.35f;

    [Header("Depth of Field (optional)")]
    [Tooltip("Volume holding the Depth Of Field override. If assigned, focus distance is driven per-View " +
             "and lerped with the pan, so each station stays in focus. Preserves your tuned DoF LOOK -- " +
             "Bokeh: only focusDistance moves (aperture/focalLength untouched); Gaussian: the band slides " +
             "but keeps its width. Leave null to leave DoF alone.")]
    [SerializeField] private Volume dofVolume;
    private DepthOfField _dof;
    private float _gaussWidth;   // Gaussian band width (End - Start), captured + preserved

    // Focus lerp state, mirrors the pose lerp.
    private float _fromFocus, _targetFocus;

    // Back() unwinds the navigation you actually walked (Main -> Options -> Sub -> Back -> Options ...),
    // rather than always jumping home, so deeper menus behave correctly.
    private readonly Stack<int> _history = new Stack<int>();
    private int _current = -1;

    // Active pan state: eases the camera from a captured start pose to the target view's pose over panDuration.
    private bool _panning;
    private float _panT;
    private Vector3 _fromPos;
    private Quaternion _fromRot;
    private Transform _target;

    private void Awake()
    {
        if (menuCamera == null) menuCamera = GetComponentInChildren<Camera>(true);

        // Read `.profile` (a runtime INSTANCE), not sharedProfile, so writes don't persist to the asset.
        if (dofVolume != null && dofVolume.profile != null && dofVolume.profile.TryGet(out _dof))
            _gaussWidth = Mathf.Max(0f, _dof.gaussianEnd.value - _dof.gaussianStart.value);
    }

    private void OnEnable()
    {
        // Every time the menu comes up, start clean at home with no glide.
        SnapHome();
    }

    /// <summary>Jump instantly to the home view and clear history. Called on menu (re)appearance.</summary>
    public void SnapHome()
    {
        _history.Clear();
        int home = IndexOf(homeViewName);
        if (home < 0) home = views.Count > 0 ? 0 : -1;
        if (home < 0) return;
        SetActiveView(home);
        ApplyPose(views[home].point, snap: true);
        SetFocus(views[home].focusDistance);   // land home already focused, no pull-in on appear
        _panning = false;
    }

    /// <summary>Pan to a named view and remember where we came from (for Back()).</summary>
    public void GoTo(string viewName) => GoTo(IndexOf(viewName));

    /// <summary>Pan to a view by index. Ignores a request to go where we already are.</summary>
    public void GoTo(int index)
    {
        if (index < 0 || index >= views.Count)
        {
            Debug.LogWarning($"[MenuDirector] GoTo: no view '{index}'.", this);
            return;
        }
        if (index == _current) return;

        if (_current >= 0) _history.Push(_current);
        BeginPan(index);
    }

    /// <summary>Step back to the previous view. At home (empty history) it does nothing.</summary>
    public void Back()
    {
        if (_history.Count == 0) return;
        BeginPan(_history.Pop());
    }

    private void BeginPan(int index)
    {
        SetActiveView(index);   // show the destination page immediately so it's there as we arrive

        if (menuCamera == null || views[index].point == null) return;

        if (panDuration <= 0f)
        {
            ApplyPose(views[index].point, snap: true);
            SetFocus(views[index].focusDistance);
            _panning = false;
            return;
        }

        _fromPos = menuCamera.transform.position;
        _fromRot = menuCamera.transform.rotation;
        _target = views[index].point;
        _fromFocus = ReadFocus();
        _targetFocus = views[index].focusDistance;
        _panT = 0f;
        _panning = true;
    }

    private void Update()
    {
        if (!_panning || _target == null) return;

        _panT += Time.unscaledDeltaTime / panDuration;   // unscaled: menus shouldn't care about timeScale
        float e = Mathf.SmoothStep(0f, 1f, Mathf.Clamp01(_panT));

        menuCamera.transform.position = Vector3.Lerp(_fromPos, _target.position, e);
        menuCamera.transform.rotation = Quaternion.Slerp(_fromRot, _target.rotation, e);
        SetFocus(Mathf.Lerp(_fromFocus, _targetFocus, e));   // focus pulls onto the new screen as we arrive

        if (_panT >= 1f) _panning = false;
    }

    // --- DoF focus, preserving your tuned look (see dofVolume tooltip) ---

    /// <summary>Current DoF focus distance. Public so cinematics (e.g. MarkerStampSequence) can lerp from it.</summary>
    public float ReadFocus()
    {
        if (_dof == null) return 0f;
        return _dof.mode.value == DepthOfFieldMode.Bokeh
            ? _dof.focusDistance.value
            : _dof.gaussianStart.value;
    }

    /// <summary>Set the DoF focus distance, preserving your tuned look (Bokeh: focusDistance only; Gaussian:
    /// slides the band, keeps its width). Public so a non-View shot can focus itself.</summary>
    public void SetFocus(float d)
    {
        if (_dof == null) return;
        if (_dof.mode.value == DepthOfFieldMode.Bokeh)
        {
            _dof.focusDistance.value = d;                 // aperture + focalLength untouched
        }
        else
        {
            _dof.gaussianStart.value = d;                 // slide the band, keep its width
            _dof.gaussianEnd.value   = d + _gaussWidth;
        }
    }

    // Enable the current view's panel, disable the rest, and record the index.
    private void SetActiveView(int index)
    {
        _current = index;
        for (int i = 0; i < views.Count; i++)
        {
            var p = views[i].panel;
            if (p != null) p.SetActive(i == index);
        }
    }

    private void ApplyPose(Transform point, bool snap)
    {
        if (menuCamera == null || point == null) return;
        menuCamera.transform.SetPositionAndRotation(point.position, point.rotation);
    }

    private int IndexOf(string viewName)
    {
        for (int i = 0; i < views.Count; i++)
            if (views[i].name == viewName) return i;
        return -1;
    }
}
