using Steamworks;
using UnityEngine;

public class SteamBootstrap : MonoBehaviour
{
    [SerializeField] private uint appId = 480; // Spacewar test app

    void Awake()
    {
        try
        {
            SteamClient.Init(appId);
            Debug.Log($"Steam ready: {SteamClient.Name} ({SteamClient.SteamId})");
        }
        catch (System.Exception e)
        {
            Debug.LogError($"Steam init failed: {e}");
        }
        DontDestroyOnLoad(gameObject);
    }

    void Update() => SteamClient.RunCallbacks();      // pumps Steam's callback queue every frame — required, the SDK does nothing on its own

    void OnApplicationQuit() => SteamClient.Shutdown();
}
