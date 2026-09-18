using UnityEngine;
using UnityEngine.UI;
using TMPro;

/// <summary>
/// The local, client-side book-reading overlay. One lives in the scene (on a Canvas); each client has its
/// own, and only the local player ever opens it — so nothing here is networked and multiple players across
/// clients can read simultaneously for free.
///
/// While open it frees the cursor (which also stops mouse-look/body-yaw, since those guard on
/// Cursor.lockState) and locks WASD via PlayerMovement, so the reader stands still and reads. Closing
/// restores both. IsReading gates PlayerInteractor so the interact key doesn't re-trigger books or hit
/// other props while a page is up.
///
/// SETUP: build a screen-space Canvas with a panel (assign as `root`, start it disabled), a title TMP, a
/// body TMP, an optional page-indicator TMP, and an optional page Image. Optionally add Prev/Next/Close
/// Buttons and wire their OnClick to Prev()/Next()/Close(). Put this component on the Canvas and assign
/// the refs. Page turn also works on arrow keys / A-D; Escape closes.
/// </summary>
public class BookReader : MonoBehaviour
{
    public static BookReader Instance { get; private set; }
    /// <summary>True while a book is open on THIS client — PlayerInteractor checks this to stay quiet.</summary>
    public static bool IsReading { get; private set; }

    [Header("UI refs")]
    [SerializeField] private GameObject root;          // the reading panel; starts disabled
    [SerializeField] private TMP_Text titleText;
    [SerializeField] private TMP_Text bodyText;
    [SerializeField] private TMP_Text pageIndicator;   // "1 / 3" — optional
    [SerializeField] private Image pageImage;          // optional illustration

    [Header("Input")]
    [SerializeField] private KeyCode closeKey = KeyCode.Escape;

    private BookData _book;
    private int _page;
    private PlayerMovement _movement;
    private CursorLockMode _prevLock;
    private bool _prevCursorVisible;

    private void Awake()
    {
        Instance = this;
        IsReading = false;
        if (root) root.SetActive(false);
    }

    private void OnDestroy()
    {
        if (Instance == this) { Instance = null; IsReading = false; }
    }

    /// <summary>Open `book` for the local player. `interactor` is used to lock that player's movement.</summary>
    public void Open(BookData book, PlayerInteractor interactor)
    {
        _book = book;
        _page = 0;
        _movement = interactor != null ? interactor.GetComponent<PlayerMovement>() : null;
        if (_movement != null) _movement.MovementLocked = true;

        _prevLock = Cursor.lockState;
        _prevCursorVisible = Cursor.visible;
        Cursor.lockState = CursorLockMode.None;   // freeing the cursor also stops look + yaw (they guard on lock)
        Cursor.visible = true;

        IsReading = true;
        if (root) root.SetActive(true);
        Show();
    }

    public void Close()
    {
        if (root) root.SetActive(false);
        if (_movement != null) _movement.MovementLocked = false;

        Cursor.lockState = _prevLock;
        Cursor.visible = _prevCursorVisible;

        _book = null;
        _movement = null;
        IsReading = false;
    }

    // Button hooks (wire to on-screen Prev/Next/Close, since the cursor is free while reading).
    public void Next() => TurnPage(+1);
    public void Prev() => TurnPage(-1);

    private void Update()
    {
        if (_book == null) return;

        if (Input.GetKeyDown(closeKey)) { Close(); return; }
        if (Input.GetKeyDown(KeyCode.RightArrow) || Input.GetKeyDown(KeyCode.D)) TurnPage(+1);
        else if (Input.GetKeyDown(KeyCode.LeftArrow) || Input.GetKeyDown(KeyCode.A)) TurnPage(-1);
    }

    private void TurnPage(int dir)
    {
        int count = _book != null && _book.pages != null ? _book.pages.Length : 0;
        if (count == 0) return;
        _page = Mathf.Clamp(_page + dir, 0, count - 1);
        Show();
    }

    private void Show()
    {
        int count = _book.pages != null ? _book.pages.Length : 0;

        if (titleText != null) titleText.text = _book.title;
        if (bodyText != null)  bodyText.text  = count > 0 ? _book.pages[Mathf.Clamp(_page, 0, count - 1)] : "";
        if (pageIndicator != null) pageIndicator.text = count > 0 ? $"{_page + 1} / {count}" : "";

        if (pageImage != null)
        {
            Sprite s = (_book.pageImages != null && _page < _book.pageImages.Length) ? _book.pageImages[_page] : null;
            pageImage.enabled = s != null;
            if (s != null) pageImage.sprite = s;
        }
    }
}
