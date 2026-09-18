using Unity.Netcode;
using UnityEngine;

/// <summary>
/// On the Player prefab. The OWNER detects entering a DungeonExit trigger and reports
/// an escape to the server, which resolves the chase for that player.
///
/// Detection is owner-side on purpose: the owner moves via CharacterController.Move,
/// so trigger callbacks fire on their machine. The server's copy of a remote player is
/// moved by ClientNetworkTransform (a raw transform sync, not CharacterController.Move),
/// so server-side OnTriggerEnter is unreliable for it -- hence detect on the owner,
/// then RPC to the server.
/// </summary>
public class ChaseEscapeReporter : NetworkBehaviour
{
    private void OnTriggerEnter(Collider other)
    {
        if (!IsOwner)
            return;

        // Reached an exit? Report its id (single-use claim happens on the server).
        var exit = other.GetComponentInParent<DungeonExit>();
        if (exit != null) { ReportExitRpc(exit.Id); return; }

        // Cleared a central objective trigger? Report its id (team unlock accrues on the server).
        var objective = other.GetComponentInParent<ObjectiveTrigger>();
        if (objective != null) { ReportObjectiveRpc(objective.Id); return; }
    }

    // A fallen player cleared objective trigger `id`. DungeonObjective unlocks the exits for EVERYONE
    // once every trigger has been cleared (co-op team unlock).
    [Rpc(SendTo.Server)]
    private void ReportObjectiveRpc(int id)
    {
        DungeonObjective.Instance?.ServerClearObjective(id);
    }

    // A fallen player reached exit `id`. It counts only if the objective is complete AND this exact
    // exit hasn't been used yet -- single-use, claimed atomically on the server. No DungeonObjective in
    // the scene -> treat as always open (backwards compatible).
    [Rpc(SendTo.Server)]
    private void ReportExitRpc(int id)
    {
        var objective = DungeonObjective.Instance;
        if (objective != null && !objective.ServerTryClaimExit(id))
            return;   // exits locked (objective incomplete) or this door already claimed

        MatchController.Instance?.ReportEscape(GetComponent<PlayerState>());
    }
}
