using System.Collections.Generic;
using System.Linq;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.AI;

/// <summary>
/// The vertical-slice monster (GDD: Stalker). Server-authoritative: the server runs the
/// NavMeshAgent and all decisions; clients just render the synced transform.
///
/// HYBRID, POSITIONAL-ONLY sensing (design locked 2026-07-17). The Stalker only gets an exact
/// fix on the prey when it SEES them -- inside a 180 degree forward cone, within visionRange, with
/// clear line of sight. It cannot hear you; the only way to break contact is positional (leave the
/// cone / put a wall between you). When it loses sight it searches your last-seen spot, and if that
/// fails it re-acquires your general ROOM (not your exact position) and keeps closing -- so a clean
/// break buys distance and a breather, never a full disappearance.
///
/// State machine:
///   Hunt      -- moving toward knownPosition (a general area). Arrive without seeing you -> Search.
///   Chase     -- can see you: home on your real position, lunge on cooldown, catch on contact.
///   Search    -- at knownPosition, scan (sweep the cone) for searchTime. Time out -> Reacquire.
///   Reacquire -- a brief "lost it" beat, then snap knownPosition to your nearest room -> Hunt.
/// Seeing the prey overrides everything and jumps straight to Chase.
///
/// Needs: a baked NavMesh, a NavMeshAgent, a NetworkObject, a server-authoritative NetworkTransform,
/// and `sightBlockers` set to the wall/environment layer. Spawned/despawned by MatchController.
/// </summary>
[RequireComponent(typeof(NavMeshAgent))]
[RequireComponent(typeof(NetworkObject))]
public class StalkerAI : NetworkBehaviour
{
    private enum State { Hunt, Chase, Search, Reacquire }

    [Header("Movement")]
    [SerializeField] private float chaseSpeed = 4.5f;
    [SerializeField] private float lungeSpeed = 11f;

    [Header("Lunge")]
    [SerializeField] private float lungeRange = 5f;       // start a lunge within this distance
    [SerializeField] private float lungeCooldown = 3f;
    [SerializeField] private float lungeDuration = 0.6f;

    [Header("Catch")]
    [SerializeField] private float catchRange = 1.0f;     // caught if it gets this close (near-contact)
    [SerializeField] private float retargetInterval = 0.3f;

    [Header("Vision (the 180-degree forward cone)")]
    [Tooltip("How far it can see.")]
    [SerializeField] private float visionRange = 18f;
    [Tooltip("Total cone width in degrees. 180 = the whole front half; the rear half is a blind spot.")]
    [SerializeField] private float visionAngle = 180f;
    [Tooltip("Eye height for the line-of-sight check, so the ray isn't cast along the floor.")]
    [SerializeField] private float eyeHeight = 1.2f;
    [Tooltip("Layers that block sight (walls / environment). MUST be set to the dungeon's wall layer.")]
    [SerializeField] private LayerMask sightBlockers;

    [Tooltip("Draw the vision cone + state in the Scene view (editor only). Turn off to declutter.")]
    [SerializeField] private bool drawGizmos = true;

    [Header("Losing & searching")]
    [Tooltip("Grace after losing sight before it drops to Search -- so clipping behind a pillar for a " +
             "split second doesn't instantly free you.")]
    [SerializeField] private float loseSightGrace = 0.7f;
    [Tooltip("When it loses you, how far PAST the last-seen spot to head, in the direction you were " +
             "moving -- so it rounds the corner you just took instead of stopping where it lost you.")]
    [SerializeField] private float searchLeadDistance = 3.5f;
    [Tooltip("How long it sweeps the last-seen spot before giving up and re-acquiring.")]
    [SerializeField] private float searchTime = 3f;
    [Tooltip("How close it must get to a known spot to count as 'arrived' and start scanning.")]
    [SerializeField] private float arriveRadius = 1.5f;
    [Tooltip("Turn rate (deg/sec) while scanning, so the cone sweeps the area.")]
    [SerializeField] private float scanSpeed = 120f;
    [Tooltip("The brief 'lost it' pause before it re-acquires your general area.")]
    [SerializeField] private float reacquireDelay = 0.6f;

