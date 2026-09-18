using System;
using UnityEngine;

/// <summary>
/// Persistent debt store — the marker balance that survives across sessions (PlayerPrefs), serviced by
/// winnings-minus-fees each match. Mirrors PlayerName. The balance NEVER clears (floored at 1); the
/// cumulative amount serviced only GROWS, and drives the reward milestones (rewards fire on cumulative
/// paid, not on balance remaining — see Debt_FakeReplayability.md).
///
/// Match logic calls Service(net) at end of game with the net applied to the marker (winnings minus the
/// house's fees — a pittance). The marker prop (MarkerDebt) and any menu readout listen to Changed.
/// </summary>
public static class PlayerDebt
{
    private const string BalKey = "PlayerDebtBalance";
    private const string SvcKey = "PlayerDebtServiced";

    /// <summary>Starting marker the player signs (the un-payable mountain).</summary>
    public const long StartingDebt = 1284502;

    /// <summary>Every $ of cumulative-serviced that crosses a multiple of this = a reward milestone.</summary>
    public const long MilestoneStep = 500;

    public static event Action Changed;

    public static long Balance =>
        long.TryParse(PlayerPrefs.GetString(BalKey, StartingDebt.ToString()), out var v) ? v : StartingDebt;

    public static long Serviced =>
        long.TryParse(PlayerPrefs.GetString(SvcKey, "0"), out var v) ? v : 0;

    /// <summary>Progress within the current milestone step (0..MilestoneStep) — for the menu's "$X left" bar.</summary>
    public static long ServicedIntoStep => Serviced % MilestoneStep;
    public static long ToNextMilestone => MilestoneStep - ServicedIntoStep;

    /// <summary>
    /// Apply a payment (net winnings after house fees). Reduces the balance, grows the cumulative serviced,
    /// and returns how many reward milestones this payment crossed (0 usually; award those unlocks).
    /// </summary>
    public static int Service(long amount)
    {
        if (amount <= 0) return 0;
        long before = Serviced;
        long bal = Math.Max(1, Balance - amount);
        long svc = before + amount;
        PlayerPrefs.SetString(BalKey, bal.ToString());
        PlayerPrefs.SetString(SvcKey, svc.ToString());
        PlayerPrefs.Save();
        Changed?.Invoke();
        return (int)(svc / MilestoneStep - before / MilestoneStep);   // milestones crossed by this payment
    }

    /// <summary>Dev only — wipe back to the fresh marker.</summary>
    public static void ResetForTesting()
    {
        PlayerPrefs.DeleteKey(BalKey);
        PlayerPrefs.DeleteKey(SvcKey);
        PlayerPrefs.Save();
        Changed?.Invoke();
    }
}
