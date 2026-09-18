using System;
using UnityEngine;

/// <summary>
/// Persistent per-player profile (PlayerPrefs, survives sessions).
///
/// MARKER NUMBER = the player's unique "Nth mark the house has taken" id. A TRUE global "Nth player ever"
/// needs a BACKEND counter (a server that atomically increments one shared number and hands each new player
/// the next value on first launch — Firebase/Supabase/PlayFab/Unity Gaming Services, or Steam stats). The
/// client cannot know a global count on its own.
///
/// Until that backend exists, MarkerNumber is assigned ONCE on first launch as a stable unique-ish
/// placeholder and persisted. When you build the backend, call SetMarkerNumber(serverValue) on first launch
/// to overwrite it with the real global index — nothing else changes (MarkerNumberLabel just reads this).
///
/// Also tracks GAMES PLAYED (lifetime, this machine) separately — useful for stats, NOT the marker number.
/// </summary>
public static class PlayerProfile
{
    private const string MarkerKey = "MarkerNumber";
    private const string GamesKey  = "GamesPlayed";

    public static event Action Changed;

    /// <summary>The player's marker number. Assigned once on first launch (placeholder) until a backend
    /// supplies the real global index via SetMarkerNumber().</summary>
    public static int MarkerNumber
    {
        get
        {
            if (!PlayerPrefs.HasKey(MarkerKey))
            {
                // placeholder unique-ish id — REPLACE with a server-assigned sequential number later
                PlayerPrefs.SetInt(MarkerKey, UnityEngine.Random.Range(1, 100000));
                PlayerPrefs.Save();
            }
            return PlayerPrefs.GetInt(MarkerKey);
        }
    }

    /// <summary>Set the marker number from a backend (the real global "Nth player" index). Call once, on
    /// first launch, after the server hands it out.</summary>
    public static void SetMarkerNumber(int n)
    {
        PlayerPrefs.SetInt(MarkerKey, n);
        PlayerPrefs.Save();
        Changed?.Invoke();
    }

    // ---- lifetime games played (this machine) — separate stat, not the marker number ----
    public static int GamesPlayed => PlayerPrefs.GetInt(GamesKey, 0);

    public static void RegisterGamePlayed()
    {
        PlayerPrefs.SetInt(GamesKey, GamesPlayed + 1);
        PlayerPrefs.Save();
        Changed?.Invoke();
    }

    public static void ResetForTesting()
    {
        PlayerPrefs.DeleteKey(MarkerKey);
        PlayerPrefs.DeleteKey(GamesKey);
        PlayerPrefs.Save();
        Changed?.Invoke();
    }
}
