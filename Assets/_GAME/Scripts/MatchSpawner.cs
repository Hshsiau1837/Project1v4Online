using System.Collections.Generic;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.SceneManagement;

/// <summary>
/// Gắn vào GameObject NetworkManager (object này không bị hủy khi đổi scene).
/// Khi mọi máy đã tải xong scene Match, server spawn nhân vật đúng prefab theo vai.
/// Yêu cầu: ô Player Prefab của NetworkManager để TRỐNG.
/// </summary>
public class MatchSpawner : MonoBehaviour
{
    [SerializeField] GameObject survivorPrefab;
    [SerializeField] GameObject hunterPrefab;
    [SerializeField] string matchScene = "Match";

    NetworkManager nm;

    void Start()
    {
        nm = GetComponent<NetworkManager>();
        if (nm == null) nm = NetworkManager.Singleton;
        if (nm == null)
        {
            Debug.LogError("MatchSpawner: không thấy NetworkManager (hãy gắn script này vào object NetworkManager).");
            enabled = false;
            return;
        }
        nm.OnServerStarted += HookSceneEvents;
        if (nm.IsServer && nm.SceneManager != null) HookSceneEvents();
    }

    void OnDestroy()
    {
        if (nm != null) nm.OnServerStarted -= HookSceneEvents;
    }

    void HookSceneEvents()
    {
        nm.SceneManager.OnLoadEventCompleted -= OnLoadCompleted;
        nm.SceneManager.OnLoadEventCompleted += OnLoadCompleted;
    }

    void OnLoadCompleted(string sceneName, LoadSceneMode mode, List<ulong> completed, List<ulong> timedOut)
    {
        if (sceneName != matchScene) return;
        foreach (ulong id in completed) SpawnFor(id);
        foreach (ulong id in timedOut) Debug.LogWarning("Client " + id + " tải scene quá lâu, không spawn.");
    }

    void SpawnFor(ulong clientId)
    {
        if (nm.ConnectedClients.TryGetValue(clientId, out var c) && c.PlayerObject != null) return;

        Role role = RoleAssignments.Get(clientId);
        GameObject prefab = role == Role.Hunter ? hunterPrefab : survivorPrefab;
        if (prefab == null) { Debug.LogError("MatchSpawner: chưa gán prefab cho vai " + role); return; }

        GameObject go = Instantiate(prefab);
        go.GetComponent<NetworkObject>().SpawnAsPlayerObject(clientId, true);
        Debug.Log($"Spawn client {clientId} với vai {role}");
    }
}