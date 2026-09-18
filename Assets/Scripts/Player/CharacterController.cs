using Unity.Netcode;
using UnityEngine;

[RequireComponent(typeof(CharacterController))]
public class PlayerMovement : NetworkBehaviour
{
    [Header("Movement")]
    [SerializeField] private float walkSpeed = 6f;
    [SerializeField] private float sprintSpeed = 9f;
    [SerializeField] private float gravity = -9.81f;
    [SerializeField] private float mouseSensitivity = 2f;

    [Header("Weight")]
    [Tooltip("How fast horizontal velocity ramps UP toward the target (units/sec^2). Lower = heavier, " +
             "takes longer to get going. ~40 reaches full walk speed in about 0.15s.")]
    [SerializeField] private float acceleration = 40f;
    [Tooltip("How fast horizontal velocity ramps DOWN when you slow or stop (units/sec^2). Usually a " +
             "touch higher than acceleration so stopping feels controlled rather than sliding on ice.")]
    [SerializeField] private float deceleration = 55f;
    [Tooltip("Horizontal accel/decel while AIRBORNE (units/sec^2). Much lower than the ground values so " +
             "you can't freely accelerate or steer mid-jump -- you mostly keep the momentum you launched " +
             "with and can only nudge it. ~8-12 gives a little air control without full mid-air movement.")]
    [SerializeField] private float airAcceleration = 10f;
    [Tooltip("How fast an external impulse (elevator shove-out, future knockback) bleeds off (units/sec^2).")]
    [SerializeField] private float externalDamping = 8f;

    [Header("Jump")]
    [Tooltip("Peak jump height in metres. The launch velocity is derived from this and gravity, so the " +
             "apex stays consistent if you retune gravity.")]
    [SerializeField] private float jumpHeight = 1.1f;
    [Tooltip("Grace period after walking off a ledge during which a jump still counts as grounded. " +
             "Kills the 'I pressed jump but just fell off' feel. ~0.1s is invisible but forgiving.")]
    [SerializeField] private float coyoteTime = 0.12f;
    [Tooltip("Gravity multiplier while FALLING. >1 makes the descent heavier than the rise, which is " +
             "what kills the floaty feel -- a controlled ascent and a decisive fall. ~1.5-2.")]
    [SerializeField] private float fallGravityMultiplier = 1.7f;
    [SerializeField] private KeyCode jumpKey = KeyCode.Space;
    private float _coyoteTimer;

    // The current horizontal velocity, ramped toward the input each frame instead of snapping to it.
    // This is what gives movement WEIGHT -- the body takes a moment to reach speed and to stop, so it
    // reads as a mass being moved rather than a camera being slid.
    private Vector3 _planarVel;

    // External impulse channel (elevator shove-out, future knockback). Added ON TOP of the input velocity
    // and bleeds off on its own — kept separate from _planarVel so normal accel/decel doesn't instantly
    // kill it the frame after it's applied.
    private Vector3 _externalVel;

    [Header("Animation")]
    [SerializeField] private Animator animator;          // Animator on the child model
    [SerializeField] private float speedDampTime = 0.1f; // smooths idle<->walk<->run blending

    [Header("Seating")]
    // Set by SeatOccupant while seated: suppresses WASD + gravity but keeps mouse
    // look running, so the player is locked in place yet can still look around.
    public bool MovementLocked;

    private CharacterController _controller;
    private PlayerStamina _stamina;   // optional: null = unlimited sprint
    private float _verticalVelocity;
    private bool _wasGrounded = true;

    // ---- State CameraFeel reads (owner-side only; that's the only place it's driven) ----
    /// <summary>Current horizontal speed in m/s. Drives head-bob rate and amplitude.</summary>
    public float PlanarSpeed { get; private set; }
    /// <summary>Whether sprint is actually active this frame (held + moving + stamina). Drives FOV.</summary>
    public bool IsSprinting { get; private set; }
    /// <summary>Raw strafe input, -1..1. Drives camera lean.</summary>
    public float StrafeInput { get; private set; }
    /// <summary>True when the controller is on the ground.</summary>
    public bool IsGrounded => _controller != null && _controller.enabled && _controller.isGrounded;
    /// <summary>Fires on the frame the player touches down, with the downward impact speed (m/s).
    /// CameraFeel scales the landing dip by it; small impacts can be ignored by a threshold.</summary>
    public event System.Action<float> Landed;

