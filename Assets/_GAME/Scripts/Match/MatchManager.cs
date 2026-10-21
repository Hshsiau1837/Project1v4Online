using Unity.Netcode;
using UnityEngine;
using UnityEngine.SceneManagement;

/// <summary>
/// Đặt 1 GameObject trong scene Match, gắn NetworkObject + script này. Mọi quyết định chạy trên SERVER.
/// Tuần 2:
///  - đếm máy xong; đủ máy thì còi vang (mọi máy), kích hoạt 2 cổng, khóa các máy còn lại
///  - chọn ngẫu nhiên 1 trong các hầm; xong 2 máy thì hầm LỘ ra; chỉ còn 1 Survivor sống thì hầm MỞ
///  - đếm người thoát (qua cổng hay hầm đều tính) và gắn cờ Đã thoát cho Survivor đó
///  - host bấm "Về phòng chờ" để chơi trận mới
/// Luật thắng/thua/hòa để tuần 4.
/// </summary>
public class MatchManager : NetworkBehaviour
{
    public static MatchManager Instance { get; private set; }

    public const byte DungeonHidden = 0, DungeonRevealed = 1, DungeonOpen = 2;

    [SerializeField] int machinesRequired = 5;
    [SerializeField] int machinesToRevealDungeon = 2;
    [Tooltip("Kéo 3 hầm vào đây. Để trống thì tự tìm tất cả Dungeon trong scene.")]
    [SerializeField] Dungeon[] dungeons;
    [SerializeField] string lobbyScene = "Lobby";
    [Tooltip("Tùy chọn: AudioSource có clip còi, phát trên mọi máy khi đủ máy.")]
    [SerializeField] AudioSource sirenAudio;

    public readonly NetworkVariable<int> MachinesDone = new NetworkVariable<int>(0);
    public readonly NetworkVariable<int> Escaped = new NetworkVariable<int>(0);
    public readonly NetworkVariable<bool> GatesPowered = new NetworkVariable<bool>(false);
    public readonly NetworkVariable<byte> DungeonPhase = new NetworkVariable<byte>(DungeonHidden);
    /// <summary>Số Survivor còn trên bản đồ (chưa thoát, chưa bị loại, còn kết nối).</summary>
    public readonly NetworkVariable<int> AliveSurvivors = new NetworkVariable<int>(0);
    /// <summary>Số Survivor đã bị loại (hết giờ trên ghế tên lửa...). Luật thắng/thua dùng ở tuần 4.</summary>
    public readonly NetworkVariable<int> Eliminated = new NetworkVariable<int>(0);

    public int MachinesRequired => machinesRequired;
    public int TotalMachines { get; private set; }

    Dungeon chosenDungeon;
    float nextCheck;

    void Awake() { Instance = this; }

    public override void OnNetworkSpawn()
    {
        Instance = this;
        TotalMachines = FindObjectsByType<CipherMachine>(FindObjectsSortMode.None).Length;
        GatesPowered.OnValueChanged += OnGatesPoweredChanged;   // còi: chạy trên mọi máy

        if (IsServer) PickDungeon();
    }

    public override void OnNetworkDespawn()
    {
        GatesPowered.OnValueChanged -= OnGatesPoweredChanged;
        if (Instance == this) Instance = null;
    }

    void OnGatesPoweredChanged(bool oldValue, bool newValue)
    {
        if (newValue && sirenAudio != null) sirenAudio.Play();
    }

    void PickDungeon()
    {
        var pool = new System.Collections.Generic.List<Dungeon>();
        if (dungeons != null)
            foreach (Dungeon d in dungeons) if (d != null) pool.Add(d);
        if (pool.Count == 0) pool.AddRange(FindObjectsByType<Dungeon>(FindObjectsSortMode.None));

        if (pool.Count == 0)
        {
            Debug.LogWarning("MatchManager: scene không có Dungeon nào.");
            return;
        }
        chosenDungeon = pool[Random.Range(0, pool.Count)];
        Debug.Log("Hầm ngục được chọn: " + chosenDungeon.name);
    }

    // ---------------- Chỉ chạy trên server ----------------

