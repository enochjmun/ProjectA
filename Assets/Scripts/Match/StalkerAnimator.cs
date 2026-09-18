using Unity.Netcode;
using UnityEngine;
using UnityEngine.AI;

/// <summary>
/// Drives the Stalker's Animator over the network, mirroring how PlayerMovement animates the player:
/// the SERVER (which runs the NavMeshAgent) publishes the monster's current speed as a NetworkVariable,
/// and EVERY client feeds that value into its local Animator's "Speed" float so the idle/walk/run blend
/// tree plays. Clients never run the agent -- they only see the synced transform -- so a replicated speed
/// value is how they know what animation to show.
///
/// The lunge/attack is a one-shot EVENT rather than a continuous value, so it goes through an RPC: when
/// StalkerAI fires a lunge on the server, it calls ServerPlayLunge(), which tells every client to pull the
/// Animator's "Lunge" trigger.
///
/// Lives on the Stalker root (beside StalkerAI, NavMeshAgent, NetworkObject, NetworkTransform). Runs on
/// ALL peers -- do NOT gate it to the server like StalkerAI, or clients would never animate.
/// </summary>
[RequireComponent(typeof(NetworkObject))]
public class StalkerAnimator : NetworkBehaviour
{
    [Tooltip("The model's Animator. Auto-found in children if left empty.")]
    [SerializeField] private Animator animator;

    [Tooltip("Float parameter the locomotion blend tree reads (match the player's convention).")]
    [SerializeField] private string speedParam = "Speed";

    [Tooltip("Trigger parameter that plays the attack/lunge state.")]
    [SerializeField] private string lungeTrigger = "Lunge";

    [Tooltip("Smoothing on the Speed value so idle<->walk<->run blends instead of snapping.")]
    [SerializeField] private float speedDampTime = 0.1f;

    private NavMeshAgent _agent;

    // Server writes the monster's speed; everyone reads it to feed their own Animator.
    private readonly NetworkVariable<float> _netSpeed = new NetworkVariable<float>(
        0f, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);

    private void Awake()
    {
        _agent = GetComponent<NavMeshAgent>();
        if (animator == null)
            animator = GetComponentInChildren<Animator>();
    }

    private void Update()
    {
        // Server: publish how fast the agent is actually moving.
        if (IsServer && _agent != null)
            _netSpeed.Value = _agent.velocity.magnitude;

        // Everyone (server included): drive the local Animator from the replicated speed, damped so the
        // blend tree eases between idle/walk/run instead of popping.
        if (animator != null)
            animator.SetFloat(speedParam, _netSpeed.Value, speedDampTime, Time.deltaTime);
    }

    /// <summary>Server-only entry point (called by StalkerAI when a lunge fires): play the attack on all clients.</summary>
    public void ServerPlayLunge()
    {
        if (IsServer)
            PlayLungeRpc();
    }

    // Sent to every client (and the host): pull the one-shot attack trigger. A trigger is the right tool
    // for a fire-once event -- it consumes itself when the transition is taken.
    [Rpc(SendTo.Everyone)]
    private void PlayLungeRpc()
    {
        if (animator != null)
            animator.SetTrigger(lungeTrigger);
    }
}
