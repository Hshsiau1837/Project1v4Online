using Unity.Netcode;
using UnityEngine;
using Unity.Netcode.Components;
public enum SurvivorHealth : byte
{
    Healthy = 0,     // Khỏe
    Injured = 1,     // Bị thương (trúng 1 đòn)
    Downed = 2,      // Gục (trúng đòn thứ 2): bò chậm, không làm được gì
    OnChair = 3,     // Bị trói trên ghế tên lửa, đếm ngược rồi bị loại
    Escaped = 4,     // Đã thoát: gắn cờ, không bị tấn công, không tương tác nữa
    Eliminated = 5,  // Bị loại
    Carried = 6      // Đang bị Hunter bế (tuần 3)
}

/// <summary>
/// Gắn vào prefab Survivor. Trạng thái do SERVER ghi, mọi máy đọc.
/// Chuỗi: Khỏe -> Bị thương -> Gục -> (Hunter bế) -> Trên ghế -> được cứu (về Bị thương) hoặc hết giờ (bị loại).
/// Hồi máu: Gục -> Bị thương -> Khỏe.
/// Mọi máy tự suy ra hiệu ứng (đổi màu, animation) từ trạng thái; chỉ máy chủ nhân nhân vật mới bị khóa/giảm tốc,
/// vì di chuyển là Owner-authoritative.
/// </summary>
public class SurvivorState : NetworkBehaviour
{
    public const ulong NoCarrier = ulong.MaxValue;

    public readonly NetworkVariable<byte> Value = new NetworkVariable<byte>((byte)SurvivorHealth.Healthy);
    /// <summary>ClientId của Hunter đang bế (chỉ có nghĩa khi trạng thái là Carried).</summary>
    public readonly NetworkVariable<ulong> CarrierId = new NetworkVariable<ulong>(NoCarrier);

    [Header("Hiệu ứng trên mọi máy (đổi màu nhân vật)")]
    [SerializeField] Color injuredColor = new Color(1f, 0.6f, 0.2f);
    [SerializeField] Color downedColor = new Color(0.85f, 0.1f, 0.1f);
    [SerializeField] Color chairColor = new Color(0.6f, 0.3f, 0.85f);

    [Header("Gục (chỉ máy chủ nhân)")]
    [Range(0.1f, 1f)]
    [SerializeField] float downedSpeedFactor = 0.3f;

    [Header("Bị bế: vị trí so với Hunter (chỉ máy chủ nhân)")]
    [SerializeField] Vector3 carryOffset = new Vector3(0f, 1f, 0.4f);   // x phải, y lên, z ra trước

    [Header("Server")]
    [Tooltip("Sau khi trúng đòn, trong ngần này giây không bị trúng thêm.")]
    [SerializeField] float hitImmunity = 1f;

    [Header("Test nhanh (chỉ Host tự thử)")]
    [Tooltip("Bật để thử trạng thái: F5 = bị đánh 1 đòn, F6 = hồi máu 1 bậc, F7 = về Khỏe.")]
    [SerializeField] bool debugKeys = false;

    static readonly int HealthParam = Animator.StringToHash("healthState");
    static readonly int BaseColorId = Shader.PropertyToID("_BaseColor");
    static readonly int ColorId = Shader.PropertyToID("_Color");

    Renderer[] renderers;
    MaterialPropertyBlock block;
    Animator animator;
    bool hasHealthParam;
    NetworkPlayerMovement move;
    CharacterController cc;
    NetworkTransform netTransform;
    float lastHitTime = -999f;       // server
    Vector3 snapPos;                 // owner: vị trí cần dịch chuyển tới (ghế / chỗ được cứu)
    float snapYaw;
    bool snapPending;

    public SurvivorHealth Current => (SurvivorHealth)Value.Value;

    /// <summary>Còn hành động được (giải mã, mở cổng, nhảy hầm, cứu, hồi máu...).</summary>
    public bool IsActive => Current == SurvivorHealth.Healthy || Current == SurvivorHealth.Injured;

