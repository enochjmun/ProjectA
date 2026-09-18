using UnityEngine;

/// <summary>
/// Data-driven content for a readable book (monster bibliography entry, the minigame guide, flavor, …).
/// One ScriptableObject per book, authored in the Project window (Create → CasinoHorror → Book). Keeps the
/// writing out of the scene so books are easy to add/edit without touching prefabs.
///
/// Keep pages SHORT and punchy — players won't read walls of text mid-horror-game. A minigame guide reads
/// best as a page or two of concise rules (optionally with an illustration); a bibliography entry as one
/// evocative paragraph with the single behaviour that matters for survival.
/// </summary>
[CreateAssetMenu(menuName = "CasinoHorror/Book", fileName = "Book")]
public class BookData : ScriptableObject
{
    public string title = "Untitled";

    [Tooltip("One entry per page. Kept short on purpose.")]
    [TextArea(4, 18)] public string[] pages;

    [Tooltip("Optional illustration per page (parallel to pages; leave an element empty for a text-only " +
             "page). Great for the minigame guide's rules diagram.")]
    public Sprite[] pageImages;
}
