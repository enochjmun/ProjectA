using System;
using Steamworks;
using UnityEngine;

/// <summary>
/// Owns the Steam client lifecycle: init on startup, pump callbacks every frame,
/// shut down on quit. Nothing else in the project should call
/// SteamClient.Init / Shutdown — route all of that through here.
/// </summary>
public class SteamManager : MonoBehaviour
{
    public static SteamManager Instance { get; private set; }

    // 480 = "Spacewar", Valve's public test AppID. Works for any Steam account
    // with no Steamworks registration. Swap for your real AppID at ship time.
    [SerializeField] private uint appId = 480;

    void Awake()
    {
        if (Instance != null) { Destroy(gameObject); return; }
        Instance = this;
        DontDestroyOnLoad(gameObject);

        try
        {
            // asyncCallbacks:false -> we pump manually in Update (see below).
            // This makes the callback mechanism explicit instead of hidden magic.
            SteamClient.Init(appId, asyncCallbacks: false);
            Debug.Log($"[Steam] Initialised. You are {SteamClient.Name} ({SteamClient.SteamId}).");
        }
        catch (Exception e)
        {
            Debug.LogError($"[Steam] Init failed — is the Steam client running and logged in? {e}");
        }
    }

    void Update()
    {
        // Steam's async lobby/friends calls only resolve if callbacks are pumped.
        // Forget this and every 'await CreateLobbyAsync(...)' hangs forever.
        if (SteamClient.IsValid)
            SteamClient.RunCallbacks();
    }

    void OnDestroy()
    {
        if (Instance == this && SteamClient.IsValid)
            SteamClient.Shutdown();
    }
}
