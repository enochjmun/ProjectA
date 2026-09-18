using Unity.Netcode;
using UnityEngine;

/// <summary>
/// One seat + its drop-away platform, as a single spawned unit. This is the
/// "SeatStation" -- a chair the player sits on and a trapdoor beneath it that
/// opens to drop them. Chair and trapdoor are one object on purpose: the seating
/// ring arrays whole stations, so the platform is guaranteed to stay under its
/// chair with zero extra bookkeeping ("do the same for the platform" is
/// structural, not a second system).
///
/// The prefab ROOT uses a stock server-authoritative NetworkTransform (NOT the
/// player's ClientNetworkTransform) -- TableSeatingManager moves the station on
/// the server and it replicates for free.
///
/// This component only owns the trapdoor's open/close visual. Everything reads
/// the replicated IsOpen flag and lerps the leaf, so the animation looks the
/// same on every machine.
/// </summary>
[RequireComponent(typeof(NetworkObject))]
public class SeatStation : NetworkBehaviour
{
    [Tooltip("Empty child marking where the seated body sits and which way it " +
             "faces (its +Z should point toward the table centre).")]
    [SerializeField] private Transform seatAnchor;

    [Tooltip("The moving trapdoor leaf. Its LOCAL pivot must sit on the hinge " +
             "edge, or it swings about its middle instead of dropping open.")]
    [SerializeField] private Transform trapdoor;

    [Tooltip("Local rotation the trapdoor (hinge pivot) adds when open, relative " +
             "to its closed pose. Default swings it down ~80 degrees about local " +
             "-Z, matching the hinge-pivot empty's axis.")]
    [SerializeField] private Vector3 trapdoorOpenEuler = new Vector3(0f, 0f, -80f);

    [SerializeField] private float trapdoorLerpSpeed = 8f;

    [Tooltip("The stencil-mask object that punches the see-through hole. On only " +
             "while the trapdoor is open, so the void shows only then. Leave unset " +
             "if this station has no stencil trapdoor.")]
    [SerializeField] private GameObject stencilMask;

    [Tooltip("The chair group that drops on a loss. The seat anchor should be a " +
             "CHILD of this so the seated player rides it down. The trapdoor, hole, " +
             "and shaft must NOT be children of this -- they stay put so players " +
             "watch the drop through a fixed hole.")]
    [SerializeField] private Transform fallingChair;

    [Tooltip("Downward acceleration (m/s^2) of the dropped chair.")]
    [SerializeField] private float fallGravity = 25f;

    [Tooltip("Shaft depth in world metres. The drop routine teleports the faller to the dungeon " +
             "once the chair has fallen this far, so the SHAFT LENGTH is the drop length regardless " +
             "of gravity. Match this to the modelled shaft.")]
    [SerializeField] private float shaftDepth = 20f;

    [Tooltip("How far (world metres) the chair drops before the trapdoor slams shut behind it -- just " +
             "past the frame, so the open-hole artifact only shows for a split second. The faller then " +
             "continues down the shaft behind the closed floor.")]
    [SerializeField] private float clearOpeningDistance = 1.5f;

    /// <summary>
    /// Server-set. Drives the trapdoor open (true) or closed (false). Private so
    /// the Netcode inspector doesn't render (and warn on) it in edit mode -- write
    /// via ServerSetOpen, everyone reads it locally in Update.
    /// </summary>
    private readonly NetworkVariable<bool> IsOpen = new NetworkVariable<bool>(
        false,
        NetworkVariableReadPermission.Everyone,
        NetworkVariableWritePermission.Server);

    /// <summary>
    /// Server-set server-time when the chair started falling (-1 = not falling).
    /// The chair drop is animated LOCALLY from this on every client (like the leaf),
    /// so it stays deterministic and under the replicated seated player without a
    /// second NetworkTransform.
    /// </summary>
    private readonly NetworkVariable<double> FallStartTime = new NetworkVariable<double>(
        -1.0,
        NetworkVariableReadPermission.Everyone,
        NetworkVariableWritePermission.Server);

