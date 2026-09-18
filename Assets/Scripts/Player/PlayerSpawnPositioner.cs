using System.Collections;
using System.Linq;
using Unity.Netcode;
using Unity.Netcode.Components;
using UnityEngine;

/// <summary>
/// Lives on the Player prefab. Places the OWNING client at a lobby SpawnPoint on spawn, chosen by client
/// id so two players never land on the same one.
///
/// Two modes:
///  - NO JoinBatcher in the scene → place + wake immediately (the original flow).
///  - JoinBatcher present → the elevator OWNS placement. We do NOT place at the lobby (that placement was
///    the split-second lobby flash before the elevator boarded you). Instead we hold BLACK + locked until
///    the ElevatorRider boards us, which positions us in the car and wakes us (releasing the blackout).
///
/// The blackout is begun in OnNetworkSpawn, BEFORE the placement coroutine's first frame — so the spawn,
/// placement and boarding all happen behind black and never render the lobby.
///
/// Placement isn't a raw transform write: the player uses a client-authoritative ClientNetworkTransform,
/// so a write on the spawn frame gets clobbered by the spawn-frame sync — we wait a frame and use
/// NetworkTransform.Teleport (the supported owner-auth move).
/// </summary>
public class PlayerSpawnPositioner : NetworkBehaviour
{
    public override void OnNetworkSpawn()
    {
        if (!IsOwner) return;

        // Go black from the very first frame — covers spawn, placement and the wait for the elevator to
        // board us. The wake-up (ElevatorRider.BeginRide, or the fallbacks below) releases it and fades in.
        GetComponent<ScreenFeedback>()?.BeginBlackout();
        StartCoroutine(PlaceWhenReady());
    }

    private IEnumerator PlaceWhenReady()
    {
        // Let ClientNetworkTransform / movement finish their spawn-frame setup first.
        yield return null;

        bool batched = FindObjectOfType<JoinBatcher>() != null;

        if (batched)
        {
            // Elevator owns placement. Do NOT place at the lobby (that was the visible flash) — just hold
            // locked + black until the ElevatorRider boards us, which positions us in the car and wakes us.
            var mv = GetComponent<PlayerMovement>();
            if (mv != null) mv.MovementLocked = true;

            var rider = GetComponent<ElevatorRider>();
            float waited = 0f;
            while (waited < 5f)
            {
                if (rider != null && rider.ActivelyRiding) yield break;   // ElevatorRider owns us; its wake clears the blackout
                waited += Time.deltaTime;
                yield return null;
            }

            // Never boarded within a few seconds → the ride isn't wired (elevator / anchors / ElevatorRider).
            // Fall back to a normal lobby spawn + wake so a misconfig can't leave us stuck black-and-locked.
            Debug.LogWarning("[PlayerSpawnPositioner] JoinBatcher present but never boarded (ElevatorRider / " +
                             "rider anchors / ElevatorController not wired?) — falling back to a normal spawn.", this);
            PlaceAtLobbySpawn();
            if (mv != null) mv.MovementLocked = false;
            GetComponent<ScreenFeedback>()?.PlayWakeUp();   // clears the blackout + fades in
            yield break;
        }

        // --- No batcher: place at a lobby SpawnPoint and wake (fades in from the blackout). ---
        PlaceAtLobbySpawn();
        GetComponent<ScreenFeedback>()?.PlayWakeUpAfterLoad();
    }

    // Place the owner at a lobby SpawnPoint, chosen by client id so two players never share one.
    private void PlaceAtLobbySpawn()
    {
        var points = FindObjectsByType<SpawnPoint>(FindObjectsSortMode.None)
            .OrderBy(p => p.name)
            .ToArray();

        if (points.Length == 0)
        {
            Debug.LogWarning("[PlayerSpawnPositioner] No SpawnPoint objects in the scene.", this);
            return;
        }

        var point = points[(int)(OwnerClientId % (ulong)points.Length)];
        var netTransform = GetComponent<NetworkTransform>();
        if (netTransform != null)
            netTransform.Teleport(point.transform.position, point.transform.rotation, transform.localScale);
        else
            transform.SetPositionAndRotation(point.transform.position, point.transform.rotation);
    }
}