    [Header("Head start")]
    [Tooltip("Seconds the prey gets to run before the Stalker activates. Also stops an instant catch " +
             "when the monster happens to spawn near a player.")]
    [SerializeField] private float headStart = 3f;

    private NavMeshAgent _agent;
    private StalkerAnimator _animator;
    private float _huntStartTime;

    private PlayerState _target;
    private float _retargetTimer;

    private float _lungeCooldownTimer;
    private float _lungeActiveTimer;

    // --- The three things it remembers (the whole "brain state") ---
    private State _state;
    private Vector3 _knownPosition;   // where it currently thinks the prey is
    private float _lastSeenTime;      // Time.time of the last frame it actually saw the prey
    private float _searchTimer;       // counts down while searching
    private float _reacquireTimer;    // counts down during the "lost it" beat

    // The prey's travel direction the last time we saw it, so Search can lead AROUND the corner it took
    // instead of stopping at the last-seen spot. Updated every frame the prey is visible.
    private Vector3 _preyLeadDir = Vector3.forward;
    private Vector3 _prevSeenPos;

    public override void OnNetworkSpawn()
    {
        _agent = GetComponent<NavMeshAgent>();
        _animator = GetComponent<StalkerAnimator>();
        // Only the server drives the AI; clients just render the synced transform.
        if (_agent != null)
            _agent.enabled = IsServer;
        enabled = IsServer;
        _huntStartTime = Time.time + headStart;
        _state = State.Hunt;
    }

    private void Update()
    {
        if (!IsServer || _agent == null || !_agent.isOnNavMesh)
            return;

        // Head start: inert for a moment so the prey can run.
        if (Time.time < _huntStartTime)
            return;

        // Default: let the NavMeshAgent steer its own facing. The scan branch turns this off
        // for the frames it wants to sweep the cone manually.
        _agent.updateRotation = true;

        // --- Pick / validate a target ---
        _retargetTimer -= Time.deltaTime;
        if (_retargetTimer <= 0f || !IsValidPrey(_target))
        {
            _retargetTimer = retargetInterval;
            var newTarget = FindNearestPrey();
            if (newTarget != _target)
            {
                _target = newTarget;
                if (_target != null)
                {
                    // Fresh target: start by hunting its general area (we haven't seen it yet).
                    _knownPosition = GeneralAreaOf(_target.transform.position);
                    _prevSeenPos = _target.transform.position;   // seed lead tracking so the first frame isn't garbage
                    _state = State.Hunt;
                }
            }
        }
        if (_target == null)
            return;

        // --- The one check the whole behaviour hangs on ---
        bool canSee = CanSeePrey(_target);

        // Seeing the prey overrides every other state: lock on and (re)enter Chase.
        if (canSee)
        {
            // Track the prey's travel direction so that if we lose them we can search AROUND the corner
            // they took, not stop where we lost them. Cap the delta so the first sighting / a teleport
            // doesn't set a garbage direction.
            Vector3 move = _target.transform.position - _prevSeenPos;
            move.y = 0f;
            if (move.sqrMagnitude > 0.0004f && move.sqrMagnitude < 4f)
                _preyLeadDir = move.normalized;
            _prevSeenPos = _target.transform.position;

            _knownPosition = _target.transform.position;
            _lastSeenTime = Time.time;
            _state = State.Chase;
        }

        switch (_state)
        {
            case State.Chase:     TickChase(canSee); break;
            case State.Search:    TickSearch();      break;
            case State.Reacquire: TickReacquire();   break;
            default:              TickHunt();        break;   // Hunt
        }
    }

    // ---- States ----

