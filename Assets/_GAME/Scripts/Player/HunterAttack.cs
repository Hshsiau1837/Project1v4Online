using System.Collections.Generic;
using Unity.Netcode;
using UnityEngine;

[RequireComponent(typeof(PlayerRoleInfo))]
[RequireComponent(typeof(NetworkPlayerMovement))]
public class HunterAttack : NetworkBehaviour
{
    public static HunterAttack Local { get; private set; }

    [Header("Phím")]
    [SerializeField] int attackMouseButton = 0;
    [SerializeField] KeyCode pickupKey = KeyCode.F;
    [SerializeField] KeyCode dropKey = KeyCode.Q;

    [Header("Đòn đánh")]
    [SerializeField] float reach = 1.8f;
    [Range(10f, 180f)][SerializeField] float halfAngle = 60f;
    [SerializeField] float cooldown = 1.2f;

    [Header("Bế người")]
    [SerializeField] float pickupRange = 2f;
    [SerializeField] float chairRange = 2.5f;
    [Range(0.3f, 1f)][SerializeField] float carrySpeedFactor = 0.85f;

    [Header("Animation")]
    [SerializeField] Animator animator;

    static readonly int AttackParam = Animator.StringToHash("attack");
    static readonly int AttackRecoveryParam = Animator.StringToHash("attack_recovery");
    static readonly int PickupParam = Animator.StringToHash("pickup_survivor");
    static readonly int DropParam = Animator.StringToHash("drop_survivor");
    static readonly int IsCarryingParam = Animator.StringToHash("isCarrying");

    readonly NetworkVariable<bool> isCarrying = new NetworkVariable<bool>(false);

    PlayerRoleInfo role;
    NetworkPlayerMovement move;

    bool hasAttack, hasRecovery, hasPickup, hasDrop, hasIsCarrying;

    float serverNextAttack;
    SurvivorState carried;

    float unlockMovementTime;
    float nextLocalAttack, nextScan;
    bool nearDowned, nearChair;
    string prompt;

    public string Prompt => prompt;

    void Awake()
    {
        role = GetComponent<PlayerRoleInfo>();
        move = GetComponent<NetworkPlayerMovement>();
        if (animator == null) animator = GetComponentInChildren<Animator>();

        if (animator != null)
        {
            foreach (AnimatorControllerParameter p in animator.parameters)
            {
                if (p.nameHash == AttackParam) hasAttack = true;
                if (p.nameHash == AttackRecoveryParam) hasRecovery = true;
                if (p.nameHash == PickupParam) hasPickup = true;
                if (p.nameHash == DropParam) hasDrop = true;
                if (p.nameHash == IsCarryingParam) hasIsCarrying = true;
            }
        }
    }

    public override void OnNetworkSpawn()
    {
        isCarrying.OnValueChanged += OnCarryChanged;
        if (IsOwner)
        {
            Local = this;
            ApplyCarrySpeed(isCarrying.Value);
        }

        if (animator != null && hasIsCarrying)
            animator.SetBool(IsCarryingParam, isCarrying.Value);
    }

    public override void OnNetworkDespawn()
    {
        isCarrying.OnValueChanged -= OnCarryChanged;
        if (Local == this) Local = null;

        // ĐÃ SỬA: Thêm vị trí và góc xoay vào hàm thả để tránh lỗi thiếu tham số
        if (IsServer && carried != null)
            carried.ServerDrop(transform.position, transform.eulerAngles.y);
    }

    void OnCarryChanged(bool oldValue, bool newValue)
    {
        if (IsOwner) ApplyCarrySpeed(newValue);
        if (animator != null && hasIsCarrying)
            animator.SetBool(IsCarryingParam, newValue);
    }

    void ApplyCarrySpeed(bool carrying)
    {
        if (move != null) move.SetSpeedMultiplier(carrying ? carrySpeedFactor : 1f);
    }

    void Update()
    {
        if (!IsSpawned) return;

        if (IsServer && isCarrying.Value && (carried == null || carried.Current != SurvivorHealth.Carried))
        {
            carried = null;
            isCarrying.Value = false;
        }

        if (!IsOwner || role.CurrentRole != Role.Hunter) return;

        // --- KHÓA TOÀN BỘ THAO TÁC KHI ĐANG LAU KIẾM ---
        if (Time.time < unlockMovementTime)
        {
            move.SetLocked(true);
            prompt = null; // Tắt luôn chữ gợi ý trên màn hình
            return; // Dừng chạy code tại đây, không cho đánh, bế hay thả gì hết!
        }
        else if (!isCarrying.Value)
        {
            move.SetLocked(false);
        }

        // --- Đánh ---
        if (!isCarrying.Value && Input.GetMouseButtonDown(attackMouseButton) && Time.time >= nextLocalAttack)
        {
            nextLocalAttack = Time.time + cooldown;
            AttackServerRpc();
        }

        if (Time.time >= nextScan)
        {
            nextScan = Time.time + 0.2f;
            ScanNearby();
        }

        // --- Đang bế người ---
        if (isCarrying.Value)
        {
            if (nearChair && Input.GetKeyDown(pickupKey)) PlaceOnChairServerRpc();
            if (Input.GetKeyDown(dropKey)) DropServerRpc();
            prompt = nearChair
                ? $"[{pickupKey}] Đặt lên ghế tên lửa   [{dropKey}] Thả xuống"
                : $"Mang người tới ghế tên lửa   [{dropKey}] Thả xuống";
            return;
        }

        // --- Bế người đang Gục ---
        if (nearDowned)
        {
            prompt = $"[{pickupKey}] Bế người đang gục";
            if (Input.GetKeyDown(pickupKey))
            {
                PickUpServerRpc();
            }
        }
        else
        {
            prompt = null;
        }
    }

