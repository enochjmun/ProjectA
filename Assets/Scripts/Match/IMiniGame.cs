using System.Collections.Generic;

/// <summary>
/// The seam between MatchController (the authoritative state machine) and any
/// individual mini-game. Server-only contract: MatchController is the only
/// caller, and it only ever calls these from server-side code (see its Update()
/// guard). Nothing here is itself networked -- a class implementing this
/// interface is free to ALSO be a NetworkBehaviour and use ServerRpc/ClientRpc/
/// NetworkVariable internally to collect input and drive its own UI, but that
/// wiring is private to the implementation and invisible to MatchController.
/// See NumberPickMiniGame for the reference example of that split.
///
/// Equally important: this interface reports outcomes, it does not apply them.
/// Points, survival streak, and bench state are shared stakes-loop mechanics
/// (GDD §5.1/§8) that are identical across every mini-game, so MatchController
/// applies them once, in GameResolve, after reading GetLosers() here. A mini-game
/// implementation should never AWARD a PlayerState's Points/SurvivalStreak/
/// BenchRoundsRemaining -- those are GameResolve's job.
///
/// Narrow exception (added for Old Maid's pay-to-peek, 2026-06-29): a mini-game
/// MAY *deduct* Points server-side as the cost of an in-game purchase, since
/// spending is part of the mini-game's own moment-to-moment economy rather than
/// the shared end-of-round payout. It still must never grant Points or touch
/// SurvivalStreak/BenchRoundsRemaining. If in-game spends proliferate, this is
/// the seam to centralise (e.g. a MatchController.TrySpend) rather than leaving
/// each mini-game to debit Points itself.
/// </summary>
public interface IMiniGame
{
    /// <summary>
    /// Called once, server-side, when MatchController enters GamePlay for this
    /// mini-game. Hand it the active (non-benched) roster for the round.
    /// </summary>
    void Setup(IReadOnlyList<PlayerState> players);

    /// <summary>
    /// Called every server frame while MatchController is in GamePlay, until
    /// IsComplete returns true. dt is server Time.deltaTime.
    /// </summary>
    void Tick(float dt);

    /// <summary>
    /// MatchController polls this after every Tick. Once true, MatchController
    /// stops calling Tick and moves to GameResolve.
    /// </summary>
    bool IsComplete { get; }

    /// <summary>
    /// Called once, after IsComplete is true. Return whichever players lost --
    /// empty list if nobody did. Do not apply any consequence here; just report.
    /// </summary>
    IReadOnlyList<PlayerState> GetLosers();

    /// <summary>
    /// Called once, after GetLosers() has been read. Despawn/clean up anything
    /// this mini-game created (NetworkObjects, UI, event subscriptions).
    /// </summary>
    void Teardown();
}
