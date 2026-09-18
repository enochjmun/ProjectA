using Unity.Netcode;
using UnityEngine;

public class PlayerCamera : NetworkBehaviour
{
    [SerializeField] private Camera playerCamera;        // child camera on the prefab
    [SerializeField] private float mouseSensitivity = 2f;
    [SerializeField] private float minPitch = -80f;       // how far you can look down
    [SerializeField] private float maxPitch = 80f;        // how far you can look up
    [SerializeField] private GameObject noseObject;
    private float _pitch;

    /// <summary>The current look pitch in degrees. CameraFeel reads this to rebuild the camera's
    /// rotation as (pitch * feel) each LateUpdate, instead of multiplying onto the live rotation
    /// (which would accumulate its own offset every frame).</summary>
    public float Pitch => _pitch;
    /// <summary>The camera transform CameraFeel layers its position/rotation offsets onto.</summary>
    public Transform CameraTransform => playerCamera != null ? playerCamera.transform : null;
        

    public override void OnNetworkSpawn()
    {
        bool owner = IsOwner;

        // Owner-only camera & listener (the gating you already had)
        if (playerCamera != null)
        {
            playerCamera.enabled = owner;
            var listener = playerCamera.GetComponent<AudioListener>();
            if (listener != null) listener.enabled = owner;
        }

        // inside OnNetworkSpawn, after you've established 'owner'
       
        // Non-owners don't run camera logic at all
        enabled = owner;
    }

    private void Update()
    {
        if (!IsOwner || playerCamera == null) return;

        // Only look while the cursor is LOCKED. A freed cursor means a mini-game
        // UI is up and the mouse is pointing at cards, not turning the head --
        // without this, moving the cursor to pick a card also pitches the camera.
        if (Cursor.lockState != CursorLockMode.Locked) return;

        // --- Camera pitch: mouse Y tilts ONLY the camera, not the body (stays local) ---
        float mouseY = Input.GetAxisRaw("Mouse Y") * mouseSensitivity;
        _pitch -= mouseY;                                  // subtract so up = look up
        _pitch = Mathf.Clamp(_pitch, minPitch, maxPitch);
        playerCamera.transform.localRotation = Quaternion.Euler(_pitch, 0f, 0f);
    }

    /// <summary>
    /// Snap the first-person view to face a world point, ONCE. Used by mini-games
    /// that free the cursor (e.g. Old Maid on your turn): while the cursor is free,
    /// Update() early-returns and stops taking mouse input, so if you happened to be
    /// facing away from the cards you'd be stuck. Calling this at turn-start aims you
    /// at them.
    ///
    /// Sets body yaw (root rotation) directly, same transform PlayerMovement yaws, and
    /// sets the stored _pitch so the aim SURVIVES the later cursor re-lock (Update
    /// resumes pitch from _pitch, not from the current transform -- writing only the
    /// transform would snap back the instant look resumes).
    /// </summary>
    public void SnapLookAt(Vector3 worldPoint)
    {
        if (playerCamera == null) return;

        Vector3 toTarget = worldPoint - playerCamera.transform.position;
        Vector3 flat = new Vector3(toTarget.x, 0f, toTarget.z);   // horizontal component only

        // Body yaw: turn the whole capsule to face the target on the ground plane.
        // LookRotation(flat, up) has zero pitch/roll, so the body only yaws -- exactly
        // what mouse X normally does. Replicates via ClientNetworkTransform like normal look.
        if (flat.sqrMagnitude > 0.0001f)
            transform.rotation = Quaternion.LookRotation(flat, Vector3.up);

        // Camera pitch: aim up/down at the target. Negative _pitch = looking up (matches
        // Update's "subtract so up = look up"), hence the leading minus.
        float pitch = -Mathf.Atan2(toTarget.y, flat.magnitude) * Mathf.Rad2Deg;
        _pitch = Mathf.Clamp(pitch, minPitch, maxPitch);
        playerCamera.transform.localRotation = Quaternion.Euler(_pitch, 0f, 0f);
    }
}