using UnityEngine;

/// <summary>
/// Decides WHAT the lobby screen shows, by match phase — the content side of the screen (ScreenDeploy is the
/// physical roll-down). The screen is a diegetic multi-channel display: standings between rounds, a timer for
/// timed minigames, the chase feed during a hunt. Keeping this info on the in-world screen instead of a HUD
/// overlay fits the "you're watching the house's monitors" fiction.
///
/// HOW IT WORKS: each channel is a SOURCE GameObject (a camera or a UI canvas+camera) preconfigured to render
/// into the SAME screen RenderTexture. Only one source is active at a time; this manager enables the source
/// for the current channel and disables the rest, so the screen shows that channel. Local / visual-only —
/// reads the already-replicated MatchController phase, no netcode (same pattern as LobbyLightingController).
///
/// A source left unassigned just means that channel shows nothing yet (the screen holds its last frame) —
/// fine for wiring channels incrementally. NOTE: the CHASE feed needs the real dungeon chase to exist to film
/// (prey moving in the dungeon); until then its source is a placeholder — the auto-director subject-following
/// is a dungeon-era finish.
///
/// SETUP: one shared RenderTexture on the screen material; each source renders to it (camera Output Target =
/// that RT, or a UI camera rendering a world-space canvas to it). Put this on the lobby manager, assign the
/// sources. Use the debug override to force a channel while authoring.
/// </summary>
public class ScreenChannelManager : MonoBehaviour
{
    public enum ScreenChannel { Idle, Leaderboard, Timer, Chase }

    [Tooltip("Scene MatchController. Auto-found if left empty.")]
    [SerializeField] private MatchController match;

    [Header("Channel sources (each renders to the shared screen RenderTexture; one active at a time)")]
    [Tooltip("The chase director-camera feed. Real prey-following is dungeon-era; placeholder for now.")]
    [SerializeField] private GameObject chaseSource;
    [Tooltip("Standings between rounds and at minigame end.")]
    [SerializeField] private GameObject leaderboardSource;
    [Tooltip("Countdown for timed minigames.")]
    [SerializeField] private GameObject timerSource;
    [Tooltip("Optional: shown when nothing else is (dead channel / static / off). Screen is usually retracted here anyway.")]
    [SerializeField] private GameObject idleSource;

    [Header("Debug")]
    [Tooltip("When ON, the channel below is forced instead of following the match phase — to author each channel without reaching a real phase.")]
    [SerializeField] private bool debugOverride;
    [SerializeField] private ScreenChannel debugChannel = ScreenChannel.Leaderboard;

    private ScreenChannel _current = (ScreenChannel)(-1);   // sentinel so the first Apply always runs

    private void Awake()
    {
        if (match == null) match = FindObjectOfType<MatchController>();
        Apply(ScreenChannel.Idle);
    }

    private void Update()
    {
        ScreenChannel target = debugOverride ? debugChannel : ChannelForPhase();
        if (target != _current) Apply(target);
    }

    private ScreenChannel ChannelForPhase()
    {
        if (match == null) return ScreenChannel.Idle;
        switch (match.CurrentPhase)
        {
            case MatchController.Phase.ChaseInProgress:
            case MatchController.Phase.ChaseResolve:
                return ScreenChannel.Chase;
            case MatchController.Phase.GamePlay:
                return ScreenChannel.Timer;
            case MatchController.Phase.Lobby:
                return ScreenChannel.Idle;   // screen is retracted in the Lobby anyway
            default:                          // RoundStart, GameSelect, GameResolve, RoundEnd, GameOver
                return ScreenChannel.Leaderboard;
        }
    }

    private void Apply(ScreenChannel channel)
    {
        _current = channel;
        SetSource(chaseSource,       channel == ScreenChannel.Chase);
        SetSource(leaderboardSource, channel == ScreenChannel.Leaderboard);
        SetSource(timerSource,       channel == ScreenChannel.Timer);
        SetSource(idleSource,        channel == ScreenChannel.Idle);
    }

    private static void SetSource(GameObject go, bool on)
    {
        if (go != null && go.activeSelf != on) go.SetActive(on);
    }
}