    void Update()
    {
        if (!IsServer || !IsSpawned) return;
        if (Time.time < nextCheck) return;
        nextCheck = Time.time + 0.25f;

        int alive = CountAliveSurvivors();
        if (AliveSurvivors.Value != alive) AliveSurvivors.Value = alive;

        // Hầm đã lộ + chỉ còn đúng 1 Survivor sống => mở hầm.
        if (chosenDungeon != null && DungeonPhase.Value == DungeonRevealed && alive == 1)
        {
            chosenDungeon.ServerOpen();
            DungeonPhase.Value = DungeonOpen;
        }
    }

    int CountAliveSurvivors()
    {
        int n = 0;
        foreach (var kv in NetworkManager.ConnectedClients)
        {
            NetworkObject po = kv.Value.PlayerObject;
            if (po == null) continue;
            PlayerRoleInfo info = po.GetComponent<PlayerRoleInfo>();
            if (info == null || info.CurrentRole != Role.Survivor) continue;
            SurvivorState st = po.GetComponent<SurvivorState>();
            if (st != null && st.IsOut) continue;
            n++;
        }
        return n;
    }

    public void OnMachineCompleted()
    {
        if (!IsServer) return;
        MachinesDone.Value++;

        if (MachinesDone.Value >= machinesToRevealDungeon && DungeonPhase.Value == DungeonHidden && chosenDungeon != null)
        {
            chosenDungeon.ServerReveal();
            DungeonPhase.Value = DungeonRevealed;
        }

        if (MachinesDone.Value >= machinesRequired && !GatesPowered.Value)
        {
            GatesPowered.Value = true;   // còi + thông báo trên mọi máy qua biến này
            foreach (ExitGate gate in FindObjectsByType<ExitGate>(FindObjectsSortMode.None))
                gate.SetPowered(true);
        }
    }

    /// <summary>Gắn cờ Đã thoát: ẩn nhân vật, khóa di chuyển, không bị tấn công. Gọi từ cổng và hầm.</summary>
    public void OnSurvivorEscaped(ulong clientId)
    {
        if (!IsServer) return;
        if (!NetworkManager.ConnectedClients.TryGetValue(clientId, out var c) || c.PlayerObject == null) return;

        PlayerRoleInfo info = c.PlayerObject.GetComponent<PlayerRoleInfo>();
        if (info == null || info.CurrentRole != Role.Survivor) return;   // Hunter không thoát

        SurvivorState st = c.PlayerObject.GetComponent<SurvivorState>();
        if (st == null) { Debug.LogError("Prefab Survivor thiếu SurvivorState."); return; }
        if (st.IsOut) return;                                            // đã tính rồi

        st.ServerSet(SurvivorHealth.Escaped);
        Escaped.Value++;
        Debug.Log($"Survivor {clientId} đã thoát. Tổng thoát: {Escaped.Value}");
    }

    /// <summary>Gắn cờ Bị loại: ẩn nhân vật, khóa di chuyển, tăng bộ đếm. Gọi từ ghế tên lửa khi hết giờ.</summary>
    public void OnSurvivorEliminated(ulong clientId)
    {
        if (!IsServer) return;
        if (!NetworkManager.ConnectedClients.TryGetValue(clientId, out var c) || c.PlayerObject == null) return;

        PlayerRoleInfo info = c.PlayerObject.GetComponent<PlayerRoleInfo>();
        if (info == null || info.CurrentRole != Role.Survivor) return;

        SurvivorState st = c.PlayerObject.GetComponent<SurvivorState>();
        if (st == null || st.IsOut) return;                              // đã tính rồi

        st.ServerSet(SurvivorHealth.Eliminated);
        Eliminated.Value++;
        Debug.Log($"Survivor {clientId} bị loại. Tổng bị loại: {Eliminated.Value}");
    }

    /// <summary>Nút "Về phòng chờ" (chỉ host): cả phòng quay lại scene Lobby để chơi trận mới.</summary>
    public void BackToLobby()
    {
        NetworkManager nm = NetworkManager.Singleton;
        if (nm == null || !nm.IsServer) return;
        nm.SceneManager.LoadScene(lobbyScene, LoadSceneMode.Single);
    }
}