    // Owner writes Speed; everyone reads it and feeds their OWN local Animator.
    private NetworkVariable<float> _netSpeed = new NetworkVariable<float>(
        0f, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Owner);

    private void Awake()
    {
        _controller = GetComponent<CharacterController>();
        _stamina = GetComponent<PlayerStamina>();
        if (animator == null)
            animator = GetComponentInChildren<Animator>();
    }

    public override void OnNetworkSpawn()
    {
        if (IsOwner)
        {
            Cursor.lockState = CursorLockMode.Locked;
            Cursor.visible = false;
        }
    }   

    /// <summary>Add an external impulse (m/s) — an elevator shove-out or a future knockback. Applied on
    /// top of normal movement and decays via externalDamping. Cleared while movement is locked.</summary>
    public void AddImpulse(Vector3 velocity) => _externalVel += velocity;

    private void Update()
    {
        // --- OWNER-ONLY: input, movement, and publishing the Speed value ---
        if (IsOwner)
        {
            // Body yaw (mouse X rotates the whole capsule; replicates via ClientNetworkTransform).
            // Only while the cursor is LOCKED -- when a mini-game frees the cursor
            // for pointing/clicking at cards, moving the mouse must not also turn
            // the player. (Same guard as PlayerCamera's pitch.)
            if (Cursor.lockState == CursorLockMode.Locked)
            {
                float mouseX = Input.GetAxisRaw("Mouse X") * mouseSensitivity;
                transform.Rotate(Vector3.up, mouseX);
            }

            // Also treat a DISABLED controller as locked. MovementLocked and the
            // controller's enabled state are written by different scripts (SeatOccupant,
            // SpectatorController, NetworkTeleporter) and can disagree for a few frames
            // during transitions -- e.g. a caught player being un-spectated, teleported,
            // and re-seated on chase resolve. Without this guard, MovementLocked briefly
            // reads false while the controller is still disabled, and CharacterController.Move
            // throws "called on inactive controller". Move is never valid on a disabled
            // controller, whoever disabled it, so this is the correct backstop.
            if (MovementLocked || !_controller.enabled)
            {
                // Seated (or otherwise controller-disabled): SeatOccupant/teleporter holds
                // our position via direct transform writes. Don't touch the CharacterController;
                // report zero speed so the walk blend stays idle. The yaw look above still ran,
                // so the player can look around while pinned.
                // Clear the ramped velocity too, or it would be retained and fling the player the
                // instant control is handed back after a teleport/unseat.
                _planarVel = Vector3.zero;
                _externalVel = Vector3.zero;
                PlanarSpeed = 0f;
                IsSprinting = false;
                StrafeInput = 0f;
                _netSpeed.Value = 0f;

                // Still tick stamina as "not sprinting" so it REGENERATES while seated/locked -- otherwise
                // Tick is never called here and stamina freezes at whatever it was when you sat down.
                _stamina?.Tick(false);
            }
            else
            {
                // Movement relative to facing
                float h = Input.GetAxisRaw("Horizontal");
                float v = Input.GetAxisRaw("Vertical");
                Vector3 input = (transform.right * h + transform.forward * v);
                input.y = 0f;
                input = input.normalized;

                // Sprint requires three things, and the middle one matters: you must actually be MOVING.
                // Gating on input alone would drain stamina while standing still holding shift, which
                // reads as a bug. _stamina may be null (component not on the prefab) -> unlimited sprint,
                // so movement keeps working if stamina is removed or not yet wired up.
                bool moving = input.sqrMagnitude > 0.01f;
                bool sprinting = Input.GetKey(KeyCode.LeftShift) && moving
                                 && (_stamina == null || _stamina.CanSprint);

                IsSprinting = sprinting;
                StrafeInput = h;

                _stamina?.Tick(sprinting);

                float speed = sprinting ? sprintSpeed : walkSpeed;
                Vector3 targetVel = input * speed;   // where velocity WANTS to be this frame

                // Ramp toward the target instead of snapping to it. On the GROUND use acceleration when
                // speeding up and deceleration when slowing (compared by magnitude, so releasing the
                // stick or letting go of sprint both decelerate, and a hard direction change eases
                // through zero rather than pivoting instantly). In the AIR use a single much-lower rate
                // for both, so you keep your launch momentum and can only nudge it -- no free mid-air
                // acceleration or full steering.
                // Ground control counts if we're grounded OR still within the coyote window. Gating on
                // the RAW isGrounded made walking sluggish, because CharacterController.isGrounded drops
                // false for stray frames even on flat ground -- so those frames used the low air rate.
                // The coyote grace (refilled every grounded frame) bridges that flicker; only a REAL
                // jump/fall, where coyote has run out, falls to airAcceleration.
                bool groundControl = _controller.isGrounded || _coyoteTimer > 0f;
                float rate = groundControl
                    ? (targetVel.sqrMagnitude > _planarVel.sqrMagnitude ? acceleration : deceleration)
                    : airAcceleration;
                _planarVel = Vector3.MoveTowards(_planarVel, targetVel, rate * Time.deltaTime);

                // Gravity / grounding. Detect the LANDING transition BEFORE anything resets
                // _verticalVelocity, so the impact speed is the real downward velocity at touchdown.
                bool grounded = _controller.isGrounded;
                if (grounded && !_wasGrounded)
                    Landed?.Invoke(-_verticalVelocity);   // -vel because downward is negative
                _wasGrounded = grounded;

                // Coyote timer: full while grounded, counts down once you leave the ground.
                _coyoteTimer = grounded ? coyoteTime : _coyoteTimer - Time.deltaTime;

                // Jump: allowed while grounded OR within the coyote window. Launch velocity from height
                // and gravity (v = sqrt(2 g h)) so the apex is stable if gravity changes.
                bool jumped = false;
                if (_coyoteTimer > 0f && Input.GetKeyDown(jumpKey))
                {
                    _verticalVelocity = Mathf.Sqrt(2f * -gravity * jumpHeight);
                    _coyoteTimer = 0f;    // consume it, so you can't re-jump off the same window
                    jumped = true;
                }

                if (jumped)
                {
                    // velocity already set to the launch speed -- don't clamp or add gravity this frame
                }
                else if (grounded)
                {
                    if (_verticalVelocity < 0f) _verticalVelocity = -2f;
                }
                else
                {
                    // Heavier gravity on the way down than up -- the float-killer.
                    float g = gravity * (_verticalVelocity < 0f ? fallGravityMultiplier : 1f);
                    _verticalVelocity += g * Time.deltaTime;
                }

                Vector3 move = _planarVel + _externalVel;
                move.y = _verticalVelocity;

                _controller.Move(move * Time.deltaTime);

                // Bleed the external impulse off toward zero.
                _externalVel = Vector3.MoveTowards(_externalVel, Vector3.zero, externalDamping * Time.deltaTime);

                // Publish the planar (ground) speed so remotes can animate. Reads off the ramped
                // velocity, so the walk<->run blend now eases with the movement for free.
                PlanarSpeed = _planarVel.magnitude;
                _netSpeed.Value = PlanarSpeed;
            }
        }

        // --- EVERY MACHINE: drive the local Animator from the replicated Speed ---
        // This is OUTSIDE the IsOwner block on purpose — remotes apply the value they received.
        if (animator != null)
            animator.SetFloat("Speed", _netSpeed.Value, speedDampTime, Time.deltaTime);
    }
}