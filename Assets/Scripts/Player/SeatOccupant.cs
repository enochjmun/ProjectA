using Unity.Netcode;
using UnityEngine;

/// <summary>
/// Lives on the Player prefab, beside PlayerState. Handles being seated at a
/// SeatStation and being dropped from it.
///
/// The networking split that makes this work: the player uses a client-
/// authoritative ClientNetworkTransform, so the SERVER can't smoothly drive the
/// body. Instead the server just animates the STATION (server-authoritative) and
/// publishes which station is mine + whether I'm seated; the OWNING client then
/// lerps its own body onto the station's moving seat anchor. Following your own
/// chair locally keeps client authority intact and makes the live re-space
/// smooth -- no per-frame teleport RPCs, no snapping.
///
/// While seated: Unity's CharacterController is disabled so our direct transform
/// writes stick (same reason NetworkTeleporter disables it across a move), and
/// PlayerMovement.MovementLocked suppresses WASD/gravity while leaving mouse look
/// running -- "locked in place, free look". On drop we re-enable the controller
/// so gravity resumes and the player falls through the open trapdoor.
/// </summary>
[RequireComponent(typeof(NetworkObject))]
public class SeatOccupant : NetworkBehaviour
{
    [SerializeField] private float followLerpSpeed = 12f;

    [Header("Seated pose")]
    [Tooltip("Animator on the child model. Auto-found if left empty.")]
    [SerializeField] private Animator animator;
    [Tooltip("Bool parameter that switches the Animator to the seated state. Add a Seated state (your sit " +
             "clip) to the Controller and transition to/from it on this bool. If the param doesn't exist " +
             "yet, this hook stays dormant (no warnings) until you add it.")]
    [SerializeField] private string seatedParam = "Seated";
    private int _seatedHash;
    private bool _hasSeatedParam;

    [Header("Debug — strip before shipping")]
    [SerializeField] private KeyCode debugDropKey = KeyCode.F7;

    // Server-set: the station this player is seated at (empty = none). Private so the
    // Netcode inspector doesn't render (and warn on) it in edit mode -- set via
    // ServerSeat/ServerRelease, read locally by the owner in Update.
    private readonly NetworkVariable<NetworkBehaviourReference> AssignedStation =
        new NetworkVariable<NetworkBehaviourReference>(
            default,
            NetworkVariableReadPermission.Everyone,
            NetworkVariableWritePermission.Server);

    // Server-set: true while actively seated, false once dropped/released. Private for
    // the same reason as AssignedStation above.
    private readonly NetworkVariable<bool> Seated = new NetworkVariable<bool>(
        false,
        NetworkVariableReadPermission.Everyone,
        NetworkVariableWritePermission.Server);

    private CharacterController _cc;
    private PlayerMovement _movement;
    private bool _seatedLocal;

    /// <summary>Owner-side: are we currently seated (respects the teleport-release latch)? CameraFeel reads
    /// this to lean/zoom the view in over the table so faces across it are readable.</summary>
    public bool IsSeated => _seatedLocal;

    // Latched by the teleport (see OwnerReleaseForTeleport). Overrides the Seated NetworkVariable on the
    // owner so the seat-follow stops the instant a teleport moves us -- without waiting for the server's
    // release to replicate. Cleared once that authoritative release actually arrives.
    private bool _releasedByTeleport;

    private void Awake()
    {
        _cc = GetComponent<CharacterController>();
        _movement = GetComponent<PlayerMovement>();
        if (animator == null) animator = GetComponentInChildren<Animator>();
        _seatedHash = Animator.StringToHash(seatedParam);
        if (animator != null)
            foreach (var p in animator.parameters)
                if (p.type == AnimatorControllerParameterType.Bool && p.name == seatedParam)
                { _hasSeatedParam = true; break; }
    }

    // ---- Server API (called by TableSeatingManager) ----

    /// <summary>Server-only: seat this player at the given station.</summary>
    public void ServerSeat(SeatStation station)
    {
        if (!IsServer) return;
        AssignedStation.Value = new NetworkBehaviourReference(station);
        Seated.Value = true;
    }

    /// <summary>Server-only: release/drop this player (they fall / stand up).</summary>
    public void ServerRelease()
    {
        if (!IsServer) return;
        Seated.Value = false;
        AssignedStation.Value = default;
    }

