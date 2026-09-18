using Unity.Netcode;
using UnityEngine;

/// <summary>
/// Owner-only placeholder readout that tells a fallen prey what the goal is: how many objectives are
/// cleared and whether the exits are open. Shows ONLY while you're actually down in the dungeon during
/// a chase (a lobby watcher or a seated player doesn't need it). Reads the replicated state on
/// DungeonObjective, so it stays in sync across the team.
///
/// SETUP: add this component to the Player prefab (no serialized refs). Throwaway IMGUI -- real UI later.
/// </summary>
public class ObjectiveHud : NetworkBehaviour
{
    [Tooltip("Prompt size + vertical offset from the top. Drawn horizontally centered (top-centre).")]
    [SerializeField] private Rect rect = new Rect(0f, 70f, 380f, 26f);

    private void OnGUI()
    {
        if (!IsOwner) return;

        var obj = DungeonObjective.Instance;
        var mc = MatchController.Instance;
        var dungeon = DungeonGenerator.Instance;
        if (obj == null || mc == null || dungeon == null) return;

        // Only for a player actually in the dungeon during a chase (i.e. the prey).
        if (!mc.ChaseActive || !dungeon.WorldBounds.Contains(transform.position)) return;

        bool done = obj.IsComplete.Value;
        string line = done
            ? "EXITS OPEN -- find an exit and run!"
            : $"Objectives: {obj.ClearedIds.Count}/{obj.ObjectiveCount.Value}    ·    EXITS LOCKED";

        var style = new GUIStyle(GUI.skin.label)
        {
            alignment = TextAnchor.MiddleCenter,
            fontSize = 15
        };
        style.normal.textColor = done ? new Color(0.3f, 1f, 0.4f) : new Color(1f, 0.8f, 0.3f);

        var r = new Rect((Screen.width - rect.width) * 0.5f, rect.y, rect.width, rect.height);
        GUI.Label(r, line, style);
    }
}
