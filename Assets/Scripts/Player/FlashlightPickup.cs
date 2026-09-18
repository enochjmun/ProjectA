using Unity.Netcode;
using UnityEngine;

/// <summary>
/// Makes a dropped flashlight pickup-able, on the same IInteractable path as ReadyUpInteractable:
/// the owner looks at it and presses interact, and their FlashlightController asks the server to
/// re-light their beam and despawn this object. Put this on the dropped-flashlight prefab (it already
/// has the Collider the interact raycast needs and the NetworkObject the server despawns).
/// </summary>
[RequireComponent(typeof(Collider))]
public class FlashlightPickup : MonoBehaviour, IInteractable
{
    public string GetPrompt(PlayerInteractor interactor) => "Pick up flashlight";

    public void Interact(PlayerInteractor interactor)
    {
        var fc = interactor.GetComponentInParent<FlashlightController>();
        var self = GetComponentInParent<NetworkObject>();   // works even if this sits on a child of the prefab
        if (fc != null && self != null)
            fc.RequestPickUp(self);   // owner-side -> fires the server RPC the player is allowed to make
    }
}
