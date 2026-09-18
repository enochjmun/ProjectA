using UnityEngine;
#if ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem;
#endif

/// <summary>
/// DEV-ONLY harness to eyeball the leaderboard without a multiplayer session. Drives a FAKE roster on
/// LeaderboardView (needs its debugMode = ON) so you can watch sorting, the leader treatment, DUE, and the
/// count + delta animations react in real time.
///
/// SETUP: drop on any GameObject in the lobby scene, assign `view`. Enter Play, then use the keys below.
/// REMEMBER to turn LeaderboardView.debugMode OFF (and ideally disable/remove this) before real play.
///
/// Keys (number row):
///   1  add the next preset player (POKER_JOE, LADY_M, ...) at 0 pts
///   Q  clear the roster
///   2 / 3   +250 / -150 to a RANDOM player   (watch it re-sort + delta pop; can't go below 0)
///   4 / 5   +250 / -150 to the LAST-ADDED player   (aim changes at one seat)
///   6       big +1000 to a random player   (dramatic overtake for the leader mark)
///   D       toggle DUE on a random player
///   R       reset: clear, add 4 players with spread-out scores (instant demo)
/// </summary>
public class LeaderboardDebug : MonoBehaviour
{
    [Tooltip("The LeaderboardView to drive. Its debugMode must be ON.")]
    [SerializeField] private LeaderboardView view;

    [Tooltip("Names handed out in order by the '1' key / the R reset.")]
    [SerializeField] private string[] presetNames =
        { "POKER_JOE", "LADY_M", "THE_KID", "SNAKE_EYES", "DOUBLE_D", "CROUPIER" };

    private int _nextPreset;

    private void Reset() { view = FindObjectByType(); }
    private void Awake() { if (view == null) view = FindObjectByType(); }

    private static LeaderboardView FindObjectByType() =>
        Object.FindFirstObjectByType<LeaderboardView>();

    private void Update()
    {
        if (view == null) return;

        if (Down(Key.Digit1, KeyCode.Alpha1)) AddNextPreset();
        if (Down(Key.Q,      KeyCode.Q))      view.DebugClear();
        if (Down(Key.Digit2, KeyCode.Alpha2)) view.DebugAddPoints(RandomIndex(), +250);
        if (Down(Key.Digit3, KeyCode.Alpha3)) view.DebugAddPoints(RandomIndex(), -150);
        if (Down(Key.Digit4, KeyCode.Alpha4)) view.DebugAddPoints(LastIndex(),   +250);
        if (Down(Key.Digit5, KeyCode.Alpha5)) view.DebugAddPoints(LastIndex(),   -150);
        if (Down(Key.Digit6, KeyCode.Alpha6)) view.DebugAddPoints(RandomIndex(), +1000);
        if (Down(Key.D,      KeyCode.D))       view.DebugToggleDue(RandomIndex());
        if (Down(Key.R,      KeyCode.R))       ResetDemo();
    }

    private void AddNextPreset()
    {
        string name = presetNames.Length > 0
            ? presetNames[_nextPreset % presetNames.Length]
            : "PLAYER_" + (_nextPreset + 1);
        _nextPreset++;
        view.DebugAddPlayer(name, 0);
    }

    private void ResetDemo()
    {
        view.DebugClear();
        _nextPreset = 0;
        int[] seed = { 1200, 800, 450, 100 };
        for (int i = 0; i < seed.Length; i++)
        {
            string name = presetNames.Length > 0
                ? presetNames[i % presetNames.Length] : "PLAYER_" + (i + 1);
            view.DebugAddPlayer(name, seed[i]);
            _nextPreset++;
        }
    }

    private int RandomIndex() => view.DebugCount > 0 ? Random.Range(0, view.DebugCount) : -1;
    private int LastIndex()   => view.DebugCount - 1;

    // Works whether the project uses the new Input System, the old one, or both.
    private static bool Down(Key newKey, KeyCode oldKey)
    {
#if ENABLE_INPUT_SYSTEM
        if (Keyboard.current != null) return Keyboard.current[newKey].wasPressedThisFrame;
#endif
#if ENABLE_LEGACY_INPUT_MANAGER
        return Input.GetKeyDown(oldKey);
#else
        return false;
#endif
    }
}
