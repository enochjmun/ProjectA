using UnityEngine;

/// <summary>
/// Marker for a dungeon landing point. DungeonManager collects these and a faller
/// drops at a random one. Random for now; the smarter placement (far-apart guarantee
/// for multi-loser, §5.1) and a procedural-dungeon generator that spawns these come
/// later -- both plug into DungeonManager without touching the fall logic.
/// </summary>
public class DungeonSpawnPoint : MonoBehaviour
{
    private void OnDrawGizmos()
    {
        Gizmos.color = new Color(1f, 0.3f, 0.2f);
        Gizmos.DrawWireSphere(transform.position + Vector3.up * 1.0f, 0.4f);
        Gizmos.DrawLine(transform.position, transform.position + transform.forward * 1.0f);
    }
}
