using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// Bows an Image / RawImage / legacy Text into a convex cap to match a curved CRT glass. Only displaces
/// each vertex in DEPTH (local Z) by a paraboloid of its distance from the screen centre, so the flat
/// RectTransform used for click-raycasting is untouched. Pair with CurvedUIText for TMP text on the same
/// screen, using the SAME 'curvature'. Only needed once you actually have Images on the canvas -- a
/// text-only menu just uses CurvedUIText.
///
/// SETUP: add to each Image/RawImage on the screen; match 'curvature' (and sign) to the CurvedUIText
/// elements. Keep all curved elements under the SAME canvas.
/// </summary>
[RequireComponent(typeof(Graphic))]
public class CurvedUIGraphic : BaseMeshEffect
{
    [Tooltip("Left-right bow. Flip the sign to change horizontal curve direction. Match CurvedUIText.")]
    public float curvatureX = -22f;
    [Tooltip("Top-bottom bow. Flip independently of X. Match CurvedUIText.")]
    public float curvatureY = -22f;
    [Tooltip("Constant push along local Z — keeps the element OFF the glass, INDEPENDENT of the bow. Match CurvedUIText.")]
    public float depthOffset = 0f;

    private RectTransform _canvasRT;

    private void Grab()
    {
        if (_canvasRT != null) return;
        var c = GetComponentInParent<Canvas>();
        if (c != null) _canvasRT = c.rootCanvas.GetComponent<RectTransform>();
    }

    private float ZOffset(Vector3 world)
    {
        if (_canvasRT == null) return 0f;
        Vector3 c = _canvasRT.InverseTransformPoint(world);
        float hw = _canvasRT.rect.width * 0.5f;
        float hh = _canvasRT.rect.height * 0.5f;
        if (hw <= 0f || hh <= 0f) return 0f;
        float nx = c.x / hw, ny = c.y / hh;
        // per-axis bow (match CurvedUIText); depthOffset keeps it off the glass.
        return depthOffset + curvatureX * (nx * nx) + curvatureY * (ny * ny);
    }

    public override void ModifyMesh(VertexHelper vh)
    {
        if (!IsActive()) return;
        Grab();
        if (_canvasRT == null) return;

        var rt = (RectTransform)transform;
        UIVertex v = default;
        for (int i = 0; i < vh.currentVertCount; i++)
        {
            vh.PopulateUIVertex(ref v, i);
            Vector3 world = rt.TransformPoint(v.position);
            v.position.z += ZOffset(world);
            vh.SetUIVertex(v, i);
        }
    }
}