    /// <summary>
    /// Owner-side: called by NetworkTeleporter the instant a teleport moves this player, so the seat-
    /// follow in Update stops fighting the new position. Fixes the REMOTE-client drop: the server's
    /// ServerRelease (a NetworkVariable) hasn't replicated to a remote owner when the teleport RPC lands,
    /// so the glue would snap the just-teleported player straight back onto the fallen chair (into the
    /// void) -- which is why the HOST teleported fine but clients didn't. Latched, so it's independent of
    /// when the release replicates.
    /// </summary>
    public void OwnerReleaseForTeleport()
    {
        if (!IsOwner) return;
        _releasedByTeleport = true;
        if (_seatedLocal) ExitSeat();
    }

    // ---- Owner-side seat follow ----

    private void Update()
    {
        // Seated pose on EVERY machine (so remotes see each other sit, not just the owner). Owner keys off
        // its local seat state (respects the teleport-release latch); remotes off the replicated flag.
        // Dormant until the Animator actually has the bool param, so it can't spam "param doesn't exist".
        if (_hasSeatedParam)
            animator.SetBool(_seatedHash, IsOwner ? _seatedLocal : Seated.Value);

        if (!IsOwner) return;

        // DEBUG (strip before shipping): drop your own seat on keypress to test the trapdoor solo.
        if (Input.GetKeyDown(debugDropKey))
            DebugDropServerRpc();

        // Once the authoritative release has caught up, drop the teleport latch so a future round can
        // seat us normally again.
        if (!Seated.Value) _releasedByTeleport = false;

        bool hasStation = AssignedStation.Value.TryGet(out SeatStation station);
        bool seated = Seated.Value && hasStation && station.SeatAnchor != null && !_releasedByTeleport;

        if (seated)
        {
            if (!_seatedLocal) EnterSeat(station);

            if (station.IsFalling)
            {
                // Dropped: glue straight to the falling chair's anchor (no lerp) so we ride it down
                // still seated instead of lagging behind gravity -- BUT let go once it bottoms out.
                //
                // ⚠ REMOTE-CLIENT TELEPORT RACE (fixed 2026-08-04): the dungeon teleport fires at the
                // shaft bottom (MatchController.FallThenDropRoutine) as an owner RPC, while the server's
                // seat-release (the Seated NetworkVariable) is still IN FLIGHT to a remote owner -- it
                // does NOT arrive in the routine's single-frame yield. So on a remote client we'd still
                // be gluing here when the teleport lands, and the next frame would snap the just-
                // teleported player straight back onto the fallen chair (now in the void) -- they'd
                // never land in the dungeon. (Worked for the HOST only, whose NetworkVariable is local.)
                // Releasing the glue at the bottom -- which the owner knows independently from the
                // replicated fall time -- lets the teleport stick regardless of when Seated replicates.
                if (!station.HasReachedShaftBottom)
                    transform.position = station.SeatAnchor.position;
            }
            else
            {
                // Sit exactly on the anchor. The anchor already moves smoothly during a re-space, so
                // snapping to it is just as smooth as lerping -- and it removes a SECOND never-settling
                // chase: a lerp toward a fixed anchor creeps forever, and layered on the station's own
                // motion that showed up as a fine jitter. Direct write is legal -- we own the transform.
                transform.position = station.SeatAnchor.position;
            }
        }
        else if (_seatedLocal)
        {
            ExitSeat();
        }
    }

    private void EnterSeat(SeatStation station)
    {
        _seatedLocal = true;

        // Disable the controller so our transform writes aren't fought by CC.Move,
        // and stop WASD/gravity while keeping mouse look.
        if (_cc != null) _cc.enabled = false;
        if (_movement != null) _movement.MovementLocked = true;

        // Snap facing toward the table once (yaw only); look stays free after this.
        float yaw = station.SeatAnchor.rotation.eulerAngles.y;
        transform.rotation = Quaternion.Euler(0f, yaw, 0f);
    }

    private void ExitSeat()
    {
        _seatedLocal = false;

        if (_movement != null) _movement.MovementLocked = false;
        // Re-enable last so the controller adopts our current (seat) position,
        // then gravity takes over from here.
        if (_cc != null) _cc.enabled = true;
    }

    // DEBUG (strip before shipping): opens + drops the seat this player is in, so the trapdoor
    // open/hole/fall sequence can be tested solo without a full minigame loss.
    [Rpc(SendTo.Server)]
    private void DebugDropServerRpc()
    {
        if (AssignedStation.Value.TryGet(out SeatStation station))
            station.ServerBeginFall();
    }
}
