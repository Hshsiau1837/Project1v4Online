using Unity.Netcode;
using UnityEngine;

public enum SurvivorHealth : byte
{
    Healthy = 0,     // Khỏe
    Injured = 1,     // Bị thương (tuần 3)
    Downed = 2,      // Gục (tuần 3)
    OnChair = 3,     // Trên ghế tên lửa (tuần 3)
    Escaped = 4,     // Đã thoát: gắn cờ, không bị tấn công, không tương tác nữa
    Eliminated = 5   // Bị loại (tuần 3-4)
}

/// <summary>
/// Gắn vào prefab Survivor. Trạng thái do SERVER ghi, mọi máy đọc.
/// Tuần 2 chỉ dùng Healthy và Escaped; tuần 3 thêm Injured/Downed/OnChair/Eliminated.
/// </summary>
public class SurvivorState : NetworkBehaviour
{
    public readonly NetworkVariable<byte> Value = new NetworkVariable<byte>((byte)SurvivorHealth.Healthy);

    public SurvivorHealth Current => (SurvivorHealth)Value.Value;

    /// <summary>Còn hành động được (giải mã, mở cửa, nhảy hầm...).</summary>
    public bool IsActive => Current == SurvivorHealth.Healthy || Current == SurvivorHealth.Injured;

    /// <summary>Đã rời trận (thoát hoặc bị loại): không còn bị tính là "còn sống trên bản đồ".</summary>
    public bool IsOut => Current == SurvivorHealth.Escaped || Current == SurvivorHealth.Eliminated;

    public void ServerSet(SurvivorHealth s)
    {
        if (IsServer) Value.Value = (byte)s;
    }

    public override void OnNetworkSpawn()
    {
        Value.OnValueChanged += OnChanged;
        Apply();
    }

    public override void OnNetworkDespawn()
    {
        Value.OnValueChanged -= OnChanged;
    }

    void OnChanged(byte oldValue, byte newValue) => Apply();

    // Đã thoát/bị loại: ẩn model, khóa di chuyển. Server cũng dùng IsOut để bỏ qua khi tấn công (tuần 3).
    void Apply()
    {
        bool gone = IsOut;
        foreach (Renderer r in GetComponentsInChildren<Renderer>()) r.enabled = !gone;

        NetworkPlayerMovement move = GetComponent<NetworkPlayerMovement>();
        if (move != null) move.enabled = !gone;

        CharacterController cc = GetComponent<CharacterController>();
        if (cc != null && IsOwner) cc.enabled = !gone;
    }
}