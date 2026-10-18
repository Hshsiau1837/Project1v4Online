using System;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.SceneManagement;

public struct LobbyEntry : INetworkSerializable, IEquatable<LobbyEntry>
{
    public ulong ClientId;
    public bool Ready;
    public bool WantsHunter;

    public void NetworkSerialize<T>(BufferSerializer<T> serializer) where T : IReaderWriter
    {
        serializer.SerializeValue(ref ClientId);
        serializer.SerializeValue(ref Ready);
        serializer.SerializeValue(ref WantsHunter);
    }

    public bool Equals(LobbyEntry o) => ClientId == o.ClientId && Ready == o.Ready && WantsHunter == o.WantsHunter;
}

/// <summary>
/// Phòng chờ (scene Lobby). Đặt 1 GameObject trong scene Lobby, gắn NetworkObject + script này.
/// Tối đa 5 chỗ. Server giữ danh sách, client gửi Ready / "Muốn làm Hunter" qua ServerRpc.
/// </summary>
public class LobbyManager : NetworkBehaviour
{
    public const int MaxPlayers = 5;

    [SerializeField] string matchScene = "Match";

    NetworkList<LobbyEntry> entries;
    bool debugAnyCount;

    void Awake()
    {
        entries = new NetworkList<LobbyEntry>();
    }

    public override void OnNetworkSpawn()
    {
        if (!IsServer) return;
        foreach (ulong id in NetworkManager.ConnectedClientsIds) AddClient(id);
        NetworkManager.OnClientConnectedCallback += AddClient;
        NetworkManager.OnClientDisconnectCallback += RemoveClient;
    }

    public override void OnNetworkDespawn()
    {
        if (IsServer && NetworkManager != null)
        {
            NetworkManager.OnClientConnectedCallback -= AddClient;
            NetworkManager.OnClientDisconnectCallback -= RemoveClient;
        }
    }

    int IndexOf(ulong id)
    {
        for (int i = 0; i < entries.Count; i++)
            if (entries[i].ClientId == id) return i;
        return -1;
    }

    void AddClient(ulong id)
    {
        if (IndexOf(id) >= 0) return;
        if (entries.Count >= MaxPlayers)
        {
            Debug.LogWarning("Phòng đã đủ 5 người, từ chối client " + id);
            NetworkManager.DisconnectClient(id);
            return;
        }
        entries.Add(new LobbyEntry { ClientId = id });
    }

    void RemoveClient(ulong id)
    {
        int i = IndexOf(id);
        if (i >= 0) entries.RemoveAt(i);
    }

    [ServerRpc(RequireOwnership = false)]
    void SetReadyServerRpc(bool ready, ServerRpcParams p = default)
    {
        int i = IndexOf(p.Receive.SenderClientId);
        if (i < 0) return;
        LobbyEntry e = entries[i];
        e.Ready = ready;
        entries[i] = e;
    }

    [ServerRpc(RequireOwnership = false)]
    void WantHunterServerRpc(bool want, ServerRpcParams p = default)
    {
        int i = IndexOf(p.Receive.SenderClientId);
        if (i < 0) return;
        LobbyEntry e = entries[i];
        e.WantsHunter = want;
        entries[i] = e;
    }

    void StartMatch()
    {
        if (!IsServer) return;

        int n = entries.Count;
        if (!debugAnyCount && n < MaxPlayers)
        {
            Debug.LogWarning($"Chưa đủ {MaxPlayers} người ({n}). Bật Debug để bắt đầu với số người tùy ý.");
            return;
        }
        for (int i = 0; i < n; i++)
        {
            // Host bấm Start nên không bắt buộc host phải bấm Sẵn sàng.
            if (!entries[i].Ready && entries[i].ClientId != NetworkManager.LocalClientId)
            {
                Debug.LogWarning("Còn người chưa sẵn sàng: client " + entries[i].ClientId);
                return;
            }
        }

        // Chọn Hunter: ưu tiên người tick "Muốn làm Hunter", không ai tick thì random.
        // Chỉ 1 người trong phòng (debug) thì tất cả là Survivor để test máy/cửa.
        ulong hunterId = ulong.MaxValue;
        if (n >= 2)
        {
            var wants = new System.Collections.Generic.List<ulong>();
            var all = new System.Collections.Generic.List<ulong>();
            for (int i = 0; i < n; i++)
            {
                all.Add(entries[i].ClientId);
                if (entries[i].WantsHunter) wants.Add(entries[i].ClientId);
            }
            var pool = wants.Count > 0 ? wants : all;
            hunterId = pool[UnityEngine.Random.Range(0, pool.Count)];
        }

        RoleAssignments.Clear();
        for (int i = 0; i < n; i++)
        {
            ulong id = entries[i].ClientId;
            RoleAssignments.Set(id, id == hunterId ? Role.Hunter : Role.Survivor);
        }

        NetworkManager.SceneManager.LoadScene(matchScene, LoadSceneMode.Single);
    }

    void OnGUI()
    {
        if (!IsSpawned) return;

        GUILayout.BeginArea(new Rect(290, 10, 320, 420));
        GUILayout.Label($"PHÒNG CHỜ ({entries.Count}/{MaxPlayers})");

        for (int i = 0; i < entries.Count; i++)
        {
            LobbyEntry e = entries[i];
            string me = e.ClientId == NetworkManager.LocalClientId ? " (bạn)" : "";
            string ready = e.Ready ? "SẴN SÀNG" : "chờ";
            string hunter = e.WantsHunter ? " [muốn Hunter]" : "";
            GUILayout.Label($"Người chơi #{e.ClientId}{me}: {ready}{hunter}");
        }

        int mine = IndexOf(NetworkManager.LocalClientId);
        if (mine >= 0)
        {
            LobbyEntry my = entries[mine];
            if (GUILayout.Button(my.Ready ? "Hủy sẵn sàng" : "Sẵn sàng")) SetReadyServerRpc(!my.Ready);

            bool want = GUILayout.Toggle(my.WantsHunter, "Tôi muốn làm Hunter");
            if (want != my.WantsHunter) WantHunterServerRpc(want);
        }

        if (IsServer)
        {
            GUILayout.Space(8);
            debugAnyCount = GUILayout.Toggle(debugAnyCount, "Debug: bắt đầu với số người tùy ý");
            if (GUILayout.Button("BẮT ĐẦU TRẬN")) StartMatch();
        }
        else
        {
            GUILayout.Label("Chờ host bắt đầu...");
        }
        GUILayout.EndArea();
    }
}