    /// <summary>Đã rời trận (thoát hoặc bị loại): không còn bị tính là "còn sống trên bản đồ".</summary>
    public bool IsOut => Current == SurvivorHealth.Escaped || Current == SurvivorHealth.Eliminated;

    void Awake()
    {
        renderers = GetComponentsInChildren<Renderer>(true);
        block = new MaterialPropertyBlock();
        animator = GetComponentInChildren<Animator>();
        move = GetComponent<NetworkPlayerMovement>();
        cc = GetComponent<CharacterController>();
        netTransform = GetComponent<NetworkTransform>();
        if (animator != null)
        {
            foreach (AnimatorControllerParameter p in animator.parameters)
                if (p.nameHash == HealthParam) { hasHealthParam = true; break; }
        }
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

    // ======================= SERVER =======================

    public void ServerSet(SurvivorHealth s)
    {
        if (IsServer) Value.Value = (byte)s;
    }

    /// <summary>Hunter đánh trúng: Khỏe -> Bị thương -> Gục. Trả về true nếu có tác dụng.</summary>
    public bool ServerHit()
    {
        if (!IsServer || Time.time - lastHitTime < hitImmunity) return false;
        switch (Current)
        {
            case SurvivorHealth.Healthy:
                lastHitTime = Time.time;
                ServerSet(SurvivorHealth.Injured);
                return true;
            case SurvivorHealth.Injured:
                lastHitTime = Time.time;
                ServerSet(SurvivorHealth.Downed);
                return true;
            default:
                return false;
        }
    }

    /// <summary>Hồi máu 1 bậc: Gục -> Bị thương, Bị thương -> Khỏe.</summary>
    public bool ServerHeal()
    {
        if (!IsServer) return false;
        switch (Current)
        {
            case SurvivorHealth.Downed: ServerSet(SurvivorHealth.Injured); return true;
            case SurvivorHealth.Injured: ServerSet(SurvivorHealth.Healthy); return true;
            default: return false;
        }
    }

    /// <summary>Hunter bắt đầu bế người đang Gục.</summary>
    public bool ServerPickUp(ulong hunterClientId)
    {
        if (!IsServer || Current != SurvivorHealth.Downed) return false;
        CarrierId.Value = hunterClientId;
        ServerSet(SurvivorHealth.Carried);
        return true;
    }

    /// <summary>Hunter thả người đang bế: về lại trạng thái Gục tại chỗ.</summary>
    // Thêm tham số truyền vị trí và hướng thả
    public void ServerDrop(Vector3 dropPos, float dropYaw)
    {
        if (!IsServer || Current != SurvivorHealth.Carried) return;
        CarrierId.Value = NoCarrier;
        SendSnap(dropPos, dropYaw); // Ép Survivor dịch chuyển đúng vị trí này
        ServerSet(SurvivorHealth.Downed);
    }

    /// <summary>Đặt lên ghế tên lửa: dịch chuyển tới ghế rồi khóa di chuyển.</summary>
    public void ServerPutOnChair(Vector3 pos, float yaw)
    {
        if (!IsServer) return;
        CarrierId.Value = NoCarrier;
        SendSnap(pos, yaw);
        ServerSet(SurvivorHealth.OnChair);
    }

    /// <summary>Được cứu khỏi ghế: đứng ở chỗ pos, trạng thái Bị thương.</summary>
    public void ServerRescue(Vector3 pos, float yaw)
    {
        if (!IsServer || Current != SurvivorHealth.OnChair) return;
        SendSnap(pos, yaw);
        ServerSet(SurvivorHealth.Injured);
    }

    void SendSnap(Vector3 pos, float yaw)
    {
        var p = new ClientRpcParams
        {
            Send = new ClientRpcSendParams { TargetClientIds = new[] { OwnerClientId } }
        };
        SnapClientRpc(pos, yaw, p);
    }

    // ======================= CLIENT =======================

    [ClientRpc]
    void SnapClientRpc(Vector3 pos, float yaw, ClientRpcParams rpcParams = default)
    {
        if (!IsOwner) return;
        snapPos = pos;
        snapYaw = yaw;
        snapPending = true;
        TrySnap();
    }

    // Chỉ dịch chuyển khi không còn bị bế, để tránh bị "kéo ngược" về chỗ Hunter do thứ tự gói tin.
    void TrySnap()
    {
        if (!snapPending || move == null || Current == SurvivorHealth.Carried) return;
        move.SnapTo(snapPos, snapYaw);
        snapPending = false;
    }

    void Update()
    {
        if (!IsSpawned) return;
        // Đưa ra ngoài if (IsOwner) để mọi máy tự cập nhật vị trí bám theo sát Hunter
        FollowCarrier();
        if (IsOwner)
        {
            TrySnap();
        }

        

        if (debugKeys && IsServer && IsOwner)
        {
            if (Input.GetKeyDown(KeyCode.F5)) ServerHit();
            if (Input.GetKeyDown(KeyCode.F6)) ServerHeal();
            if (Input.GetKeyDown(KeyCode.F7)) ServerSet(SurvivorHealth.Healthy);
        }
    }

    // Đang bị bế: máy chủ nhân tự bám theo Hunter (di chuyển là Owner-authoritative nên chỉ owner đặt được vị trí).
    void FollowCarrier()
    {
        // Đã thay số 0 thành NoCarrier
        if (Current != SurvivorHealth.Carried || CarrierId.Value == NoCarrier) return;

        if (NetworkManager.Singleton.SpawnManager.SpawnedObjects.TryGetValue(CarrierId.Value, out NetworkObject hunter))
        {
            if (cc != null) cc.enabled = false;

            // Ép tắt đồng bộ mạng của Unity để không bị giật lại vị trí cũ
            var netTransform = GetComponent<Unity.Netcode.Components.NetworkTransform>();
            if (netTransform != null) netTransform.enabled = false;

            Vector3 carryOffset = new Vector3(0f, 1.5f, 0.5f);
            transform.position = hunter.transform.position + hunter.transform.rotation * carryOffset;
            transform.rotation = hunter.transform.rotation;
        }
    }

    // Chạy trên MỌI máy mỗi khi trạng thái đổi.
    void Apply()
    {
        SurvivorHealth s = Current;
        bool gone = IsOut;

        // Đã thoát/bị loại: ẩn model, khóa di chuyển. Server cũng dùng IsOut để bỏ qua khi tấn công.
        foreach (Renderer r in renderers)
            if (r != null) r.enabled = !gone;
        if (move != null) move.enabled = !gone;

        ApplyTint(s);
        if (animator != null && hasHealthParam) animator.SetInteger(HealthParam, (int)s);

        if (!IsOwner) return;

        if (move != null)
        {
            move.SetLocked(s == SurvivorHealth.OnChair || s == SurvivorHealth.Carried);
            move.SetSpeedMultiplier(s == SurvivorHealth.Downed ? downedSpeedFactor : 1f);
        }
        // Đang bị bế thì tắt CharacterController để tự bám theo Hunter được.
        if (cc != null) cc.enabled = !(gone || s == SurvivorHealth.Carried || s == SurvivorHealth.OnChair);
        var netTransform = GetComponent<Unity.Netcode.Components.NetworkTransform>();
        if (netTransform != null)
        {
            netTransform.enabled = !(s == SurvivorHealth.Carried);
        }

        TrySnap();
    }

    void ApplyTint(SurvivorHealth s)
    {
        Color c;
        switch (s)
        {
            case SurvivorHealth.Injured: c = injuredColor; break;
            case SurvivorHealth.Downed:
            case SurvivorHealth.Carried: c = downedColor; break;
            case SurvivorHealth.OnChair: c = chairColor; break;
            default: c = Color.white; break;
        }

        foreach (Renderer r in renderers)
        {
            if (r == null) continue;
            r.GetPropertyBlock(block);
            block.SetColor(BaseColorId, c);   // URP Lit
            block.SetColor(ColorId, c);       // shader cũ
            r.SetPropertyBlock(block);
        }
    }
}
