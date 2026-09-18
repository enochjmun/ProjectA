using Netcode.Transports.Facepunch;
using Unity.Netcode;
using Unity.Netcode.Transports.UTP;
using UnityEngine;

/// <summary>
/// Dev-only connect UI that lets you choose the transport BEFORE hosting/joining:
///   - UTP (Unity Transport) over 127.0.0.1 for fast single-machine / ParrelSync
///     testing (no Steam needed).
///   - Facepunch (Steam P2P) for real testing with friends.
///
/// The chosen transport is written to NetworkManager.NetworkConfig.NetworkTransport
/// right before StartHost/StartClient -- transports can't be swapped once a session
/// is live. Because only the ACTIVE transport gets Initialize()'d, picking UTP means
/// FacepunchTransport.Initialize() (which calls SteamClient.Init) never runs, so UTP
/// mode works without Steam running -- as long as nothing else inits Steam
/// independently (your architecture keeps Steam init owned by the transport, so this
/// holds).
///
/// SETUP:
///   1. Add a UnityTransport component to the NetworkManager GameObject (the
///      FacepunchTransport is already there).
///   2. Put this script on any scene object and drag both transports into its fields.
///   3. Disable/delete ConnectTest and DevConnectUI so connect UIs don't overlap.
/// </summary>
public class TransportSwitcherUI : MonoBehaviour
{
    [Header("Transports (both live on the NetworkManager GameObject)")]
    [SerializeField] private UnityTransport utpTransport;
    [SerializeField] private FacepunchTransport facepunchTransport;

    [Header("UTP settings")]
    [Tooltip("Address UTP clients connect to. 127.0.0.1 = same machine; a LAN IP for two machines.")]
    [SerializeField] private string utpAddress = "127.0.0.1";
    [SerializeField] private ushort utpPort = 7777;

    [Header("Disconnect")]
    [Tooltip("Key to leave the session. A keybind so disconnect works even when the cursor is locked " +
             "(IMGUI buttons aren't clickable with a locked cursor).")]
    [SerializeField] private KeyCode disconnectKey = KeyCode.F9;

    private enum Mode { UTP, Steam }
    private Mode _mode = Mode.UTP;
    private string _joinSteamId = "";

    private void Update()
    {
        var nm = NetworkManager.Singleton;
        if (nm != null && (nm.IsClient || nm.IsServer) && Input.GetKeyDown(disconnectKey))
            nm.Shutdown();
    }

    private void OnGUI()
    {
        var nm = NetworkManager.Singleton;
        if (nm == null)
            return; // no NetworkManager in this scene

        GUILayout.BeginArea(new Rect(10, 10, 300, 250), GUI.skin.box);

        // --- Already in a session: status + disconnect ---
        if (nm.IsClient || nm.IsServer)
        {
            GUILayout.Label(nm.IsHost ? "HOST" : nm.IsServer ? "SERVER" : "CLIENT");
            GUILayout.Label($"Transport: {nm.NetworkConfig.NetworkTransport?.GetType().Name}");
            if (nm.IsServer)
                GUILayout.Label($"Clients connected: {nm.ConnectedClientsList.Count}");

            // Show the host's SteamID so a friend can join (only meaningful on Steam).
            if (nm.IsServer && nm.NetworkConfig.NetworkTransport == facepunchTransport
                && Steamworks.SteamClient.IsValid)
                GUILayout.Label($"My SteamID: {Steamworks.SteamClient.SteamId}");

            if (GUILayout.Button("Disconnect"))
                nm.Shutdown();
            GUILayout.Label($"(or press {disconnectKey})");

            GUILayout.EndArea();
            return;
        }

        // --- Not connected: pick transport, then host/join ---
        GUILayout.Label("Transport:");
        _mode = (Mode)GUILayout.SelectionGrid((int)_mode, new[] { "UTP (local)", "Steam" }, 2);

        GUILayout.Space(6);

        if (GUILayout.Button("Host"))
        {
            if (ApplyTransport())
                nm.StartHost();
        }

        GUILayout.Space(6);

        if (_mode == Mode.UTP)
        {
            GUILayout.Label("Client connects to:");
            utpAddress = GUILayout.TextField(utpAddress);
            if (GUILayout.Button("Join (UTP)"))
            {
                if (ApplyTransport())
                    nm.StartClient();
            }
        }
        else
        {
            GUILayout.Label("Join via host SteamID:");
            _joinSteamId = GUILayout.TextField(_joinSteamId);
            if (GUILayout.Button("Join (Steam)"))
            {
                if (ulong.TryParse(_joinSteamId, out var id) && ApplyTransport())
                {
                    facepunchTransport.targetSteamId = id;
                    nm.StartClient();
                }
            }
        }

        GUILayout.EndArea();
    }

    /// <summary>
    /// Points the NetworkManager at the selected transport. Returns false (and logs)
    /// if the needed transport reference wasn't assigned.
    /// </summary>
    private bool ApplyTransport()
    {
        var nm = NetworkManager.Singleton;

        if (_mode == Mode.UTP)
        {
            if (utpTransport == null)
            {
                Debug.LogError("[TransportSwitcherUI] UnityTransport not assigned.", this);
                return false;
            }
            utpTransport.SetConnectionData(utpAddress, utpPort);
            nm.NetworkConfig.NetworkTransport = utpTransport;
            return true;
        }

        if (facepunchTransport == null)
        {
            Debug.LogError("[TransportSwitcherUI] FacepunchTransport not assigned.", this);
            return false;
        }
        nm.NetworkConfig.NetworkTransport = facepunchTransport;
        return true;
    }
}
