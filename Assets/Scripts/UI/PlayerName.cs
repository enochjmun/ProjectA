using System;
using UnityEngine;

/// <summary>
/// The single source of truth for the player's entered name. Backed by PlayerPrefs so it survives across
/// scenes/sessions, and fires Changed so anything showing it (the marker signature, a lobby nametag) updates
/// live as the player types. Also the natural place to read the display name from when spawning in
/// multiplayer -- one key, "PlayerName".
///
/// Static by design: there's exactly one local player name, so no instance/singleton wiring to get wrong.
/// </summary>
public static class PlayerName
{
    private const string Key = "PlayerName";

    /// <summary>Fires whenever the name changes, with the new value. Subscribe to update UI live.</summary>
    public static event Action<string> Changed;

    public static string Get() => PlayerPrefs.GetString(Key, "");

    public static void Set(string name)
    {
        name = (name ?? "").Trim();
        PlayerPrefs.SetString(Key, name);
        PlayerPrefs.Save();
        Changed?.Invoke(name);
    }
}
