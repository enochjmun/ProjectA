using Unity.Netcode;
using UnityEngine;

public class DevConnectUI : MonoBehaviour
{
    void OnGUI()
    {
        GUILayout.BeginArea(new Rect(10, 10, 200, 100));
        var nm = NetworkManager.Singleton;
        if (!nm.IsClient && !nm.IsServer)
        {
            if (GUILayout.Button("Host")) nm.StartHost();
            if (GUILayout.Button("Client")) nm.StartClient();
        }
        else GUILayout.Label(nm.IsServer ? "HOST" : "CLIENT");
        GUILayout.EndArea();
    }
}