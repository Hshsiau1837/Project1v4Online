using Unity.Netcode;
using UnityEngine;

/// <summary>
/// Đòn đánh của Hunter. Việc bế / thả / đặt lên ghế nằm ở HunterCarry.
/// Server kiểm tra tầm và góc; owner bị khóa di chuyển trong lúc vung và lau kiếm.
/// </summary>
[RequireComponent(typeof(PlayerRoleInfo))]
[RequireComponent(typeof(NetworkPlayerMovement))]
public class HunterAttack : NetworkBehaviour
{
    // Server nhả khóa sớm 10% để bù độ trễ mạng, tránh từ chối nhầm yêu cầu kế tiếp của owner.
    public const float ServerLockSlack = 0.9f;

    [Header("Phím")]
    [SerializeField] int attackMouseButton = 0;

    [Header("Đòn đánh")]
    [SerializeField] float reach = 1.8f;
    [Range(10f, 180f)][SerializeField] float halfAngle = 60f;
    [SerializeField] float cooldown = 1.2f;

    [Header("Khóa di chuyển sau khi đánh (giây). 0 = không khóa. Chỉnh cho bằng độ dài animation.")]
    [SerializeField] float lockOnHit = 4.5f;     // đánh trúng: vung + lau kiếm
    [SerializeField] float lockOnMiss = 0.8f;    // đánh hụt
    [SerializeField] float recoveryDelay = 0.3f; // từ lúc vung tới lúc chạy animation lau kiếm

    [Header("Animation")]
    [SerializeField] Animator animator;

    static readonly int AttackParam = Animator.StringToHash("attack");
    static readonly int AttackRecoveryParam = Animator.StringToHash("attack_recovery");

    PlayerRoleInfo role;
    NetworkPlayerMovement move;
    HunterCarry carry;
    bool hasAttack, hasRecovery;

    float serverNextAttack;
    float nextLocalAttack;

    /// <summary>Server: Hunter còn đang làm động tác (đánh, bế, thả...) đến thời điểm này thì chặn mọi yêu cầu mới.</summary>
    float serverBusyUntil;

    void Awake()
    {
        role = GetComponent<PlayerRoleInfo>();
        move = GetComponent<NetworkPlayerMovement>();
        carry = GetComponent<HunterCarry>();
        if (animator == null) animator = GetComponentInChildren<Animator>();

        if (animator != null)
        {
            foreach (AnimatorControllerParameter p in animator.parameters)
            {
                if (p.nameHash == AttackParam) hasAttack = true;
                if (p.nameHash == AttackRecoveryParam) hasRecovery = true;
            }
        }
    }

    /// <summary>Server: đây có phải Hunter hợp lệ, trận chưa kết thúc và đang rảnh tay không.</summary>
    public bool ServerCanAct =>
        IsServer && role.CurrentRole == Role.Hunter && !MatchManager.Ended && Time.time >= serverBusyUntil;

    /// <summary>Server: đánh dấu Hunter bận seconds giây (có trừ hao độ trễ).</summary>
    public void ServerBeginAction(float seconds) => serverBusyUntil = Time.time + seconds * ServerLockSlack;

    void Update()
    {
        if (!IsSpawned || !IsOwner || role.CurrentRole != Role.Hunter) return;
        if (move.IsLocked) return;
        if (carry != null && carry.IsCarrying) return;

        if (Input.GetMouseButtonDown(attackMouseButton) && Time.time >= nextLocalAttack)
        {
            nextLocalAttack = Time.time + cooldown;
            AttackServerRpc();
        }
    }

    [ServerRpc]
    void AttackServerRpc()
    {
        if (!ServerCanAct || (carry != null && carry.IsCarrying)) return;
        if (Time.time < serverNextAttack) return;
        serverNextAttack = Time.time + cooldown * ServerLockSlack;

        SurvivorState target = FindHitTarget();
        bool isHit = target != null && target.ServerHit();
        ServerBeginAction(isHit ? lockOnHit : lockOnMiss);

        PlayAttackClientRpc(isHit);
    }

    SurvivorState FindHitTarget()
    {
        SurvivorState best = null;
        float bestDist = float.MaxValue;

        foreach (SurvivorState st in SurvivorState.All)
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
        if (animator != null && hasAttack) animator.SetTrigger(AttackParam);
        if (IsOwner) move.LockFor(isHit ? lockOnHit : lockOnMiss);
        if (isHit) StartCoroutine(PlayRecoveryAfterDelay());
    }

    System.Collections.IEnumerator PlayRecoveryAfterDelay()
    {
        yield return new WaitForSeconds(recoveryDelay);
        if (animator != null && hasRecovery) animator.SetTrigger(AttackRecoveryParam);
    }
}
