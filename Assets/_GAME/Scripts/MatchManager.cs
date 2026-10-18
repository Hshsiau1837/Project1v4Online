using Unity.Netcode;
using UnityEngine;

/// <summary>
/// Đặt 1 GameObject trong scene Match, gắn NetworkObject + script này.
/// Tuần 2: đếm máy xong, kích hoạt cửa, đếm số người thoát. Luật thắng/thua để tuần 4.
/// </summary>
public class MatchManager : NetworkBehaviour
{
    public static MatchManager Instance { get; private set; }

    [SerializeField] int machinesRequired = 5;

    public readonly NetworkVariable<int> MachinesDone = new NetworkVariable<int>(0);
    public readonly NetworkVariable<int> Escaped = new NetworkVariable<int>(0);
    public readonly NetworkVariable<bool> GatesPowered = new NetworkVariable<bool>(false);

    int totalMachines;

    void Awake() { Instance = this; }

    public override void OnNetworkSpawn()
    {
        totalMachines = FindObjectsByType<CipherMachine>(FindObjectsSortMode.None).Length;
    }

    // ---- Chỉ gọi trên server ----
    public void OnMachineCompleted()
    {
        if (!IsServer) return;
        MachinesDone.Value++;

        if (MachinesDone.Value >= machinesRequired && !GatesPowered.Value)
        {
            GatesPowered.Value = true;   // còi báo: mọi máy hiện thông báo qua biến này
            foreach (ExitGate gate in FindObjectsByType<ExitGate>(FindObjectsSortMode.None))
                gate.SetPowered(true);
        }
    }

    public void OnSurvivorEscaped(ulong clientId)
    {
        if (!IsServer) return;
        if (!NetworkManager.ConnectedClients.TryGetValue(clientId, out var c) || c.PlayerObject == null) return;

        Escaped.Value++;
        Debug.Log($"Survivor {clientId} đã thoát. Tổng thoát: {Escaped.Value}");
        c.PlayerObject.Despawn(true);   // tuần 4 sẽ thay bằng màn hình kết quả / khán giả
    }

    void OnGUI()
    {
        if (!IsSpawned) return;
        float x = Screen.width / 2f - 220;
        GUI.Label(new Rect(x, 10, 440, 25),
            $"Máy mã hóa: {MachinesDone.Value}/{machinesRequired} (có {totalMachines} máy)   Đã thoát: {Escaped.Value}");
        if (GatesPowered.Value)
            GUI.Label(new Rect(x, 32, 440, 25), ">>> ĐÃ ĐỦ MÁY - CỔNG THOÁT ĐƯỢC KÍCH HOẠT <<<");
    }
}