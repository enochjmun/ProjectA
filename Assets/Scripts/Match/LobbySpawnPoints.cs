using UnityEngine;

/// <summary>
/// Generates several lobby <see cref="SpawnPoint"/> markers spread in a row, so multiple players arriving
/// at the elevator don't all stack on one point. Replaces the old single hand-placed spawn (and the
/// deleted LobbyGenerator's ring).
///
/// SETUP: put this on an empty at the elevator mouth (or on the Elevator prop), facing INTO the lobby.
/// The row spreads along this object's local X and the points inherit its facing, so players spawn
/// looking the right way. Tune count/spacing; the gizmo previews the positions in-editor without
/// creating anything. The actual markers are generated at runtime.
/// </summary>
public class LobbySpawnPoints : MonoBehaviour
{
    [Tooltip("How many spawn points to create. Make this >= your max lobby player count.")]
    [SerializeField] private int count = 4;
    [Tooltip("Metres between adjacent points along the row.")]
    [SerializeField] private float spacing = 1.2f;
    [Tooltip("Local axis the row spreads along (default = local X / sideways across the elevator mouth).")]
    [SerializeField] private Vector3 rowAxis = Vector3.right;

    private void Awake() => Generate();

    private void Generate()
    {
        Vector3 axis = rowAxis.sqrMagnitude > 0.0001f ? rowAxis.normalized : Vector3.right;
        float half = (count - 1) * 0.5f;
        for (int i = 0; i < count; i++)
        {
            var go = new GameObject($"LobbySpawn_{i}");
            go.transform.SetParent(transform, false);
            go.transform.localPosition = axis * ((i - half) * spacing);
            go.transform.localRotation = Quaternion.identity;   // inherit this object's facing (into the lobby)
            go.AddComponent<SpawnPoint>();
        }
    }

    // Editor preview of where the points will be, so you can place/aim this object without running.
    private void OnDrawGizmos()
    {
        Vector3 axis = rowAxis.sqrMagnitude > 0.0001f ? rowAxis.normalized : Vector3.right;
        float half = (count - 1) * 0.5f;
        Gizmos.color = new Color(0.3f, 0.9f, 1f, 0.8f);
        for (int i = 0; i < count; i++)
        {
            Vector3 p = transform.TransformPoint(axis * ((i - half) * spacing));
            Gizmos.DrawWireSphere(p + Vector3.up, 0.35f);
            Gizmos.DrawLine(p, p + transform.forward);   // facing
        }
    }
}
