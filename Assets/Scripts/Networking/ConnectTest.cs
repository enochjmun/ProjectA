using Netcode.Transports.Facepunch;
using Unity.Netcode;
using UnityEngine;

public class ConnectTest : MonoBehaviour
{
    private FacepunchTransport transport;
    private string joinIdInput = "";

    void Start()
    {
        transport = NetworkManager.Singleton.GetComponent<FacepunchTransport>();

        NetworkManager.Singleton.OnClientConnectedCallback += OnClientConnected;
        NetworkManager.Singleton.OnClientDisconnectCallback += OnClientDisconnected;
    }

    void OnClientConnected(ulong clientId)
    {
        Debug.Log($"[ConnectTest] Client {clientId} connected. " +
                   $"Total connected = {NetworkManager.Singleton.ConnectedClientsList.Count}");
    }

    void OnClientDisconnected(ulong clientId)
    {
        Debug.Log($"[ConnectTest] Client {clientId} disconnected.");
    }

    void OnGUI()
    {
        GUILayout.BeginArea(new Rect(10, 10, 300, 200));

        if (!NetworkManager.Singleton.IsClient && !NetworkManager.Singleton.IsServer)
        {
            if (GUILayout.Button("Host"))
            {
                NetworkManager.Singleton.StartHost();
                Debug.Log($"[ConnectTest] Hosting. My SteamID = {Steamworks.SteamClient.SteamId}");
            }

            GUILayout.Label("Join SteamID:");
            joinIdInput = GUILayout.TextField(joinIdInput);

            if (GUILayout.Button("Join") && ulong.TryParse(joinIdInput, out var targetId))
            {
                // NOTE: confirm this field name against your local transport source —
                // it may be targetSteamId, ConnectToSteamID, or similar depending on version.
                transport.targetSteamId = targetId;
                NetworkManager.Singleton.StartClient();
            }
        }
        else
        {
            GUILayout.Label(NetworkManager.Singleton.IsHost ? "Hosting" : "Connected as client");
            GUILayout.Label($"Connected clients: {NetworkManager.Singleton.ConnectedClientsList.Count}");
        }

        GUILayout.EndArea();
    }
}
