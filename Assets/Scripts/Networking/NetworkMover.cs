using Unity.Netcode;
using UnityEngine;

/// <summary>
/// Proof-of-replication test object. Put this on a cube that ALSO has a
/// NetworkObject + NetworkTransform component. Only the server moves it;
/// NetworkTransform syncs that movement to every client.
///
/// If the cube moves on the OTHER machine when you drive it on the host,
/// your NGO-over-Steam pipe works. That is the whole goal of the spike.
/// </summary>
public class NetworkMover : NetworkBehaviour
{
    [SerializeField] float speed = 4f;

    void Update()
    {
        if (!IsServer) return;  // server owns movement; clients only receive it

        float h = Input.GetAxis("Horizontal"); // A/D or arrow keys
        float v = Input.GetAxis("Vertical");   // W/S or arrow keys
        transform.position += new Vector3(h, 0f, v) * speed * Time.deltaTime;
    }
}
