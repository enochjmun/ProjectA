using System.Collections;
using Unity.Netcode;
using UnityEngine;

/// <summary>
/// DEV TOOL: jump the local player straight into the dungeon with a working beam, bypassing the
/// whole lobby -> ready-up -> minigame -> fall flow.
///
/// Built for art/lighting iteration, where you need to stand in the REAL dungeon -- correct
/// per-area post grade, real volumetric fog, real generated layout, and the owner-only player glow
/// (PlayerLocalGlow keys off DungeonGenerator.WorldBounds, so it switches on automatically once
/// you're inside). A hand-built test scene can't give you that.
///
/// Flow: owner presses the key -> server RPC -> server generates, waits for the geometry and the
/// runtime NavMesh bake to settle, picks a spawn, teleports the player, switches the beam on.
/// Same owner-side request pattern as SpectatorController.RequestSpectate.
///
/// PUT THIS ON THE PLAYER PREFAB. Strip it (or #if UNITY_EDITOR gate it) before shipping.
/// </summary>
public class DungeonDebugJump : NetworkBehaviour
{
    [Tooltip("Press to generate the dungeon (if needed) and drop straight into it.")]
    [SerializeField] private KeyCode jumpKey = KeyCode.F10;
    [Tooltip("Seconds to wait after generating so the geometry and runtime NavMesh bake settle before you stand on it.")]
    [SerializeField] private float generateWait = 0.75f;
    [Tooltip("Reroll a fresh layout on every jump. OFF = only generate when no dungeon exists yet, so you can leave and re-enter the same one.")]
    [SerializeField] private bool regenerateEachJump = true;
    [Tooltip("Switch the held flashlight beam on so you can actually see down there.")]
    [SerializeField] private bool giveBeam = true;

    private void Update()
    {
        if (!IsOwner) return;
        if (Input.GetKeyDown(jumpKey)) RequestJumpRpc();
    }

    // Owner asks; the server owns generation, spawn selection and the teleport.
    [Rpc(SendTo.Server)]
    private void RequestJumpRpc() => StartCoroutine(JumpRoutine());

    private IEnumerator JumpRoutine()
    {
        var spawn = DungeonManager.Instance != null ? DungeonManager.Instance.GetRandomSpawn() : null;

        // Generate when asked to, or when there's simply no dungeon to jump into yet.
        if (regenerateEachJump || spawn == null)
        {
            if (DungeonGenerator.Instance == null)
            {
                Debug.LogWarning("[DungeonDebugJump] No DungeonGenerator in the scene -- nothing to jump into.", this);
                yield break;
            }

            DungeonGenerator.Instance.ServerGenerate();
            yield return new WaitForSeconds(generateWait);   // let geometry + the navmesh bake settle
            spawn = DungeonManager.Instance != null ? DungeonManager.Instance.GetRandomSpawn() : null;
        }

        if (spawn == null)
        {
            Debug.LogWarning("[DungeonDebugJump] No dungeon spawn points found after generating.", this);
            yield break;
        }

        // Owner-targeted teleport -- the client-authoritative transform means the owner must move itself.
        GetComponent<NetworkTeleporter>()?.TeleportRpc(spawn.position, spawn.rotation);

        if (giveBeam) GetComponent<FlashlightController>()?.ServerDebugBeamOn();
    }
}
