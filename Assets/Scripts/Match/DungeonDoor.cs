using UnityEngine;

/// <summary>
/// A swinging door leaf. Plain MonoBehaviour by design -- it registers with DungeonDoors for a
/// replicated id rather than being a NetworkObject, so a dungeon full of doors doesn't blow up the
/// local-deterministic model (see DungeonDoors for the reasoning).
///
/// PUT THIS ON THE DOOR'S ROOT inside a room prefab, with `leaf` pointing at the swinging mesh. The
/// leaf's PIVOT MUST SIT ON THE HINGE AXIS, not at the mesh centre -- otherwise it rotates about its
/// middle and swings through the wall.
///
/// CURRENT LIMITATION (expected, not a bug): this does NOT carve the navmesh, per the locked DELAY
/// door design. So a closed door blocks PLAYERS (CharacterController collides with the leaf) but the
/// monster walks straight through, because NavMeshAgent follows the navmesh rather than physics. That
/// looks wrong and breaks nothing. When AI door handling lands, "open it, which costs time" replaces
/// the clipping -- and that delay IS the mechanic.
/// </summary>
public class DungeonDoor : MonoBehaviour, IInteractable
{
    [Tooltip("The swinging mesh. Its pivot must be on the hinge axis.")]
    [SerializeField] private Transform leaf;

    [Tooltip("Hinge axis in the LEAF'S OWN LOCAL SPACE. (0,1,0) is right when the leaf's local Y points " +
             "up -- but an FBX imported without Bake Axis Conversion keeps Blender's Z-up, so its hinge " +
             "is often (0,0,1) instead. If the door swings on the wrong axis, change this, not the mesh.")]
    [SerializeField] private Vector3 hingeAxis = Vector3.up;

    [Tooltip("Degrees the leaf swings. Slightly past 90 reads as pushed open rather than snapped square. " +
             "The door is DOUBLE-ACTING -- it swings this far in whichever direction the player pushed " +
             "from, so the sign here doesn't matter.")]
    [SerializeField] private float openAngle = 100f;

    [Tooltip("Flip this if the door opens TOWARD the pusher instead of away. The face direction is " +
             "worked out automatically from the geometry, so this is the only knob you should need.")]
    [SerializeField] private bool invertPushSide;

    [Tooltip("Seconds for a full swing. Fast enough not to feel sluggish under pressure, slow enough " +
             "that you see it happen.")]
    [SerializeField] private float swingDuration = 0.45f;

    [Tooltip("Shapes the swing over time. X = normalised progress, Y = fraction of Open Angle.\n\n" +
             "Ease-in-out reads as a push. A curve that rises ABOVE 1 near the end and settles back " +
             "gives the leaf a slight overshoot as it hits the stop, which is what makes a door feel " +
             "like it has weight. Values outside 0-1 are fine and are the point.")]
    [SerializeField] private AnimationCurve swingCurve = AnimationCurve.EaseInOut(0f, 0f, 1f, 1f);

    [SerializeField] private string openPrompt = "Close door";
    [SerializeField] private string closedPrompt = "Open door";

    private int _id = -1;
    private bool _open;

    // Which way the leaf is currently swinging: +1 or -1. Kept while CLOSING too, so the door retreats
    // along the same arc it opened on instead of snapping through the frame to close the other way.
    private float _dir = 1f;

    // The leaf's face normal in world space with the door SHUT. Captured once -- reading it live would
    // rotate with the leaf, so a half-open door would report the wrong side and could reverse mid-swing.
    private Vector3 _closedFaceWS;

    // The full authored "shut" rotation, kept as a QUATERNION. Storing a single euler angle and writing
    // Quaternion.Euler(0, y, 0) would wipe any X/Z the import left on the leaf -- which an FBX brought in
    // without Bake Axis Conversion definitely has.
    private Quaternion _closedRot;

    // Normalised progress, 0 = shut, 1 = fully open. Driving a 0-1 value and shaping it through the
    // curve is what allows easing (and overshoot) -- animating the ANGLE directly can only ever move
    // linearly, because the angle is both the thing being eased and the thing being measured.
    private float _t;

    private void Awake()
    {
        if (leaf == null) leaf = transform;
        _closedRot = leaf.localRotation;
        if (hingeAxis.sqrMagnitude < 1e-6f) hingeAxis = Vector3.up;

        _closedFaceWS = DeriveClosedFaceNormal();
        if (invertPushSide) _closedFaceWS = -_closedFaceWS;
    }

