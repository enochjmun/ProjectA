using Unity.Netcode;
using UnityEngine;

/// <summary>
/// Per-player match data -- the thing MatchController reads/writes when
/// applying round and chase consequences, and what IMiniGame implementations
/// receive instead of a raw client ID. Lives on the Player prefab alongside
/// VoiceRoomRouter as a separate component -- this is match/stakes state,
/// voice routing is a different concern and shouldn't be coupled to it.
///
/// Server-write only, same pattern as VoiceRoomRouter's Role NetworkVariable:
/// only MatchController (running server-side) ever changes these values;
/// every client just reads them for HUD/UI.
///
/// Deliberately NOT here: the persistent meta currency (GDD §8). That's
/// playtime-based, survives across matches, and is spent in a lobby/Theme-
/// unlock screen that doesn't exist yet -- it has no relationship to in-match
/// state and belongs in its own persistent-profile component once that lobby
/// flow gets built.
/// </summary>
[RequireComponent(typeof(NetworkObject))]
public class PlayerState : NetworkBehaviour
{
    /// <summary>
    /// In-match score and spend budget (GDD §8). Resets to 0 every match --
    /// nothing here persists across matches by design.
    /// </summary>
    public readonly NetworkVariable<int> Points = new NetworkVariable<int>(
        0,
        NetworkVariableReadPermission.Everyone,
        NetworkVariableWritePermission.Server);

    /// <summary>
    /// Rounds survived in a row without being caught. Drives the chip-payout
    /// multiplier in MatchController.GameResolve. Resets to 0 on a catch.
    /// </summary>
    public readonly NetworkVariable<int> SurvivalStreak = new NetworkVariable<int>(
        0,
        NetworkVariableReadPermission.Everyone,
        NetworkVariableWritePermission.Server);

    /// <summary>
    /// 0 = active and eligible for the next mini-game. Set to 1 when caught;
    /// decremented at the start of each RoundStart. A single counter rather
    /// than a separate bool+count so it can't desync from itself, and so a
    /// future multi-round bench is a one-line change.
    /// </summary>
    public readonly NetworkVariable<int> BenchRoundsRemaining = new NetworkVariable<int>(
        0,
        NetworkVariableReadPermission.Everyone,
        NetworkVariableWritePermission.Server);

    /// <summary>
    /// Lobby ready flag. The owning client requests changes via SetReadyServerRpc;
    /// only the server writes the value (same server-authoritative pattern as the
    /// vars above). MatchController gates Lobby -> RoundStart on every player being
    /// ready, then clears these. Read by everyone for the lobby readout.
    /// </summary>
    public readonly NetworkVariable<bool> IsReady = new NetworkVariable<bool>(
        false,
        NetworkVariableReadPermission.Everyone,
        NetworkVariableWritePermission.Server);

    /// <summary>
    /// Owning client toggles its own ready state; the write happens server-side.
    /// RequireOwnership stops one client from readying another player.
    /// </summary>
    [ServerRpc(RequireOwnership = true)]
    public void SetReadyServerRpc(bool ready)
    {
        // Only togglable between rounds -- keeps seated players locked to their chair during a
        // minigame/chase (leaving the chair means unreadying, which this blocks mid-round).
        var mc = MatchController.Instance;
        if (mc != null && !mc.ReadyUpAllowed) return;
        IsReady.Value = ready;
    }

    public bool IsActive => BenchRoundsRemaining.Value == 0;
}
