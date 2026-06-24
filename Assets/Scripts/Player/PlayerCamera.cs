using Unity.Netcode;
using UnityEngine;

public class PlayerCamera : NetworkBehaviour
{
    [SerializeField] private Camera playerCamera;        // child camera on the prefab
    [SerializeField] private float mouseSensitivity = 2f;
    [SerializeField] private float minPitch = -80f;       // how far you can look down
    [SerializeField] private float maxPitch = 80f;        // how far you can look up

    private float _pitch;

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
        if (owner && playerCamera != null)
        {
            int hiddenLayer = LayerMask.NameToLayer("OwnerHidden");
            if (hiddenLayer >= 0)
                playerCamera.cullingMask &= ~(1 << hiddenLayer);
        }
        // Non-owners don't run camera logic at all
        enabled = owner;
    }

    private void Update()
    {
        if (!IsOwner || playerCamera == null) return;

        // --- Camera pitch: mouse Y tilts ONLY the camera, not the body (stays local) ---
        float mouseY = Input.GetAxisRaw("Mouse Y") * mouseSensitivity;
        _pitch -= mouseY;                                  // subtract so up = look up
        _pitch = Mathf.Clamp(_pitch, minPitch, maxPitch);
        playerCamera.transform.localRotation = Quaternion.Euler(_pitch, 0f, 0f);
    }
}