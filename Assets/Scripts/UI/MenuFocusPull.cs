using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

/// <summary>
/// Auto-focus for the security-room menu. DoF focus is a property of the SHOT, not the scene:
/// each camera pose looks at a screen at a different distance, so a fixed focus plane (calibrated
/// to the hero) falls off the side stations when you pan. This raycasts straight ahead from the
/// menu camera and pulls focus onto whatever screen it hits, easing so it settles as the pan lands.
///
/// PRESERVES your tuned DoF look. It writes ONLY the focus location:
///   - Bokeh   : sets focusDistance. Aperture + focalLength (your blur character) are untouched.
///   - Gaussian: keeps your exact band WIDTH (gaussianEnd - gaussianStart) and maxRadius, and just
///               slides the band so its sharp edge sits on the subject. Falloff is identical.
///
/// SETUP: put on the menu camera. Assign `dofVolume` (the Volume holding the Depth Of Field override)
/// and `focusMask` (the layer your CRT screens are on). The screens need colliders on that layer
/// (a Box Collider per CRT is enough) for the ray to hit. If a ray misses, focus holds at `fallback`.
///
/// NOTE: reads `volume.profile` (a runtime instance), NOT sharedProfile, so these writes DO NOT
/// persist to the profile asset after Play — no stuck-focus-in-editor bug.
/// </summary>
[RequireComponent(typeof(Camera))]
public class MenuFocusPull : MonoBehaviour
{
    [Header("Refs")]
    [Tooltip("The Volume that holds the Depth Of Field override (Global or Local).")]
    [SerializeField] private Volume dofVolume;
    [Tooltip("Layer(s) the CRT screens are on. Screens need colliders on this layer for the ray to hit.")]
    [SerializeField] private LayerMask focusMask = ~0;

    [Header("Ray")]
    [Tooltip("How far ahead to search for a screen to focus on.")]
    [SerializeField] private float maxRayDistance = 20f;
    [Tooltip("Added to the hit distance. Small + values focus a touch behind the glass; 0 = on it.")]
    [SerializeField] private float focusOffset = 0f;
    [Tooltip("Focus distance used when the ray hits nothing (hold a sane default instead of snapping).")]
    [SerializeField] private float fallback = 2.5f;

    [Header("Pull")]
    [Tooltip("Seconds to ease onto a new focus target. ~0.25-0.5 reads as a filmic focus pull.")]
    [SerializeField] private float smoothTime = 0.35f;

    private Camera _cam;
    private DepthOfField _dof;
    private float _focus;      // current eased focus distance
    private float _vel;        // SmoothDamp velocity
    private float _gaussWidth; // captured Gaussian band width (End - Start), preserved

    private void Awake()
    {
        _cam = GetComponent<Camera>();

        if (dofVolume == null || dofVolume.profile == null ||
            !dofVolume.profile.TryGet(out _dof))
        {
            Debug.LogWarning("[MenuFocusPull] No Depth Of Field override found on dofVolume. Disabling.", this);
            enabled = false;
            return;
        }

        // Capture the tuned Gaussian band width so we slide it without changing its shape.
        _gaussWidth = Mathf.Max(0f, _dof.gaussianEnd.value - _dof.gaussianStart.value);

        // Seed from whatever the profile already focuses at, so frame 1 doesn't jump.
        _focus = _dof.mode.value == DepthOfFieldMode.Bokeh
            ? _dof.focusDistance.value
            : _dof.gaussianStart.value;
    }

    private void LateUpdate()
    {
        if (_dof == null) return;

        // Where is the subject? Raycast forward; hold fallback on a miss.
        float target = Physics.Raycast(transform.position, transform.forward,
                                       out RaycastHit hit, maxRayDistance, focusMask)
            ? hit.distance + focusOffset
            : fallback;

        _focus = Mathf.SmoothDamp(_focus, target, ref _vel, smoothTime);

        if (_dof.mode.value == DepthOfFieldMode.Bokeh)
        {
            // Only the focus plane moves; aperture + focalLength (your look) stay put.
            _dof.focusDistance.value = _focus;
        }
        else
        {
            // Gaussian: slide the band, keep its width. Sharp edge sits at the subject.
            _dof.gaussianStart.value = _focus;
            _dof.gaussianEnd.value   = _focus + _gaussWidth;
        }
    }
}
