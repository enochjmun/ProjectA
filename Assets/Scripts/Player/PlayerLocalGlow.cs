using Unity.Netcode;
using UnityEngine;

/// <summary>
/// Lethal-Company-style personal footing light. A dim point light childed to the player that gives
/// the OWNER a small pool of visibility in the pitch-black dungeon.
///
/// Key design points (see the flashlight/ambience design notes):
/// - OWNER-ONLY: the light is enabled only on the owning client, so each player sees only their OWN
///   glow. No one else -- including the monster -- ever perceives it, which keeps it a pure
///   quality-of-life aid and OUT of the light-detection the flashlight feeds. It physically cannot
///   betray you.
/// - IN-DUNGEON ONLY: it only lights up while the player is inside the generated dungeon
///   (DungeonGenerator.WorldBounds), so it stays dark in the lobby.
/// - It's a real Unity point light (lights by normals -> reveals form), kept small and dim on purpose
///   so it never makes the flashlight redundant.
/// </summary>
public class PlayerLocalGlow : NetworkBehaviour
{
    [Tooltip("The dim point light childed to the player (the personal footing glow).")]
    [SerializeField] private Light glowLight;

    public override void OnNetworkSpawn()
    {
        // Every client instantiates a copy of this player prefab (including remote players' avatars on
        // MY machine). Only the OWNER should ever render their own glow, so on every non-owned copy we
        // switch the light off for good and stop ticking Update.
        if (!IsOwner)
        {
            if (glowLight != null) glowLight.enabled = false;
            enabled = false;
            return;
        }

        // Owner starts dark; Update turns the glow on once inside the dungeon.
        if (glowLight != null) glowLight.enabled = false;
    }

    private void Update()
    {
        if (glowLight == null) return;

        // Lit ONLY while inside the generated dungeon -> automatically dark in the lobby. Uses the
        // player's own transform (not Camera.main), so it's immune to the camera-tag issue the grade hit.
        bool inDungeon = DungeonGenerator.Instance != null
            && DungeonGenerator.Instance.WorldBounds.Contains(transform.position);

        if (glowLight.enabled != inDungeon)
            glowLight.enabled = inDungeon;
    }
}
