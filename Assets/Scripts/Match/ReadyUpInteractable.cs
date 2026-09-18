using UnityEngine;

/// <summary>
/// A world object (needs a Collider) that toggles the interacting player's lobby
/// ready state -- the real replacement for the temporary R-key control in
/// MatchController. Place one in the lobby room (a panel, a button, the table
/// edge) and players ready up by looking at it and pressing the interact key.
///
/// Plain MonoBehaviour: the networking lives in PlayerState.SetReadyServerRpc,
/// which the interacting player owns and is allowed to call.
/// </summary>
[RequireComponent(typeof(Collider))]
public class ReadyUpInteractable : MonoBehaviour, IInteractable
{
    public string GetPrompt(PlayerInteractor interactor)
    {
        var player = interactor.PlayerState;
        bool ready = player != null && player.IsReady.Value;
        return ready ? "Cancel ready" : "Ready up";
    }

    public void Interact(PlayerInteractor interactor)
    {
        // No readying/unreadying mid-round -- you can't leave your chair during a minigame/chase.
        var mc = MatchController.Instance;
        if (mc != null && !mc.ReadyUpAllowed) return;

        var player = interactor.PlayerState;
        if (player != null)
            player.SetReadyServerRpc(!player.IsReady.Value);
    }
}
