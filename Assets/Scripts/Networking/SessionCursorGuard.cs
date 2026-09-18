using Unity.Netcode;
using UnityEngine;

/// <summary>
/// Restores the mouse cursor whenever THIS machine's netcode session stops -- so a
/// client whose host disconnected (or anyone who leaves a session) isn't left with a
/// locked, invisible cursor and no way to click the reconnect UI without restarting
/// the game.
///
/// Why this is needed: the cursor is locked+hidden by PlayerMovement.OnNetworkSpawn
/// (FPS look) and freed only transiently by the mini-game OnGUI scripts. When the
/// session tears down, every networked Player object DESPAWNS, so whatever script was
/// managing the cursor is gone -- leaving it stuck in its last state (Locked/hidden if
/// you were in the lobby or FP view). Nothing else restores it. This component isn't a
/// NetworkBehaviour and lives on a persistent scene object (the NetworkManager), so it
/// survives the despawn and can put the cursor back.
///
/// OnClientStopped fires on the local NetworkManager stopping for BOTH host (wasHost =
/// true) and client (wasHost = false), including the case where a pure client shuts
/// down after losing the host -- so it's the single reliable hook here.
///
/// SETUP: add this component to the NetworkManager GameObject (or any object that
/// persists across the session, e.g. the same object TransportSwitcherUI lives on).
/// No serialized references to wire.
///
/// This only restores the CURSOR (enough to use the IMGUI reconnect UI). When a real
/// main-menu scene exists, this is also the natural place to trigger a scene reload /
/// "host lost" screen instead of dropping back into the dead session scene.
/// </summary>
public class SessionCursorGuard : MonoBehaviour
{
    private void OnEnable()
    {
        if (NetworkManager.Singleton != null)
            NetworkManager.Singleton.OnClientStopped += HandleSessionStopped;
    }

    private void OnDisable()
    {
        if (NetworkManager.Singleton != null)
            NetworkManager.Singleton.OnClientStopped -= HandleSessionStopped;
    }

    private void HandleSessionStopped(bool wasHost)
    {
        // Free the cursor so the reconnect UI is usable again. Unlocked + visible is
        // the correct state for menu/IMGUI interaction; the next Player spawn re-locks
        // it for FPS via PlayerMovement.OnNetworkSpawn.
        Cursor.lockState = CursorLockMode.None;
        Cursor.visible = true;
    }
}
