using System.Collections;
using Unity.Netcode;
using UnityEngine;

/// <summary>
/// The player's dungeon flashlight. A fallen player lands in the DARK; a few beats later their
/// flashlight tumbles down the shaft after them and lands nearby. They go find it, pick it up, and
/// THEN the held beam turns on (the light "moves" from the floor into their hand). Reset on return.
///
/// Server-authoritative: the held-on state is a NetworkVariable so every client sees every player's
/// beam (like Lethal Company). The held light itself is a Spot Light you place as a child of the
/// player's aim/head transform (so its beam aims with look and replicates) -- this script only
/// toggles it, never moves it.
/// </summary>
public class FlashlightController : NetworkBehaviour
{
    [Tooltip("A Spot Light child on the player's aim/head transform (so the beam aims with look and " +
             "replicates to other clients). This script only enables/disables it.")]
    [SerializeField] private Light heldLight;
    [Tooltip("Networked prefab dropped at the player's feet when the flashlight falls " +
             "(NetworkObject + Light + Rigidbody + Collider + server NetworkTransform).")]
    [SerializeField] private GameObject droppedFlashlightPrefab;
    [Tooltip("Seconds after the player lands before their flashlight tumbles down the shaft after them.")]
    [SerializeField] private float dropDelay = 2f;
    [Tooltip("How high above the player it spawns, so it falls in from the shaft above (needs a Rigidbody to fall).")]
    [SerializeField] private float dropHeight = 6f;
    [Tooltip("Random tumble spin (rad/s) applied to the dropped flashlight so it rolls/tumbles as it falls.")]
    [SerializeField] private float tumbleSpin = 8f;

    // Server-write, everyone-read -> drives heldLight.enabled on every client so beams are shared.
    private readonly NetworkVariable<bool> _heldOn = new NetworkVariable<bool>(
        false, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);

    private NetworkObject _dropped;      // server handle so we can despawn it on return
    private Coroutine _dropRoutine;

    public override void OnNetworkSpawn()
    {
        _heldOn.OnValueChanged += OnHeldChanged;
        ApplyHeld(_heldOn.Value);
    }

    public override void OnNetworkDespawn() => _heldOn.OnValueChanged -= OnHeldChanged;

    private void OnHeldChanged(bool _, bool now) => ApplyHeld(now);
    private void ApplyHeld(bool on) { if (heldLight != null) heldLight.enabled = on; }

    /// <summary>Server: the player just landed in the dungeon IN THE DARK. After a beat, their
    /// flashlight tumbles down the shaft after them and lands nearby, to be found and picked up.</summary>
    public void ServerEnterDungeon()
    {
        if (!IsServer) return;
        _heldOn.Value = false;                                  // they land WITHOUT a beam
        if (_dropRoutine != null) StopCoroutine(_dropRoutine);
        _dropRoutine = StartCoroutine(DropFlashlightAfterDelay());
    }

    private IEnumerator DropFlashlightAfterDelay()
    {
        yield return new WaitForSeconds(dropDelay);
        if (droppedFlashlightPrefab == null)
        {
            Debug.LogWarning("[FlashlightController] droppedFlashlightPrefab not assigned -- nothing falls.", this);
            yield break;
        }
        // Spawn as high as we can WITHOUT clipping the ceiling: the dungeon has a solid roof, so a
        // naive dropHeight would spawn ABOVE it and stick on the roof collider. Raycast up from over
        // the player and drop from just under whatever we hit; open above -> use the full dropHeight.
        Vector3 origin = transform.position + Vector3.up * 2.2f;   // above the player's head
        float rise = dropHeight;
        if (Physics.Raycast(origin, Vector3.up, out var ceil, dropHeight, ~0, QueryTriggerInteraction.Ignore))
            rise = Mathf.Max(0.5f, ceil.distance - 0.4f);          // stop just below the ceiling

        Vector3 spawn = origin + Vector3.up * rise
            + new Vector3(Random.Range(-0.6f, 0.6f), 0f, Random.Range(-0.6f, 0.6f));
        var go = Instantiate(droppedFlashlightPrefab, spawn, Random.rotation);
        _dropped = go.GetComponent<NetworkObject>();
        _dropped?.Spawn();                                         // networked -> everyone sees it tumble down

        // Give it a random tumble as it falls. The server owns the physics sim (server NetworkTransform
        // replicates the motion), so setting angularVelocity here shows the roll on every client.
        if (tumbleSpin > 0f && go.TryGetComponent<Rigidbody>(out var rb))
            rb.angularVelocity = Random.insideUnitSphere * tumbleSpin;

        _dropRoutine = null;
    }

    /// <summary>Server: the player left the dungeon -- kill the beam and clean up the dropped light.</summary>
    public void ServerReturnToLobby()
    {
        if (!IsServer) return;
        if (_dropRoutine != null) { StopCoroutine(_dropRoutine); _dropRoutine = null; }
        _heldOn.Value = false;
        if (_dropped != null && _dropped.IsSpawned) _dropped.Despawn();
        _dropped = null;
    }

    /// <summary>DEV ONLY: force the held beam on without a pickup. Used by DungeonDebugJump so you
    /// can actually see when you drop straight into the dungeon for art iteration. Server only.</summary>
    public void ServerDebugBeamOn()
    {
        if (!IsServer) return;
        _heldOn.Value = true;
    }

    /// <summary>Owner-side (from FlashlightPickup.Interact): ask the server to give this player the
    /// beam back and remove the dropped flashlight they walked into.</summary>
    public void RequestPickUp(NetworkObject dropped) => PickUpRpc(dropped);

    [Rpc(SendTo.Server)]
    private void PickUpRpc(NetworkObjectReference droppedRef)
    {
        _heldOn.Value = true;                                   // beam back on (does NOT re-arm the auto-drop)
        if (droppedRef.TryGet(out var dropped) && dropped.IsSpawned)
            dropped.Despawn();
    }
}