    // Homes on the prey and tries to catch. Reached only when canSee is true (set above), or during
    // the brief grace window right after losing sight.
    private void TickChase(bool canSee)
    {
        float dist = HorizontalDistance(transform.position, _target.transform.position);

        // Lunge: a short speed burst on a cooldown when within lunge range (unchanged behaviour).
        if (_lungeActiveTimer > 0f)
        {
            _lungeActiveTimer -= Time.deltaTime;
            _agent.speed = lungeSpeed;
        }
        else
        {
            _agent.speed = chaseSpeed;
            _lungeCooldownTimer -= Time.deltaTime;
            if (dist <= lungeRange && _lungeCooldownTimer <= 0f)
            {
                _lungeActiveTimer = lungeDuration;
                _lungeCooldownTimer = lungeCooldown;
                _animator?.ServerPlayLunge();   // play the attack on every client
            }
        }

        // Head for the last place we knew the prey was. While canSee is true this is refreshed to the
        // real position every frame (set in Update); during the grace window it's the last-seen spot.
        _agent.SetDestination(_knownPosition);

        if (dist <= catchRange)
            MatchController.Instance?.ReportCaught(_target);

        // Lost sight for longer than the grace window -> search, but LEAD past the last-seen spot in the
        // prey's travel direction so the monster rounds the corner they took instead of stopping at it.
        if (!canSee && Time.time - _lastSeenTime > loseSightGrace)
        {
            _knownPosition += _preyLeadDir * searchLeadDistance;   // the agent paths to the nearest reachable point
            _state = State.Search;
            _searchTimer = searchTime;
        }
    }

    // Walk to the last-seen spot; once there, stand and sweep the cone. Time out -> Reacquire.
    private void TickSearch()
    {
        _agent.speed = chaseSpeed;
        _searchTimer -= Time.deltaTime;

        if (HorizontalDistance(transform.position, _knownPosition) <= arriveRadius)
        {
            // Arrived: stop letting the agent steer facing and sweep the 180 cone by hand, so a prey
            // hiding just out of view can be swept up instead of staying invisible forever.
            _agent.updateRotation = false;
            transform.Rotate(0f, scanSpeed * Time.deltaTime, 0f);
        }
        else
        {
            _agent.SetDestination(_knownPosition);
        }

        if (_searchTimer <= 0f)
        {
            _state = State.Reacquire;
            _reacquireTimer = reacquireDelay;
        }
    }

    // The "lost it" beat: hold still briefly, then snap the known position to the prey's general room
    // and resume hunting. This is the hybrid guarantee -- it always comes back to your neighbourhood.
    private void TickReacquire()
    {
        _agent.SetDestination(transform.position);   // hold position for the beat
        _reacquireTimer -= Time.deltaTime;

        if (_reacquireTimer <= 0f)
        {
            _knownPosition = GeneralAreaOf(_target.transform.position);
            _state = State.Hunt;
        }
    }

    // Travel to the general area. Arrive without having spotted the prey -> Search (sweep it).
    private void TickHunt()
    {
        _agent.speed = chaseSpeed;
        _agent.SetDestination(_knownPosition);

        if (HorizontalDistance(transform.position, _knownPosition) <= arriveRadius)
        {
            _state = State.Search;
            _searchTimer = searchTime;
        }
    }

    // ---- Sensing ----

    // The 3-part "can I see the prey right now?" test: in range, in the 180 cone, and no wall between.
    private bool CanSeePrey(PlayerState prey)
    {
        if (prey == null)
            return false;

        // 1) In range (horizontal only -- ignore height so a vertical pivot gap doesn't matter).
        Vector3 to = prey.transform.position - transform.position;
        to.y = 0f;
        float dist = to.magnitude;
        if (dist > visionRange)
            return false;

        // 2) In the cone: angle between where it's facing and the direction to the prey. Half the
        //    cone each side, so a 180 cone means <= 90 degrees off-forward.
        Vector3 fwd = transform.forward;
        fwd.y = 0f;
        if (Vector3.Angle(fwd, to) > visionAngle * 0.5f)
            return false;

        // 3) Line of sight: a ray from eye to eye. If it hits anything on the wall layers, the view
        //    is blocked. Linecast returns TRUE when something is in the way.
        Vector3 eye = transform.position + Vector3.up * eyeHeight;
        Vector3 preyEye = prey.transform.position + Vector3.up * eyeHeight;
        if (Physics.Linecast(eye, preyEye, sightBlockers))
            return false;

        return true;
    }

