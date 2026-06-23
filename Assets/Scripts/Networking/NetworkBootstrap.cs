using System;
using Steamworks;
using Steamworks.Data;
using Unity.Netcode;
using Netcode.Transports.Facepunch;
using UnityEngine;

/// <summary>
/// Minimal host/join flow for the networking spike.
///
///   Host:   creates a Steam lobby, starts the NGO host, stamps its own SteamId
///           into the lobby metadata.
///   Client: joins the lobby (via a Steam invite / "Join Game"), reads the host
///           SteamId out of the lobby data, points the transport at it, then
///           starts the NGO client.
///
/// Remember: the Steam lobby and the NGO connection are TWO separate things.
/// The lobby is just a discovery room. OnLobbyEntered is where we actually read
/// the host id and dial it — that's the moment the netcode connection begins.
/// </summary>
[RequireComponent(typeof(FacepunchTransport))]
public class NetworkBootstrap : MonoBehaviour
{
    FacepunchTransport transport;
    Lobby? currentLobby;

    const string HostIdKey = "HostSteamId";

    void Start()
    {
        transport = GetComponent<FacepunchTransport>();

        SteamMatchmaking.OnLobbyCreated += OnLobbyCreated;
        SteamMatchmaking.OnLobbyEntered += OnLobbyEntered;
        SteamFriends.OnGameLobbyJoinRequested += OnGameLobbyJoinRequested;

        NetworkManager.Singleton.OnClientConnectedCallback += id =>
            Debug.Log($"[NGO] Client connected: {id}  (total: {NetworkManager.Singleton.ConnectedClients.Count})");
        NetworkManager.Singleton.OnClientDisconnectCallback += id =>
            Debug.Log($"[NGO] Client disconnected: {id}");
    }

    void OnDestroy()
    {
        SteamMatchmaking.OnLobbyCreated -= OnLobbyCreated;
        SteamMatchmaking.OnLobbyEntered -= OnLobbyEntered;
        SteamFriends.OnGameLobbyJoinRequested -= OnGameLobbyJoinRequested;
    }

    // ---- Host ----------------------------------------------------------------

    public async void HostLobby()
    {
        NetworkManager.Singleton.StartHost();
        currentLobby = await SteamMatchmaking.CreateLobbyAsync(maxMembers: 10);
        // Success/failure is handled in OnLobbyCreated.
    }

    void OnLobbyCreated(Result result, Lobby lobby)
    {
        if (result != Result.OK)
        {
            Debug.LogError($"[Steam] Lobby creation failed: {result}");
            return;
        }
        lobby.SetPublic();
        lobby.SetJoinable(true);
        lobby.SetData(HostIdKey, SteamClient.SteamId.Value.ToString());
        Debug.Log("[Steam] Lobby created. Invite a friend via the Steam overlay (Shift+Tab).");
    }

    // ---- Client --------------------------------------------------------------

    // Fires when you accept an invite or click "Join Game" on a friend in Steam.
    async void OnGameLobbyJoinRequested(Lobby lobby, SteamId hostId)
    {
        currentLobby = lobby;
        await lobby.Join();
        // Entry is handled in OnLobbyEntered.
    }

    void OnLobbyEntered(Lobby lobby)
    {
        // The host already started its own session — don't connect to itself.
        if (NetworkManager.Singleton.IsHost) return;

        if (!ulong.TryParse(lobby.GetData(HostIdKey), out var hostId))
        {
            Debug.LogError("[Steam] Lobby had no HostSteamId — did the host stamp it?");
            return;
        }

        transport.targetSteamId = hostId;   // point the pipe at the host
        NetworkManager.Singleton.StartClient();
        Debug.Log($"[NGO] Joining host {hostId} ...");
    }

    // ---- Dev UI (spike only — replace with real lobby UI later) --------------

    void OnGUI()
    {
        GUILayout.BeginArea(new Rect(10, 10, 260, 200));
        var nm = NetworkManager.Singleton;

        if (nm.IsHost || nm.IsClient)
        {
            GUILayout.Label(nm.IsHost ? "Hosting" : "Connected as client");
            GUILayout.Label($"Clients connected: {nm.ConnectedClients.Count}");
            if (GUILayout.Button("Disconnect"))
            {
                nm.Shutdown();
                currentLobby?.Leave();
            }
        }
        else
        {
            if (GUILayout.Button("Host")) HostLobby();
            GUILayout.Label("Client: accept a Steam invite from\nthe host (overlay → invite) to join.");
        }
        GUILayout.EndArea();
    }
}
