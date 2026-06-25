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

    [Header("Animation")]
    [SerializeField] private Animator animator;          // Animator on the child model
    [SerializeField] private float speedDampTime = 0.1f; // smooths idle<->walk<->run blending

    private CharacterController _controller;
    private float _verticalVelocity;

    // Owner writes Speed; everyone reads it and feeds their OWN local Animator.
    private NetworkVariable<float> _netSpeed = new NetworkVariable<float>(
        0f, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Owner);

    private void Awake()
    {
        _controller = GetComponent<CharacterController>();
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

    private void Update()
    {
        // --- OWNER-ONLY: input, movement, and publishing the Speed value ---
        if (IsOwner)
        {
            // Body yaw (mouse X rotates the whole capsule; replicates via ClientNetworkTransform)
            float mouseX = Input.GetAxisRaw("Mouse X") * mouseSensitivity;
            transform.Rotate(Vector3.up, mouseX);

            // Movement relative to facing
            float h = Input.GetAxisRaw("Horizontal");
            float v = Input.GetAxisRaw("Vertical");
            Vector3 input = (transform.right * h + transform.forward * v);
            input.y = 0f;
            input = input.normalized;

            float speed = Input.GetKey(KeyCode.LeftShift) ? sprintSpeed : walkSpeed;
            Vector3 move = input * speed;

            // Gravity / grounding
            if (_controller.isGrounded)
            {
                if (_verticalVelocity < 0f) _verticalVelocity = -2f;
            }
            else
            {
                _verticalVelocity += gravity * Time.deltaTime;
            }
            move.y = _verticalVelocity;

            _controller.Move(move * Time.deltaTime);

            // Publish the planar (ground) speed so remotes can animate.
            float planarSpeed = new Vector2(move.x, move.z).magnitude;
            _netSpeed.Value = planarSpeed;
        }

        // --- EVERY MACHINE: drive the local Animator from the replicated Speed ---
        // This is OUTSIDE the IsOwner block on purpose — remotes apply the value they received.
        if (animator != null)
            animator.SetFloat("Speed", _netSpeed.Value, speedDampTime, Time.deltaTime);
    }
}