    /// <summary>
    /// Which way the shut door's face points, in world space, derived from the geometry rather than a
    /// hand-set axis.
    ///
    /// The leaf pivots on the hinge and EXTENDS away from it toward its own centre of mass. That extent
    /// direction and the hinge axis both lie IN the door's plane, so their cross product is perpendicular
    /// to the plane -- i.e. the face normal. No guessing which local axis is which after an FBX import.
    ///
    /// Why this is worth deriving: a hand-set face axis that happens to be PARALLEL to the hinge axis
    /// makes the side test measure height above the hinge instead of side of the door. The player is
    /// always below, so it always resolves the same way and the door only ever opens one direction --
    /// which looks like the feature is broken rather than misconfigured.
    ///
    /// Captured ONCE, shut. Reading it live would rotate with the leaf, so pushing a half-open door
    /// could reverse its direction mid-swing.
    /// </summary>
    private Vector3 DeriveClosedFaceNormal()
    {
        Vector3 axisWS = leaf.TransformDirection(hingeAxis.normalized);

        var rend = leaf.GetComponentInChildren<Renderer>();
        if (rend != null)
        {
            Vector3 extent = rend.bounds.center - leaf.position;         // hinge -> middle of the leaf
            extent -= Vector3.Project(extent, axisWS);                   // drop any along-hinge component
            if (extent.sqrMagnitude > 1e-6f)
                return Vector3.Cross(axisWS, extent).normalized;
        }

        // No renderer to measure (or a leaf pivoted at its own centre): fall back to any axis that
        // isn't the hinge, so the test at least measures a horizontal direction.
        Vector3 fallback = Mathf.Abs(Vector3.Dot(axisWS, Vector3.up)) > 0.9f ? Vector3.forward : Vector3.up;
        return Vector3.Cross(axisWS, fallback).normalized;
    }

    /// <summary>Closed rotation, plus the swing for normalised progress `t`, shaped by the curve and
    /// signed by the direction it was pushed.</summary>
    private Quaternion RotationAt(float t) =>
        _closedRot * Quaternion.AngleAxis(swingCurve.Evaluate(t) * openAngle * _dir, hingeAxis.normalized);

    private void Start()
    {
        // Register in Start, not Awake: the generator instantiates rooms during its own Awake/Update
        // pass, and DungeonDoors.Instance may not be assigned yet when a room's Awake runs.
        _id = DungeonDoors.Instance != null ? DungeonDoors.Instance.Register(this) : -1;
        if (_id >= 0) SetState(DungeonDoors.Instance.StateOf(_id));

        // Snap to state on spawn rather than animating -- a late joiner shouldn't watch every door in
        // the level swing open as they load in.
        _t = _open ? 1f : 0f;
        leaf.localRotation = RotationAt(_t);
    }

    /// <summary>
    /// Called by DungeonDoors when replicated state changes. 0 = closed, +/-1 = open in that direction.
    /// The direction is only latched while OPENING -- on close we keep the last one so the leaf retreats
    /// along the arc it came from instead of snapping through the frame to close the other way.
    /// </summary>
    public void SetState(sbyte state)
    {
        if (state != 0) _dir = state;
        _open = state != 0;
    }

    private void Update()
    {
        float target = _open ? 1f : 0f;
        if (Mathf.Approximately(_t, target)) return;

        // Progress moves LINEARLY; the curve does the shaping. Reversing mid-swing therefore eases
        // correctly from wherever the leaf currently is, with no special case.
        _t = Mathf.MoveTowards(_t, target, Time.deltaTime / Mathf.Max(swingDuration, 0.01f));
        leaf.localRotation = RotationAt(_t);
    }

    // ---- IInteractable ----

    public string GetPrompt(PlayerInteractor interactor) => _open ? openPrompt : closedPrompt;

    public void Interact(PlayerInteractor interactor)
    {
        if (_id < 0) return;

        // Which side is the pusher on? Dot the vector from the hinge to the player against the leaf's
        // CLOSED face normal. Positive means they're in front of the face, so the door must swing the
        // other way -- i.e. away from them. Hence the negation.
        //
        // Measured against the closed normal rather than the live one so the answer doesn't change as
        // the leaf rotates; otherwise pushing a half-open door could flip its direction mid-swing.
        Vector3 toPlayer = interactor.transform.position - leaf.position;
        sbyte dir = Vector3.Dot(toPlayer, _closedFaceWS) > 0f ? (sbyte)(-1) : (sbyte)1;

        // Ask, don't act. The server owns the state so two players hitting the same door on the same
        // frame can't leave it open on one client and shut on another.
        DungeonDoors.Instance?.RequestToggleRpc(_id, dir);
    }
}
