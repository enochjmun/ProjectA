using Unity.Netcode;
using UnityEngine;

/// <summary>
/// PLACEHOLDER owner-side control that lets a LOBBY player opt into spectating the dungeon during a
/// chase -- the exact same orbit spectator a benched player gets. Throwaway, to be replaced by real
/// UI later. Lives on the Player alongside SpectatorController.
///
/// KEYBOARD-DRIVEN, not a clickable button, on purpose: a lobby watcher's cursor is Locked for FPS
/// look, and an IMGUI button both (a) can't be clicked reliably with a locked cursor and (b) got
/// covered by the host's overlapping dev GUI (TransportSwitcherUI's status box occupies the same
/// screen corner). A keypress has no rect and ignores cursor state, so it behaves identically in the
/// editor and a build. OnGUI now only draws a text PROMPT of the keybind.
///
/// Press `spectateKey` to start (only when an eligible lobby watcher); Esc to stop. Both route through
/// SpectatorController -> the server-side gate (MatchController.CanLobbySpectate), so this can't grant
/// anything the server won't allow, and compulsory benched/caught spectate can't be Esc'd out of.
/// </summary>
[RequireComponent(typeof(SpectatorController))]
public class SpectateButtonHud : NetworkBehaviour
{
    [Tooltip("Key a lobby watcher presses to start spectating the chase.")]
    [SerializeField] private KeyCode spectateKey = KeyCode.V;

    [Tooltip("Prompt size (pixels) and vertical offset from the top. Drawn horizontally CENTERED " +
             "(TOP-CENTER) so it's clear of the corner dev HUDs; only .y/.width/.height are used.")]
    [SerializeField] private Rect promptRect = new Rect(0f, 40f, 280f, 24f);

    private SpectatorController _spectator;
    private PlayerState _state;

    // True only while WE opted in (vs compulsory benched/caught spectate), so Esc-to-stop and the
    // prompt only apply to a voluntary lobby watcher.
    private bool _manual;

    // Tracks the previous spectating value so we can clear _manual only when a spectate actually
    // ENDS (true->false), not during the request round-trip where IsSpectating is briefly still
    // false after we asked (which would wrongly wipe the intent on a client).
    private bool _prevSpectating;

    private void Awake()
    {
        _spectator = GetComponent<SpectatorController>();
        _state = GetComponent<PlayerState>();
    }

    private void Update()
    {
        if (!IsOwner || _spectator == null || _state == null) return;

        bool spectating = _spectator.IsSpectating.Value;
        if (_prevSpectating && !spectating) _manual = false;   // spectate actually ended -> reset intent
        _prevSpectating = spectating;

        if (_manual && spectating)
        {
            // Esc stops ONLY a manual (lobby opt-in) spectate. Benched/caught spectate is compulsory
            // and server-controlled, so this branch (gated on _manual) never runs for them.
            if (Input.GetKeyDown(KeyCode.Escape))
                _spectator.RequestSpectate(false);
            return;
        }

        // Start via keypress when an eligible lobby watcher -- no rect to be covered, no cursor to fight.
        if (IsEligibleLobbyWatcher(spectating) && Input.GetKeyDown(spectateKey))
        {
            _manual = true;
            _spectator.RequestSpectate(true);
        }
    }

    // Chase running, not benched, standing in the lobby (outside the dungeon bounds), not already
    // spectating. The server re-checks this in RequestSpectateRpc, so this is only a UI gate.
    private bool IsEligibleLobbyWatcher(bool spectating)
    {
        var mc = MatchController.Instance;
        var dungeon = DungeonGenerator.Instance;
        bool inLobby = dungeon == null || !dungeon.WorldBounds.Contains(transform.position);
        return mc != null && mc.ChaseActive && _state.IsActive && !spectating && inLobby;
    }

    private void OnGUI()
    {
        if (!IsOwner || _spectator == null || _state == null) return;

        bool spectating = _spectator.IsSpectating.Value;

        // Center horizontally (top-center) so it never lands under a corner HUD.
        var r = new Rect((Screen.width - promptRect.width) * 0.5f, promptRect.y, promptRect.width, promptRect.height);
        var centered = new GUIStyle(GUI.skin.label) { alignment = TextAnchor.MiddleCenter };

        if (_manual && spectating)
            GUI.Label(r, "Spectating -- press Esc to stop", centered);
        else if (IsEligibleLobbyWatcher(spectating))
            GUI.Label(r, $"Press {spectateKey} to spectate chase", centered);
    }
}
