using Unity.Netcode;
using UnityEngine;

public class NetworkTestUI : MonoBehaviour
{
    void OnGUI()
    {
        GUILayout.BeginArea(new Rect(10, 10, 220, 160));
        var nm = NetworkManager.Singleton;
        if (nm == null) { GUILayout.Label("Chưa có NetworkManager"); GUILayout.EndArea(); return; }

        if (!nm.IsClient && !nm.IsServer)
        {
            if (GUILayout.Button("Host")) nm.StartHost();
            if (GUILayout.Button("Client")) nm.StartClient();
        }
        else
        {
            GUILayout.Label(nm.IsHost ? "Host" : "Client");
            if (nm.IsServer) GUILayout.Label("Clients: " + nm.ConnectedClientsIds.Count);
            if (GUILayout.Button("Shutdown")) nm.Shutdown();
        }
        GUILayout.EndArea();
    }
}