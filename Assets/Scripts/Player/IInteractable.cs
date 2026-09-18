/// <summary>
/// Anything the player can look at and activate via PlayerInteractor. Kept as a
/// tiny interface (same interface-driven approach as IMiniGame) so any object --
/// a ready terminal, a door, a minigame seat -- can be interactable without a
/// shared base class.
/// </summary>
public interface IInteractable
{
    /// <summary>
    /// Short label shown in the on-screen prompt, e.g. "Ready up". Takes the
    /// interactor so the text can reflect the interacting player's state
    /// (e.g. "Ready up" vs "Cancel ready").
    /// </summary>
    string GetPrompt(PlayerInteractor interactor);

    /// <summary>
    /// Called on the LOCAL owner when they press the interact key while aiming at
    /// this object. The interactor is passed so the object can act on the
    /// interacting player (e.g. toggle their ready state via a ServerRpc).
    /// </summary>
    void Interact(PlayerInteractor interactor);
}
