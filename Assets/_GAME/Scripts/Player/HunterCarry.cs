using Unity.Netcode;
using UnityEngine;

/// <summary>
/// Hunter bế người đang Gục, thả xuống hoặc đặt lên ghế tên lửa.
/// Bế / thả / đặt đều khóa Hunter (không đi, không nhảy cửa sổ, không đánh) trong lockOnPickup / lockOnDrop / lockOnPlace giây.
/// Survivor bị bế tự bám theo Hunter, xem SurvivorState.FollowCarrier.
/// </summary>
[RequireComponent(typeof(PlayerRoleInfo))]
[RequireComponent(typeof(NetworkPlayerMovement))]
[RequireComponent(typeof(HunterAttack))]
public class HunterCarry : NetworkBehaviour
{
    public static HunterCarry Local { get; private set; }

    [Header("Phím")]
    [SerializeField] KeyCode pickupKey = KeyCode.F;
    [SerializeField] KeyCode dropKey = KeyCode.Q;

    [Header("Khóa sau khi bế / thả / đặt lên ghế (giây). 0 = không khóa. Chỉnh cho bằng độ dài animation.")]
    [SerializeField] float lockOnPickup = 1.0f;
    [SerializeField] float lockOnDrop = 0.8f;
    [SerializeField] float lockOnPlace = 1.0f;

    [Header("Bế người")]
    [SerializeField] float pickupRange = 2f;
    [SerializeField] float chairRange = 2.5f;
    [Range(0.3f, 1f)][SerializeField] float carrySpeedFactor = 0.85f;

    [Header("Animation")]
    [SerializeField] Animator animator;

    static readonly int PickupParam = Animator.StringToHash("pickup_survivor");
    static readonly int DropParam = Animator.StringToHash("drop_survivor");
    static readonly int IsCarryingParam = Animator.StringToHash("isCarrying");

    const byte ActionPickup = 0, ActionDrop = 1, ActionPlace = 2;

    readonly NetworkVariable<bool> isCarrying = new NetworkVariable<bool>(false);

    PlayerRoleInfo role;
    NetworkPlayerMovement move;
    HunterAttack attack;
    bool hasPickup, hasDrop, hasIsCarrying;

    SurvivorState carried;   // server
    float nextScan;
    bool nearDowned, nearChair;
    string prompt;

    public bool IsCarrying => isCarrying.Value;
    public string Prompt => prompt;

    void Awake()
    {
        role = GetComponent<PlayerRoleInfo>();
        move = GetComponent<NetworkPlayerMovement>();
        attack = GetComponent<HunterAttack>();
        if (animator == null) animator = GetComponentInChildren<Animator>();

        if (animator == null) return;
        foreach (AnimatorControllerParameter p in animator.parameters)
        {
            if (p.nameHash == PickupParam) hasPickup = true;
            if (p.nameHash == DropParam) hasDrop = true;
            if (p.nameHash == IsCarryingParam) hasIsCarrying = true;
        }
    }

    public override void OnNetworkSpawn()
    {
        isCarrying.OnValueChanged += OnCarryChanged;
        if (IsOwner)
        {
            Local = this;
            move.SetSpeedMultiplier(isCarrying.Value ? carrySpeedFactor : 1f);
        }
        if (animator != null && hasIsCarrying) animator.SetBool(IsCarryingParam, isCarrying.Value);
    }

    public override void OnNetworkDespawn()
    {
        isCarrying.OnValueChanged -= OnCarryChanged;
        if (Local == this) Local = null;

        // Hunter rớt mạng giữa lúc đang bế: thả người xuống tại chỗ.
        if (IsServer && carried != null) carried.ServerDrop(transform.position, transform.eulerAngles.y);
    }

    void OnCarryChanged(bool oldValue, bool newValue)
    {
        if (IsOwner) move.SetSpeedMultiplier(newValue ? carrySpeedFactor : 1f);
        if (animator != null && hasIsCarrying) animator.SetBool(IsCarryingParam, newValue);
    }

