using Unity.Netcode;
using UnityEngine;

/// <summary>
/// Gắn vào prefab Survivor (và Hunter, script tự bỏ qua nếu không phải Survivor).
/// Chỉ chạy trên máy của chủ nhân nhân vật: nhấn F gần máy/cổng/hầm để bắt đầu, thanh tiến độ tự chạy;
/// di chuyển hoặc nhấn F lần nữa để buông. Không vẽ giao diện: InteractUI (Canvas) đọc Local.Target.
/// </summary>
[RequireComponent(typeof(PlayerRoleInfo))]
public class PlayerInteractor : NetworkBehaviour
{
    /// <summary>PlayerInteractor của người chơi trên máy này (null nếu chưa spawn).</summary>
    public static PlayerInteractor Local { get; private set; }

    [SerializeField] KeyCode interactKey = KeyCode.F;

    PlayerRoleInfo role;
    SurvivorState state;
    NetInteractable nearest, current;
    float nextScan;

    /// <summary>Thứ đang làm, hoặc thứ gần nhất có thể làm (null nếu không có).</summary>
    public NetInteractable Target => current != null ? current : nearest;
    /// <summary>true khi đang giữ máy/cổng/hầm (thanh tiến độ đang chạy).</summary>
    public bool IsWorking => current != null;

    void Awake()
    {
        role = GetComponent<PlayerRoleInfo>();
        state = GetComponent<SurvivorState>();   // có thể null nếu chưa gắn
    }

    public override void OnNetworkSpawn()
    {
        enabled = IsOwner;   // người khác không chạy Update của script này
        if (IsOwner) Local = this;
    }

    public override void OnNetworkDespawn()
    {
        if (Local == this) Local = null;
    }

    void Update()
    {
        if (!IsSpawned) return;

        bool canAct = role.CurrentRole == Role.Survivor && (state == null || state.IsActive);
        if (!canAct)
        {
            if (current != null) { current.StopUseServerRpc(); current = null; }
            nearest = null;
            return;
        }

        bool moving = new Vector2(Input.GetAxisRaw("Horizontal"), Input.GetAxisRaw("Vertical")).sqrMagnitude > 0.01f;

        if (Time.time >= nextScan)
        {
            nextScan = Time.time + 0.2f;
            nearest = FindNearest();
        }

        if (current != null)
        {
            bool tooFar = current.FlatDistance(transform.position) > current.Range + 0.5f;
            // !CanUse: người khác vừa giữ ghế trước mình (hoặc mình không còn được dùng thứ này) thì thôi, khỏi hiện tiến độ của người khác.
            if (moving || tooFar || current.Done.Value || !current.IsAvailable || !current.CanUse(OwnerClientId) || Input.GetKeyDown(interactKey))
            {
                NetStatsLogger.Mark("interact_stop");
                current.StopUseServerRpc();
                current = null;
            }
            return;
        }

        if (nearest != null && !moving && Input.GetKeyDown(interactKey))
        {
            NetInteractable target = nearest;
            NetStatsLogger.Mark("interact_start_" + target.name);
            target.StartUseServerRpc();
            if (target.IsInstant) nearest = null;   // nhảy hầm: xong ngay, không có trạng thái "đang làm"
            else current = target;
        }
    }

    NetInteractable FindNearest()
    {
        NetInteractable best = null;
        float bestDist = float.MaxValue;
        foreach (NetInteractable it in FindObjectsByType<NetInteractable>(FindObjectsSortMode.None))
        {
            if (!it.IsSpawned || !it.IsAvailable || !it.CanUse(OwnerClientId)) continue;
            float d = it.FlatDistance(transform.position);
            if (d <= it.Range && d < bestDist) { bestDist = d; best = it; }
        }
        return best;
    }
}
