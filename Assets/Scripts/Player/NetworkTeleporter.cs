using Unity.Netcode;
using Unity.Netcode.Components;
using UnityEngine;

/// <summary>
/// Lives on the Player prefab. Lets the server move a player by telling the OWNER to
/// teleport (the player uses a client-authoritative ClientNetworkTransform, so only
/// the owner can move it and have it stick -- a server-side transform write gets
/// overwritten). Used for the fall-into-dungeon drop and the return-to-table.
///
/// Uses an owner-targeted universal RPC: the server calls TeleportRpc and it executes
/// on the owning client, which then uses NetworkTransform.Teleport (the interpolation-
/// safe move). Same fix pattern as PlayerSpawnPositioner.
/// </summary>
[RequireComponent(typeof(NetworkObject))]
public class NetworkTeleporter : NetworkBehaviour
{
    [Rpc(SendTo.Owner)]
    public void TeleportRpc(Vector3 position, Quaternion rotation)
    {
        // If we're being dropped from a seat, tell the SeatOccupant to let go NOW -- otherwise its
        // per-frame seat-follow (still seeing the not-yet-replicated Seated flag on a remote client)
        // would snap us straight back onto the fallen chair and we'd never land where we teleported.
        GetComponent<SeatOccupant>()?.OwnerReleaseForTeleport();

        // Unity's CharacterController owns the transform: if it's enabled when we move
        // the transform, its next Move() snaps the player straight back. Disable it
        // across the teleport so the new position sticks, then restore it (it adopts
        // the new spot on re-enable).
        var cc = GetComponent<CharacterController>();
        bool ccWas = cc != null && cc.enabled;
        if (cc != null)
            cc.enabled = false;

        var nt = GetComponent<NetworkTransform>();
        if (nt != null)
            nt.Teleport(position, rotation, transform.localScale);
        else
            transform.SetPositionAndRotation(position, rotation);

        if (cc != null)
            cc.enabled = ccWas;
    }
}
