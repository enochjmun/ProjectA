using Unity.Netcode;
using UnityEngine;

/// <summary>
/// Owner-only "look at something and press a key to use it" interaction. Lives on
/// the Player prefab. Raycasts forward from the player camera every frame; if it
/// hits an IInteractable it shows a prompt and calls Interact() on key press.
///
/// Owner-gated like PlayerCamera (enabled = IsOwner in OnNetworkSpawn) so remote
/// players' interactors never run -- each client only ever drives its own. The
/// raycast is purely local; any networking lives inside the interactable (e.g.
/// ReadyUpInteractable calls a ServerRpc the interacting player is allowed to make).
/// </summary>
[RequireComponent(typeof(PlayerState))]
public class PlayerInteractor : NetworkBehaviour
{
    [SerializeField] private Camera viewCamera;            // owner's child camera; auto-found if left null
    [SerializeField] private float range = 3f;
    [SerializeField] private KeyCode interactKey = KeyCode.E;
    [Tooltip("Which layers count as interactable. Leave as Everything to start.")]
    [SerializeField] private LayerMask mask = ~0;

    /// <summary>
    /// This player's match state, handed to interactables so they can act on the
    /// interacting player (e.g. toggle ready).
    /// </summary>
    public PlayerState PlayerState { get; private set; }

    private IInteractable _current;

    public override void OnNetworkSpawn()
    {
        PlayerState = GetComponent<PlayerState>();
        if (viewCamera == null)
            viewCamera = GetComponentInChildren<Camera>(true);

        // Only the owning client runs interaction logic.
        enabled = IsOwner;
    }

    private void Update()
    {
        if (!IsOwner || viewCamera == null)
            return;

        // While this client is reading a book, suppress interaction entirely — so the interact key can't
        // re-open the book or hit another prop, and no prompt shows over the reading overlay.
        if (BookReader.IsReading)
        {
            _current = null;
            return;
        }

        // Raycast forward from the camera. GetComponentInParent lets the collider
        // sit on a child of the interactable object.
        _current = null;
        if (Physics.Raycast(viewCamera.transform.position, viewCamera.transform.forward,
                             out var hit, range, mask, QueryTriggerInteraction.Collide))
        {
            _current = hit.collider.GetComponentInParent<IInteractable>();
        }

        if (_current != null && Input.GetKeyDown(interactKey))
            _current.Interact(this);
    }

    private void OnGUI()
    {
        if (!IsOwner || _current == null)
            return;

        var style = new GUIStyle(GUI.skin.box) { alignment = TextAnchor.MiddleCenter, fontSize = 16 };
        const float w = 340f, h = 34f;
        GUI.Label(new Rect((Screen.width - w) * 0.5f, Screen.height * 0.62f, w, h),
                  $"[{interactKey}] {_current.GetPrompt(this)}", style);
    }
}