    /// <summary>True once the chair has been dropped. Read by SeatOccupant.</summary>
    public bool IsFalling => FallStartTime.Value >= 0.0;

    /// <summary>World metres the chair has fallen since the drop started (0 if not falling).</summary>
    public float FallenDistance
    {
        get
        {
            if (FallStartTime.Value < 0.0 || NetworkManager == null) return 0f;
            double t = NetworkManager.ServerTime.Time - FallStartTime.Value;
            if (t < 0.0) t = 0.0;
            return 0.5f * fallGravity * (float)(t * t);
        }
    }

    /// <summary>True once the falling chair has dropped past the shaft bottom.</summary>
    public bool HasReachedShaftBottom => IsFalling && FallenDistance >= shaftDepth;

    /// <summary>True once the chair has cleared the opening -- safe to slam the trapdoor shut.</summary>
    public bool HasClearedOpening => IsFalling && FallenDistance >= clearOpeningDistance;

    private Quaternion _closedLocalRot;
    private Vector3 _chairRestLocalPos;

    /// <summary>Where the occupant's body is placed/faced. Read by SeatOccupant.</summary>
    public Transform SeatAnchor => seatAnchor;

    private void Awake()
    {
        if (trapdoor != null)
            _closedLocalRot = trapdoor.localRotation;
        if (fallingChair != null)
            _chairRestLocalPos = fallingChair.localPosition;

        // Start closed: no hole until the trapdoor opens.
        if (stencilMask != null) stencilMask.SetActive(false);
    }

    private void Update()
    {
        // Runs on every machine -- purely visual, driven by the replicated flag.

        // The stencil hole only exists while open. Toggled on state change only,
        // so the void appears/vanishes in sync with the leaf swing on every client.
        if (stencilMask != null && stencilMask.activeSelf != IsOpen.Value)
            stencilMask.SetActive(IsOpen.Value);

        // Chair drop: deterministic free-fall from the synced start time, so the
        // locally-animated chair lands identically on every client and stays right
        // under the replicated seated player.
        if (fallingChair != null && FallStartTime.Value >= 0.0 && NetworkManager != null)
        {
            double t = NetworkManager.ServerTime.Time - FallStartTime.Value;
            if (t < 0.0) t = 0.0;
            float drop = 0.5f * fallGravity * (float)(t * t);   // world metres

            // localPosition lives in the PARENT's scaled space, so a non-1 root scale would
            // multiply the drop (a scale-100 root = 100x gravity). Divide by the parent's world
            // scale so `drop` stays in real metres and fallGravity means real m/s^2 regardless.
            float parentScaleY = fallingChair.parent != null
                ? Mathf.Max(0.0001f, fallingChair.parent.lossyScale.y) : 1f;
            fallingChair.localPosition = _chairRestLocalPos + Vector3.down * (drop / parentScaleY);
        }

        if (trapdoor == null) return;

        Quaternion target = IsOpen.Value
            ? _closedLocalRot * Quaternion.Euler(trapdoorOpenEuler)
            : _closedLocalRot;

        trapdoor.localRotation = Quaternion.Slerp(
            trapdoor.localRotation, target, trapdoorLerpSpeed * Time.deltaTime);
    }

    /// <summary>Server-only: open or close the trapdoor.</summary>
    public void ServerSetOpen(bool open)
    {
        if (!IsServer) return;
        IsOpen.Value = open;
    }

    /// <summary>
    /// Server-only: open the trapdoor and drop the chair (with its still-seated
    /// occupant) straight down. The trapdoor, hole, and shaft stay put so others
    /// watch the fall through a fixed opening.
    /// </summary>
    public void ServerBeginFall()
    {
        if (!IsServer) return;
        IsOpen.Value = true;
        FallStartTime.Value = NetworkManager.ServerTime.Time;
    }
}
