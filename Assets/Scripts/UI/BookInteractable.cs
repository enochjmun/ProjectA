using UnityEngine;

/// <summary>
/// A readable book on the shelf. Implements IInteractable, so PlayerInteractor picks it up like any other
/// prompt ("Read ...") — but Interact() just opens the LOCAL BookReader. No networking: each player reads
/// their own book, so any number of players can have a book open at once with zero replication.
///
/// SETUP: put on the book prop (needs a collider on it or a child, on an interactable layer). Assign the
/// BookData asset. The BookReader UI must exist in the scene.
/// </summary>
public class BookInteractable : MonoBehaviour, IInteractable
{
    [SerializeField] private BookData book;

    public string GetPrompt(PlayerInteractor interactor)
        => book != null ? $"Read “{book.title}”" : "Read";

    public void Interact(PlayerInteractor interactor)
    {
        if (book == null) return;
        if (BookReader.Instance == null)
        {
            Debug.LogWarning("[BookInteractable] No BookReader in the scene — can't open the book.", this);
            return;
        }
        BookReader.Instance.Open(book, interactor);   // purely local to this client
    }
}
