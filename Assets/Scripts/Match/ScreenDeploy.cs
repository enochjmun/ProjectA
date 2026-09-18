using UnityEngine;

/// <summary>
/// Rolls the projector-style screen DOWN for the active round and back UP once the CHASE is resolved. The
/// screen shows the dungeon chase, so it descends at the minigame (GameSelect/GamePlay), stays down through
/// GameResolve and the chase (ChaseInProgress), then holds for the points/standings at ChaseResolve — where
/// the outcome-betting payout settles scores — and only THEN rolls up (RoundEnd). Stowed in Lobby/GameOver.
///
/// Local / visual-only: it reads the ALREADY-REPLICATED MatchController phase and reconstructs the motion on
/// each client — no netcode here, exactly the pattern used by the trapdoor and LobbyLightingController.
///
/// SETUP: model a housing/valance box at the ceiling and the screen PANEL as a child that slides down out
/// of it. Put this on a persistent scene object (the lobby manager is fine). Assign the panel Transform,
/// then position it fully-down and use the "Capture … as Deployed" context-menu item, position it hidden
/// in the housing and "Capture … as Retracted". Press the debug key (F9) in play to preview the roll.
/// </summary>
public class ScreenDeploy : MonoBehaviour
{
    [Tooltip("Scene MatchController. Auto-found if left empty.")]
    [SerializeField] private MatchController match;

    [Header("Screen panel")]
    [Tooltip("The panel that slides down. Its face shows the ChaseTVFeed material.")]
    [SerializeField] private Transform screen;
    [Tooltip("Local position when stowed (hidden up in the housing).")]
    [SerializeField] private Vector3 retractedLocalPosition;
    [Tooltip("Local position when fully rolled down (visible).")]
    [SerializeField] private Vector3 deployedLocalPosition;
    [Tooltip("How fast it eases between stowed and deployed. Lower = a slow, ominous descent.")]
    [SerializeField] private float deploySpeed = 2f;

    [Header("Feed (optional)")]
    [Tooltip("Enabled once the screen is mostly down (e.g. the ChaseTVFeed camera, or a lit-screen " +
             "component) and disabled while stowed — so the feed only runs when it's visible.")]
    [SerializeField] private Behaviour feedWhenDeployed;
    [SerializeField, Range(0f, 1f)] private float feedOnThreshold = 0.6f;

    [Header("Deploy window")]
    [Tooltip("Descend a beat early, while the minigame is being SELECTED (GameSelect). Otherwise it drops at GamePlay.")]
    [SerializeField] private bool descendAtSelect = true;

    [Header("Debug / authoring")]
    [Tooltip("When ON, deploy is driven by the slider below (and the key) instead of the chase phase — " +
             "so you can preview/author the roll without reaching a real chase.")]
    [SerializeField] private bool debugOverride;
    [SerializeField, Range(0f, 1f)] private float debugDeploy;
    [SerializeField] private KeyCode debugToggleKey = KeyCode.F9;

    private float _deploy;   // 0 = stowed, 1 = down; eased

    private void Awake()
    {
        if (match == null) match = FindObjectOfType<MatchController>();
        if (screen != null) screen.localPosition = retractedLocalPosition;   // start stowed
    }

    private void Update()
    {
        // F9: force debug override and flip the target between stowed and deployed.
        if (Input.GetKeyDown(debugToggleKey))
        {
            debugOverride = true;
            debugDeploy = debugDeploy > 0.5f ? 0f : 1f;
        }

        // Stowed by default; down from the minigame through chase resolution, then back up at RoundEnd.
        bool deployed = match != null && IsScreenActivePhase(match.CurrentPhase);
        float target = debugOverride ? debugDeploy : (deployed ? 1f : 0f);

        // Framerate-independent ease (same as LobbyLightingController / AreaEnvironment).
        float t = 1f - Mathf.Exp(-deploySpeed * Time.deltaTime);
        _deploy = Mathf.Lerp(_deploy, target, t);

        if (screen != null)
            screen.localPosition = Vector3.Lerp(retractedLocalPosition, deployedLocalPosition, _deploy);

        if (feedWhenDeployed != null)
        {
            bool on = _deploy >= feedOnThreshold;
            if (feedWhenDeployed.enabled != on) feedWhenDeployed.enabled = on;
        }
    }

    // Down from the minigame through the chase and its resolution (points/betting settle at ChaseResolve);
    // up in Lobby, RoundStart, RoundEnd, GameOver.
    private bool IsScreenActivePhase(MatchController.Phase p)
    {
        switch (p)
        {
            case MatchController.Phase.GamePlay:
            case MatchController.Phase.GameResolve:
            case MatchController.Phase.ChaseInProgress:
            case MatchController.Phase.ChaseResolve:
                return true;
            case MatchController.Phase.GameSelect:
                return descendAtSelect;
            default:
                return false;
        }
    }

    [ContextMenu("Capture current as Deployed")]
    private void CaptureDeployed() { if (screen != null) deployedLocalPosition = screen.localPosition; }

    [ContextMenu("Capture current as Retracted")]
    private void CaptureRetracted() { if (screen != null) retractedLocalPosition = screen.localPosition; }
}