    // ---- Target selection (unchanged from before; supports multi-prey even though the slice is single-loser) ----

    private bool IsValidPrey(PlayerState p)
    {
        var mc = MatchController.Instance;
        return p != null && mc != null && mc.FallenPlayers.Contains(p) && !mc.HasChaseOutcome(p);
    }

    private PlayerState FindNearestPrey()
    {
        var mc = MatchController.Instance;
        if (mc == null)
            return null;

        PlayerState best = null;
        float bestSq = float.MaxValue;
        foreach (var p in mc.FallenPlayers)
        {
            if (p == null || mc.HasChaseOutcome(p))
                continue;
            float sq = (p.transform.position - transform.position).sqrMagnitude;
            if (sq < bestSq)
            {
                bestSq = sq;
                best = p;
            }
        }
        return best;
    }

    // ---- Helpers ----

    // The "general area" the re-acquire heads for: the prey's nearest room centre. Falls back to the
    // raw position if the dungeon isn't available.
    private static Vector3 GeneralAreaOf(Vector3 preyPos)
    {
        var d = DungeonGenerator.Instance;
        return d != null ? d.NearestRoomCentre(preyPos) : preyPos;
    }

    private static float HorizontalDistance(Vector3 a, Vector3 b)
    {
        a.y = 0f;
        b.y = 0f;
        return Vector3.Distance(a, b);
    }

#if UNITY_EDITOR
    // Scene-view debug draw (editor only -- OnDrawGizmos never runs in a build, and this whole block is
    // compiled out of one anyway). Shows the 180 cone, facing, range, and -- while playing -- the current
    // state (by colour) and where it thinks the prey is. State/knownPosition are only populated on the
    // machine running the AI (the server), so this reads live when you're the host.
    //
    // Colour key: Hunt = cyan, Chase = red, Search = orange, Reacquire = yellow, edit-mode = grey.
    private void OnDrawGizmos()
    {
        if (!drawGizmos)
            return;

        Vector3 origin = transform.position + Vector3.up * eyeHeight;
        Vector3 fwd = transform.forward;
        fwd.y = 0f;
        if (fwd.sqrMagnitude < 0.0001f)
            return;
        fwd.Normalize();

        // Colour by state so you can watch the transitions happen.
        Color c = Color.gray;
        if (Application.isPlaying)
        {
            switch (_state)
            {
                case State.Chase:     c = Color.red; break;
                case State.Search:    c = new Color(1f, 0.6f, 0f); break;   // orange
                case State.Reacquire: c = Color.yellow; break;
                default:              c = Color.cyan; break;                 // Hunt
            }
        }

        float half = visionAngle * 0.5f;
        Vector3 left = Quaternion.AngleAxis(-half, Vector3.up) * fwd;
        Vector3 right = Quaternion.AngleAxis(half, Vector3.up) * fwd;

        // Cone edges.
        Gizmos.color = c;
        Gizmos.DrawRay(origin, left * visionRange);
        Gizmos.DrawRay(origin, right * visionRange);

        // Arc across the far edge of the cone.
        const int segs = 24;
        Vector3 prev = origin + left * visionRange;
        for (int i = 1; i <= segs; i++)
        {
            float ang = Mathf.Lerp(-half, half, (float)i / segs);
            Vector3 point = origin + (Quaternion.AngleAxis(ang, Vector3.up) * fwd) * visionRange;
            Gizmos.DrawLine(prev, point);
            prev = point;
        }

        // Facing (brighter, shorter).
        Gizmos.color = Color.white;
        Gizmos.DrawRay(origin, fwd * (visionRange * 0.5f));

        // Where it currently thinks the prey is (only meaningful while the AI is running).
        if (Application.isPlaying)
        {
            Gizmos.color = c;
            Gizmos.DrawLine(transform.position, _knownPosition);
            Gizmos.DrawWireSphere(_knownPosition, 0.5f);
        }
    }
#endif
}
