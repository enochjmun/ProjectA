using System.Globalization;
using Unity.Netcode;
using UnityEngine;

public class HeadLookIK : NetworkBehaviour
{
    [SerializeField] private Animator animator;
    [SerializeField] private Transform lookSource;   // owner's camera (for reading pitch locally)
    [SerializeField] private Transform headOrigin;   // a point at head height to look FROM (e.g. the camera, or a head bone/empty)
    [SerializeField] private float lookDistance = 10f;
    [SerializeField] private float lookWeight = 1f;
    [SerializeField] private float bodyWeight = 0.3f;
    [SerializeField] private float headWeight = 1f;
    [SerializeField] private float clampWeight = 0.5f;

    // The pitch angle, replicated: owner writes it, everyone reads it.
    private NetworkVariable<float> _netPitch = new NetworkVariable<float>(0f, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Owner);

    private void Update()
    {
        // Owner publishes its current pitch each frame so remotes can reconstruct the look.
        if (IsOwner && lookSource != null)
        {   
            // localEulerAngles.x is the camera's pitch. Convert 0..360 to -180..180 so "up" is negative, "down" positive.
            float pitch = lookSource.localEulerAngles.x;
            if (pitch > 180f) pitch -= 360f;
            _netPitch.Value = pitch;
        }
    }

    private void OnAnimatorIK(int layerIndex)
    {
        if (animator == null) return;

        // Build a look direction from the BODY's facing (already replicated via yaw)
        // tilted by the replicated pitch. This works the same on owner and remote machines.
        Vector3 origin = headOrigin != null ? headOrigin.position : transform.position + Vector3.up * 1.6f;

        // body forward, then rotate it around the body's right axis by the pitch
        Quaternion pitchRot = Quaternion.AngleAxis(_netPitch.Value, transform.right);
        Vector3 lookDir = pitchRot * transform.forward;

        Vector3 lookTarget = origin + lookDir * lookDistance;

        animator.SetLookAtWeight(lookWeight, bodyWeight, headWeight, 0f, clampWeight);
        animator.SetLookAtPosition(lookTarget);
    }
}