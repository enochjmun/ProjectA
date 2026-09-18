using Netcode.Transports.Facepunch;
using Unity.Netcode;
using Unity.Netcode.Transports.UTP;
using UnityEngine;

/// <summary>
/// The security-room main menu, single-scene state version. The room + a parked menu camera + a
/// world-space canvas on the hero monitor live in the gameplay scene; before a session you see the menu,
/// and the instant a session starts (Host or Join) the menu switches off so the spawned player's camera
/// takes over. Leaving the session (Shutdown) brings the menu back.
///
/// It wraps the SAME host/join logic as the dev TransportSwitcherUI — pick UTP (local / ParrelSync) or
/// Facepunch (Steam P2P), then StartHost / StartClient — so nothing about the working boot flow changes;
/// this is just a proper front-end for it. Promotes cleanly to a separate MainMenu scene later: the only
/// thing that changes is "disable menuRoot" becomes "load the game scene".
///
/// WIRING (UI Buttons call the public methods; input fields call the setters):
///   Host button           -> Host()
///   Join button           -> Join()
///   Quit button           -> Quit()
///   "Use Steam" toggle     -> SetSteamMode(bool)     (off = UTP local)
///   Steam ID input field   -> SetJoinSteamId(string) (onValueChanged)
///   UTP address field opt  -> SetUtpAddress(string)  (onValueChanged)
///
/// SETUP: put this on a scene object. Assign menuRoot (the parent holding the menu camera + canvas so
/// one SetActive hides both), and drag in the two transports that live on the NetworkManager.
/// </summary>
public class MainMenuController : MonoBehaviour
{
    [Header("Menu presentation")]
    [Tooltip("Parent of the menu camera + the monitor canvas. Disabled the moment a session starts so " +
             "the spawned player's camera and HUD take over; re-enabled on disconnect.")]
    [SerializeField] private GameObject menuRoot;
    [Tooltip("Optional: the security-room camera director. When the menu re-appears (disconnect / back " +
             "to menu) we snap it to the Main view so you never return mid-pan on the Options monitor. " +
             "It also snaps home on its own OnEnable; this is the belt-and-braces call.")]
    [SerializeField] private MenuCameraDirector cameraDirector;

    [Header("Transports (both live on the NetworkManager GameObject)")]
    [SerializeField] private UnityTransport utpTransport;
    [SerializeField] private FacepunchTransport facepunchTransport;

    [Header("Collection cinematic")]
    [Tooltip("Optional. If assigned, committing (Host/Join) plays the marker-stamp cinematic FIRST, then " +
             "starts the session. Leave null to start immediately (old behaviour).")]
    [SerializeField] private MarkerStampSequence stampSequence;

    [Header("Performance")]
    [Tooltip("Frame cap while the MENU is up. A menu doesn't need 60 -- 30 roughly halves GPU load across " +
             "the dynamic lights, post, and live feeds. NOTE: only takes effect if VSync is OFF " +
             "(Quality settings -> VSync Count = Don't Sync); with VSync on this is ignored.")]
    [SerializeField] private int menuFrameRate = 30;
    [Tooltip("Frame cap restored when a session starts. -1 = uncapped / platform default (let VSync govern).")]
    [SerializeField] private int gameplayFrameRate = -1;

    [Header("Connection")]
    [Tooltip("Default: Steam P2P. Turn off (via the toggle) for UTP local / ParrelSync testing.")]
    [SerializeField] private bool useSteam = true;
    [SerializeField] private string utpAddress = "127.0.0.1";
    [SerializeField] private ushort utpPort = 7777;

    private string _joinSteamId = "";
    private bool _inSession;

    // ---- UI hooks ----
    public void SetSteamMode(bool on) => useSteam = on;
    public void SetJoinSteamId(string id) => _joinSteamId = id;
    public void SetUtpAddress(string addr) => utpAddress = addr;

    /// <summary>Host button. Starts a session; the networked scene stays put (single-scene), the menu
    /// hides itself when the session goes live (see Update).</summary>
    public void Host()
    {
        if (!ApplyTransport()) return;
        // Play the collection cinematic first (if wired), THEN start — so the stamp isn't racing the spawn.
        CommitThenStart(() => NetworkManager.Singleton.StartHost());
    }

    /// <summary>Join button. UTP joins the configured address; Steam joins the entered SteamID.</summary>
    public void Join()
    {
        if (!ApplyTransport()) return;

        if (useSteam)
        {
            if (!ulong.TryParse(_joinSteamId, out var id))
            {
                Debug.LogWarning("[MainMenu] Steam join: enter a valid host SteamID first.", this);
                return;
            }
            facepunchTransport.targetSteamId = id;
        }
        CommitThenStart(() => NetworkManager.Singleton.StartClient());
    }

    // Validation has already passed. Play the marker-stamp cinematic, then run `start` at its end; if no
    // sequence is wired, start immediately.
    private void CommitThenStart(System.Action start)
    {
        if (stampSequence != null) stampSequence.Begin(start);
        else start();
    }

    public void Quit()
    {
        Application.Quit();
#if UNITY_EDITOR
        UnityEditor.EditorApplication.isPlaying = false;
#endif
    }

    private void Start()
    {
        ShowMenu(true);   // menu up, cursor free, before any session
    }

    private void Update()
    {
        // Flip the menu on the session-state transition. StartHost/StartClient don't fire a single tidy
        // "connected" callback across host+client uniformly, so poll IsListening and act on the edge.
        var nm = NetworkManager.Singleton;
        bool inSession = nm != null && (nm.IsClient || nm.IsServer);
        if (inSession != _inSession)
        {
            _inSession = inSession;
            ShowMenu(!inSession);
        }
    }

    // Menu visible = camera + canvas on, cursor free to click. When a session starts we only HIDE it —
    // gameplay (PlayerMovement) owns the cursor lock from there, so we don't fight it.
    private void ShowMenu(bool show)
    {
        if (menuRoot != null) menuRoot.SetActive(show);

        // Cap the framerate in the menu (halves GPU load on the dynamic scene); restore on session start.
        Application.targetFrameRate = show ? menuFrameRate : gameplayFrameRate;

        if (show)
        {
            // Always come back to the main monitor, not wherever the camera was parked when we left.
            if (cameraDirector != null) cameraDirector.SnapHome();
            Cursor.lockState = CursorLockMode.None;
            Cursor.visible = true;
        }
    }

    // Point the NetworkManager at the chosen transport right before starting (can't swap mid-session).
    private bool ApplyTransport()
    {
        var nm = NetworkManager.Singleton;
        if (nm == null) { Debug.LogError("[MainMenu] No NetworkManager in the scene.", this); return false; }

        if (!useSteam)
        {
            if (utpTransport == null) { Debug.LogError("[MainMenu] UnityTransport not assigned.", this); return false; }
            utpTransport.SetConnectionData(utpAddress, utpPort);
            nm.NetworkConfig.NetworkTransport = utpTransport;
            return true;
        }

        if (facepunchTransport == null) { Debug.LogError("[MainMenu] FacepunchTransport not assigned.", this); return false; }
        nm.NetworkConfig.NetworkTransport = facepunchTransport;
        return true;
    }
}