    void ScanNearby()
    {
        nearDowned = false;
        nearChair = false;

        foreach (SurvivorState st in FindObjectsByType<SurvivorState>(FindObjectsSortMode.None))
        {
            if (st.Current == SurvivorHealth.Downed && FlatDistance(st.transform.position) <= pickupRange)
            {
                nearDowned = true;
                break;
            }
        }
        foreach (RocketChair c in FindObjectsByType<RocketChair>(FindObjectsSortMode.None))
        {
            if (c.IsSpawned && c.IsFree && FlatDistance(c.transform.position) <= chairRange)
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

    bool ServerIsValidHunter() => IsServer && role.CurrentRole == Role.Hunter;

    IEnumerable<SurvivorState> ServerSurvivors()
    {
        foreach (var kv in NetworkManager.ConnectedClients)
        {
            NetworkObject po = kv.Value.PlayerObject;
            if (po == null) continue;
            SurvivorState st = po.GetComponent<SurvivorState>();
            if (st != null) yield return st;
        }
    }

    [ServerRpc]
    void AttackServerRpc()
    {
        if (!ServerIsValidHunter() || isCarrying.Value) return;
        if (Time.time < serverNextAttack) return;
        serverNextAttack = Time.time + cooldown * 0.9f;

        SurvivorState target = FindHitTarget();
        bool isHit = target != null && target.ServerHit();

        PlayAttackClientRpc(isHit);
    }

    SurvivorState FindHitTarget()
    {
        SurvivorState best = null;
        float bestDist = float.MaxValue;

        foreach (SurvivorState st in ServerSurvivors())
        {
            if (!st.IsActive) continue;

            Vector3 to = st.transform.position - transform.position;
            to.y = 0f;
            float d = to.magnitude;
            if (d > reach) continue;
            if (d > 0.05f && Vector3.Angle(transform.forward, to) > halfAngle) continue;

            if (d < bestDist) { bestDist = d; best = st; }
        }
        return best;
    }

    [ClientRpc]
    void PlayAttackClientRpc(bool isHit)
    {
        if (animator == null) return;

        // BƯỚC 1: LUÔN LUÔN chạy hoạt ảnh vung vũ khí chém trước
        if (hasAttack) animator.SetTrigger(AttackParam);

        if (isHit)
        {
            // Nếu đánh TRÚNG: Tổng thời gian đứng im = Thời gian chém trúng + Thời gian lau kiếm (VD: 1.5s)
            if (IsOwner) unlockMovementTime = Time.time + 5.5f;

            // BƯỚC 2: Gọi Coroutine để chờ vung dao xong mới chạy anim Lau kiếm
            StartCoroutine(WaitAndPlayRecovery());
        }
        else
        {
            // Nếu đánh HỤT: Đứng im theo độ dài của anim chém hụt (VD: 0.8s)
            if (IsOwner) unlockMovementTime = Time.time + 0.8f;
        }
    }

    // Hàm chờ (Coroutine)
    System.Collections.IEnumerator WaitAndPlayRecovery()
    {
        // Số 0.3f là độ trễ từ lúc bấm đánh đến lúc hoạt ảnh chém chạm vào người Survivor.
        // Bạn hãy tăng/giảm con số này (ví dụ 0.2f hoặc 0.4f) sao cho khớp với Animation của bạn nhất.
        yield return new WaitForSeconds(0.3f);

        // Sau khi chờ xong, kích hoạt anim Lau kiếm
        if (animator != null && hasRecovery)
        {
            animator.SetTrigger(AttackRecoveryParam);
        }
    }

    [ServerRpc]
    void PickUpServerRpc()
    {
        if (!ServerIsValidHunter() || isCarrying.Value) return;

        SurvivorState best = null;
        float bestDist = pickupRange + 0.5f;
        foreach (SurvivorState st in ServerSurvivors())
        {
            if (st.Current != SurvivorHealth.Downed) continue;
            float d = FlatDistance(st.transform.position);
            if (d <= bestDist) { bestDist = d; best = st; }
        }

        if (best != null && best.ServerPickUp(OwnerClientId))
        {
            carried = best;
            isCarrying.Value = true;
            PlayActionAnimClientRpc(true);
        }
    }

    [ServerRpc]
    void PlaceOnChairServerRpc()
    {
        if (!ServerIsValidHunter() || !isCarrying.Value || carried == null) return;

        RocketChair chair = null;
        float bestDist = chairRange + 0.5f;
        foreach (RocketChair c in FindObjectsByType<RocketChair>(FindObjectsSortMode.None))
        {
            if (!c.IsSpawned || !c.IsFree) continue;
            float d = FlatDistance(c.transform.position);
            if (d <= bestDist) { bestDist = d; chair = c; }
        }

        if (chair != null && chair.ServerPlace(carried))
        {
            carried = null;
            isCarrying.Value = false;
            PlayActionAnimClientRpc(false);
        }
    }

    [ServerRpc]
    void DropServerRpc()
    {
        if (!ServerIsValidHunter() || !isCarrying.Value) return;

        if (carried != null)
        {
            Vector3 dropPos = transform.position + transform.forward * 1f;
            dropPos.y = transform.position.y;
            // ĐÃ SỬA: Truyền đủ 2 tham số
            carried.ServerDrop(dropPos, transform.eulerAngles.y);
        }

        carried = null;
        isCarrying.Value = false;
        PlayActionAnimClientRpc(false);
    }

    [ClientRpc]
    void PlayActionAnimClientRpc(bool isPickup)
    {
        if (animator == null) return;

        if (isPickup && hasPickup)
            animator.SetTrigger(PickupParam);
        else if (!isPickup && hasDrop)
            animator.SetTrigger(DropParam);
    }
}