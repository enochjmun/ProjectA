using UnityEngine;
using TMPro;

/// <summary>
/// Bows a TextMeshPro (UGUI) text into a convex cap so it matches a curved CRT glass instead of sitting
/// flat on a bulging screen. Only displaces each vertex in DEPTH (local Z) by a paraboloid of its distance
/// from the screen centre -- the visual curves toward the viewer at the middle, while the flat
/// RectTransform used for click-raycasting is untouched, so buttons still hit where they look. Re-warps
/// automatically as the text changes (hooks TMP's text-changed event), so booting/typed text curves too.
///
/// SETUP: add to each TMP text on a wing/hero canvas. Set 'curvature' the SAME on every element of a
/// screen so they form one dome. Start ~ -22; flip the sign if it caves the wrong way. Keep all curved
/// elements under the SAME canvas (the bow is measured against the root canvas rect).
/// </summary>
[ExecuteAlways]
[RequireComponent(typeof(TMP_Text))]
public class CurvedUIText : MonoBehaviour
{
    [Tooltip("Left-right bow (edges vs centre). Flip the sign to change which way it curves horizontally.")]
    public float curvatureX = -22f;
    [Tooltip("Top-bottom bow. Flip the sign independently of X (so up/down and left/right can each be correct).")]
    public float curvatureY = -22f;
    [Tooltip("Constant push along local Z — keeps the whole element OFF the glass, INDEPENDENT of the bow. " +
             "Raise (or flip sign) until the text sits in FRONT of the screen mesh. Match across a screen.")]
    public float depthOffset = 0f;

    private TMP_Text _tmp;
    private RectTransform _canvasRT;

    private void Awake() { _tmp = GetComponent<TMP_Text>(); Grab(); }

    private void Grab()
    {
        if (_canvasRT != null) return;
        var c = GetComponentInParent<Canvas>();
        if (c != null) _canvasRT = c.rootCanvas.GetComponent<RectTransform>();
    }

    private void OnEnable()
    {
        TMPro_EventManager.TEXT_CHANGED_EVENT.Add(OnTextChanged);
        if (_tmp != null) _tmp.ForceMeshUpdate();   // triggers the event -> warps the current mesh
    }

    private void OnDisable() => TMPro_EventManager.TEXT_CHANGED_EVENT.Remove(OnTextChanged);

    private void OnTextChanged(Object obj)
    {
        if (obj == _tmp) Warp();
    }

    private float ZOffset(Vector3 world)
    {
        if (_canvasRT == null) return 0f;
        Vector3 c = _canvasRT.InverseTransformPoint(world);
        float hw = _canvasRT.rect.width * 0.5f;
        float hh = _canvasRT.rect.height * 0.5f;
        if (hw <= 0f || hh <= 0f) return 0f;
        float nx = c.x / hw, ny = c.y / hh;
        // per-axis bow so horizontal and vertical curve directions are independent; depthOffset keeps it off the glass.
        return depthOffset + curvatureX * (nx * nx) + curvatureY * (ny * ny);
    }

    // TMP hands us freshly-flat vertices each rebuild, so we add the bow once per rebuild.
    private void Warp()
    {
        Grab();
        if (_canvasRT == null || _tmp == null) return;

        var info = _tmp.textInfo;
        if (info == null) return;

        for (int m = 0; m < info.meshInfo.Length; m++)
        {
            var verts = info.meshInfo[m].vertices;
            if (verts == null) continue;
            for (int j = 0; j < verts.Length; j++)
            {
                Vector3 world = transform.TransformPoint(verts[j]);
                verts[j].z += ZOffset(world);
            }
        }

        for (int m = 0; m < info.meshInfo.Length; m++)
        {
            var mesh = info.meshInfo[m].mesh;
            if (mesh == null) continue;
            mesh.vertices = info.meshInfo[m].vertices;
            _tmp.UpdateGeometry(mesh, m);
        }
    }
}
