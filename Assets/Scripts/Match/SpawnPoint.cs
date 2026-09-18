using UnityEngine;

/// <summary>
/// Marker for a lobby spawn location. LobbyGenerator creates a ring of these;
/// PlayerSpawnPositioner places each connecting player at one. Pure marker --
/// no logic, just a transform with a gizmo so you can see/move them in-scene.
/// </summary>
public class SpawnPoint : MonoBehaviour
{
    private void OnDrawGizmos()
    {
        Gizmos.color = Color.cyan;
        Gizmos.DrawWireSphere(transform.position + Vector3.up * 1.0f, 0.4f);
        Gizmos.DrawLine(transform.position, transform.position + transform.forward * 1.0f);
    }
}
