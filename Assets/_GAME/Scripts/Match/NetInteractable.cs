using System.Collections.Generic;
using Unity.Netcode;
using UnityEngine;

/// <summary>
/// Lớp cha cho mọi thứ Survivor nhấn F để làm: máy mã hóa, mở cổng, nhảy hầm.
/// Nhấn F một lần là thanh tiến độ tự chạy; đi xa / di chuyển / nhấn F lần nữa thì buông (tiến độ được giữ).
/// Riêng thứ nào đặt IsInstant = true (nhảy hầm) thì nhấn F là xong ngay, không có thanh tiến độ.
/// Client chỉ gửi "bắt đầu / dừng"; SERVER kiểm tra vai, trạng thái, khoảng cách rồi mới cộng tiến độ.
/// </summary>
public abstract class NetInteractable : NetworkBehaviour
{
    [SerializeField] protected float duration = 60f;   // giây nếu 1 người làm; nhiều người thì nhanh hơn
    [SerializeField] float range = 2.5f;
    [SerializeField] Renderer visual;                   // để trống thì tự tìm Renderer

    public readonly NetworkVariable<float> Progress = new NetworkVariable<float>(0f);
    public readonly NetworkVariable<bool> Done = new NetworkVariable<bool>(false);

    readonly HashSet<ulong> users = new HashSet<ulong>();
    readonly List<ulong> toRemove = new List<ulong>();
    /// <summary>Những người đang làm lúc hoàn thành (dùng trong OnCompleted).</summary>
    protected readonly List<ulong> finishers = new List<ulong>();
    float serverProgress;

    public float Range => range;
    public abstract string PromptText { get; }
    public virtual bool IsAvailable => !Done.Value;
    /// <summary>true = nhấn F là xong ngay, không có thanh tiến độ (ví dụ nhảy hầm).</summary>
    public virtual bool IsInstant => false;

    protected virtual void OnCompleted() { }
    protected virtual Color GetColor() => Color.Lerp(Color.red, Color.green, Progress.Value);

    /// <summary>Khoảng cách trên mặt phẳng ngang (bỏ qua chênh lệch độ cao).</summary>
    public float FlatDistance(Vector3 p)
    {
        Vector3 d = p - transform.position;
        d.y = 0f;
        return d.magnitude;
    }

    [ServerRpc(RequireOwnership = false)]
    public void StartUseServerRpc(ServerRpcParams p = default)
    {
        ulong id = p.Receive.SenderClientId;
        if (!IsAvailable) return;
        if (!TryGetSurvivor(id, out Transform t)) return;
        if (FlatDistance(t.position) > range + 0.5f) return;   // dung sai cho độ trễ
        if (IsInstant) { CompleteInstantly(id); return; }
        users.Add(id);
    }

    /// <summary>Hoàn thành ngay trên server cho thứ IsInstant (không qua thanh tiến độ).</summary>
    void CompleteInstantly(ulong id)
    {
        if (Done.Value) return;
        serverProgress = 1f;
        Progress.Value = 1f;
        Done.Value = true;
        finishers.Clear();
        finishers.Add(id);
        OnCompleted();
    }

    [ServerRpc(RequireOwnership = false)]
    public void StopUseServerRpc(ServerRpcParams p = default)
    {
        users.Remove(p.Receive.SenderClientId);
    }

    /// <summary>
    /// Lấy Survivor của clientId nếu hợp lệ. Mặc định phải còn hành động được (Khỏe/Bị thương).
    /// allowDowned = true: cho cả người đang Gục (dùng khi kiểm tra thoát qua cổng, vì người gục vẫn bò ra cổng được).
    /// </summary>
    protected static bool TryGetSurvivor(ulong clientId, out Transform t, bool allowDowned = false)
    {
        t = null;
        NetworkManager nm = NetworkManager.Singleton;
        if (nm == null || !nm.ConnectedClients.TryGetValue(clientId, out var c) || c.PlayerObject == null) return false;

        PlayerRoleInfo info = c.PlayerObject.GetComponent<PlayerRoleInfo>();
        if (info == null || info.CurrentRole != Role.Survivor) return false;

        SurvivorState st = c.PlayerObject.GetComponent<SurvivorState>();
        if (st != null)
        {
            bool ok = st.IsActive || (allowDowned && st.Current == SurvivorHealth.Downed);
            if (!ok) return false;
        }

        t = c.PlayerObject.transform;
        return true;
    }

    protected virtual void Update()
    {
        if (visual == null) visual = GetComponentInChildren<Renderer>();
        if (visual != null) visual.material.color = GetColor();   // mọi máy tự tô màu

        if (!IsServer || Done.Value || users.Count == 0) return;

        toRemove.Clear();
        foreach (ulong id in users)
        {
            if (!IsAvailable || !TryGetSurvivor(id, out Transform t) || FlatDistance(t.position) > range + 0.5f)
                toRemove.Add(id);   // đi xa / rớt mạng / bị đánh / hết hiệu lực -> buông
        }
        foreach (ulong id in toRemove) users.Remove(id);
        if (users.Count == 0) return;

        serverProgress = Mathf.Min(1f, serverProgress + users.Count * Time.deltaTime / duration);
        if (serverProgress - Progress.Value >= 0.01f || serverProgress >= 1f)
            Progress.Value = serverProgress;   // ghi theo bước 1% để đỡ tốn băng thông

        if (serverProgress >= 1f)
        {
            Done.Value = true;
            finishers.Clear();
            finishers.AddRange(users);
            users.Clear();
            OnCompleted();
        }
    }
}