    void Update()
    {
        if (!IsSpawned) return;

        // Server: người bị bế đổi trạng thái từ nơi khác (rớt mạng...) thì thôi bế.
        if (IsServer && isCarrying.Value && (carried == null || carried.Current != SurvivorHealth.Carried))
        {
            carried = null;
            isCarrying.Value = false;
        }

        if (!IsOwner || role.CurrentRole != Role.Hunter) return;

        // Đang làm động tác: đứng yên, không thao tác gì được.
        if (move.IsLocked) { prompt = null; return; }

        if (Time.time >= nextScan)
        {
            nextScan = Time.time + 0.2f;
            ScanNearby();
        }

        if (isCarrying.Value)
        {
            if (nearChair && Input.GetKeyDown(pickupKey)) PlaceOnChairServerRpc();
            if (Input.GetKeyDown(dropKey)) DropServerRpc();
            prompt = nearChair
                ? $"[{pickupKey}] Đặt lên ghế tên lửa   [{dropKey}] Thả xuống"
                : $"Mang người tới ghế tên lửa   [{dropKey}] Thả xuống";
            return;
        }

        prompt = nearDowned ? $"[{pickupKey}] Bế người đang gục" : null;
        if (nearDowned && Input.GetKeyDown(pickupKey)) PickUpServerRpc();
    }

    void ScanNearby()
    {
        nearDowned = false;
        nearChair = false;

        foreach (SurvivorState st in SurvivorState.All)
        {
            if (st.Current == SurvivorHealth.Downed && FlatDistance(st.transform.position) <= pickupRange)
            {
                nearDowned = true;
                break;
            }
        }
        foreach (RocketChair c in RocketChair.All)
        {
            if (c.IsFree && FlatDistance(c.transform.position) <= chairRange)
            {
                nearChair = true;
                break;
            }
        }
    }

    float FlatDistance(Vector3 p)
    {
        Vector3 d = p - transform.position;
        d.y = 0f;
        return d.magnitude;
    }

    [ServerRpc]
    void PickUpServerRpc()
    {
        if (!attack.ServerCanAct || isCarrying.Value) return;

        SurvivorState best = null;
        float bestDist = pickupRange + 0.5f;
        foreach (SurvivorState st in SurvivorState.All)
        {
            if (st.Current != SurvivorHealth.Downed) continue;
            float d = FlatDistance(st.transform.position);
            if (d <= bestDist) { bestDist = d; best = st; }
        }

        if (best == null || !best.ServerPickUp(NetworkObject)) return;

        carried = best;
        isCarrying.Value = true;
        attack.ServerBeginAction(lockOnPickup);
        PlayActionClientRpc(ActionPickup);
    }

    [ServerRpc]
    void PlaceOnChairServerRpc()
    {
        if (!attack.ServerCanAct || !isCarrying.Value || carried == null) return;

        RocketChair chair = null;
        float bestDist = chairRange + 0.5f;
        foreach (RocketChair c in RocketChair.All)
        {
            if (!c.IsFree) continue;
            float d = FlatDistance(c.transform.position);
            if (d <= bestDist) { bestDist = d; chair = c; }
        }

        if (chair == null || !chair.ServerPlace(carried)) return;

        carried = null;
        isCarrying.Value = false;
        attack.ServerBeginAction(lockOnPlace);
        PlayActionClientRpc(ActionPlace);
    }

    [ServerRpc]
    void DropServerRpc()
    {
        if (!attack.ServerCanAct || !isCarrying.Value) return;

        if (carried != null)
        {
            Vector3 dropPos = transform.position + transform.forward * 1f;
            dropPos.y = transform.position.y;
            carried.ServerDrop(dropPos, transform.eulerAngles.y);
        }

        carried = null;
        isCarrying.Value = false;
        attack.ServerBeginAction(lockOnDrop);
        PlayActionClientRpc(ActionDrop);
    }

    [ClientRpc]
    void PlayActionClientRpc(byte action)
    {
        // Bế dùng pickup_survivor; thả và đặt lên ghế dùng drop_survivor.
        if (animator != null)
        {
            if (action == ActionPickup && hasPickup) animator.SetTrigger(PickupParam);
            else if (action != ActionPickup && hasDrop) animator.SetTrigger(DropParam);
        }

        if (IsOwner)
            move.LockFor(action == ActionPickup ? lockOnPickup : action == ActionDrop ? lockOnDrop : lockOnPlace);
    }
}
