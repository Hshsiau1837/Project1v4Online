using Unity.Netcode;
using UnityEngine;

/// <summary>
/// Di chuyển người chơi qua mạng (Owner-authoritative), tuần 1.
///   Idle        : không bấm gì (mặc định)
///   Running     : có hướng di chuyển (WASD) -> chạy ngay, không có Walking
///   CrouchIdle  : giữ phím Crouch, đứng yên
///   CrouchWalk  : giữ phím Crouch + WASD
/// Owner tính trạng thái rồi ghi vào 1 NetworkVariable; mọi máy đọc biến này để chạy animation.
/// Vị trí/hướng do NetworkTransform (Authority Mode = Owner) lo, không xử lý ở đây.
/// </summary>
[RequireComponent(typeof(CharacterController))]
public class NetworkPlayerMovement : NetworkBehaviour
{
    public enum MoveState : byte { Idle = 0, Running = 1, CrouchIdle = 2, CrouchWalk = 3 }

    /// <summary>Chỉ bắn trên máy của chủ nhân nhân vật (dùng cho HUD).</summary>
    public static event System.Action<MoveState> OnLocalStateChanged;

    [Header("Tốc độ")]
    [SerializeField] float runSpeed = 6f;
    [SerializeField] float crouchSpeed = 2.5f;
    [SerializeField] float turnSpeed = 720f;      // độ/giây
    [SerializeField] float gravity = -20f;

    [Header("Phím")]
    [SerializeField] KeyCode crouchKey = KeyCode.C;   // lộ trình: giữ C để khom
    [SerializeField] bool crouchIsToggle = false; // false = giữ để ngồi, thả thì đứng

    [Header("Crouch")]
    [Range(0.4f, 0.9f)]
    [SerializeField] float crouchHeightRatio = 0.6f; // chiều cao khi ngồi / chiều cao khi đứng

    [Header("Animation")]
    [SerializeField] Animator animator;           // Animator của model con (MainChar.controller)

    [Header("Vị trí spawn (để các nhân vật không chồng lên nhau)")]
    [SerializeField] Vector3 spawnOrigin = new Vector3(0f, 0.1f, 0f);
    [SerializeField] float spawnSpacing = 2f;

    // Owner ghi, mọi máy đọc.
    readonly NetworkVariable<byte> netState = new NetworkVariable<byte>(
        0, NetworkVariableBase.DefaultReadPerm, NetworkVariableWritePermission.Owner);

    // Tham số int "moveState" trong MainChar.controller, có 4 transition từ Any State:
    // 0 idle, 1 running, 2 crouchIdle, 3 crouchWalk.
    static readonly int MoveStateParam = Animator.StringToHash("moveState");

    CharacterController cc;
    Transform cam;
    float standHeight;
    Vector3 standCenter;
    float verticalVelocity;
    bool crouching;
    bool capsuleCrouched;
    int lastShownState = -1;

    void Awake()
    {
        cc = GetComponent<CharacterController>();
        if (animator == null) animator = GetComponentInChildren<Animator>();
        standHeight = cc.height;   // đọc từ prefab: chỉnh capsule lúc đứng là đủ
        standCenter = cc.center;
    }

    public override void OnNetworkSpawn()
    {
        // Máy không phải owner: tắt CharacterController để không xung đột với NetworkTransform.
        cc.enabled = false;
        if (!IsOwner) return;

        int slot = (int)(OwnerClientId % 5);
        Vector3 pos = spawnOrigin + Vector3.right * (spawnSpacing * slot);
        transform.position = pos;   // CharacterController đang tắt nên đặt thẳng vị trí được
        cc.enabled = true;

        if (Camera.main != null) cam = Camera.main.transform;

        // Camera chỉ bám nhân vật của chính mình.
        var follow = FindFirstObjectByType<ThirdPersonCamera>();
        if (follow != null) follow.target = transform;   // đổi "target" nếu biến trong ThirdPersonCamera tên khác
    }

    void Update()
    {
        if (!IsSpawned) return;
        if (IsOwner) OwnerUpdate();
        ShowAnimation();   // chạy trên mọi máy, kể cả owner
    }

    void OwnerUpdate()
    {
        if (cam == null && Camera.main != null) cam = Camera.main.transform;

        // --- Crouch ---
        if (crouchIsToggle)
        {
            if (Input.GetKeyDown(crouchKey)) crouching = !crouching;
        }
        else
        {
            crouching = Input.GetKey(crouchKey);
        }
        if (crouching != capsuleCrouched) SetCapsule(crouching);

        // --- Hướng di chuyển theo camera, không gia tốc: bấm là chạy ngay ---
        float h = Input.GetAxisRaw("Horizontal");
        float v = Input.GetAxisRaw("Vertical");

        Vector3 fwd = cam != null ? cam.forward : transform.forward;
        Vector3 right = cam != null ? cam.right : transform.right;
        fwd.y = 0f; right.y = 0f;
        fwd.Normalize(); right.Normalize();

        Vector3 dir = fwd * v + right * h;
        bool moving = dir.sqrMagnitude > 0.0001f;
        if (moving)
        {
            dir.Normalize();
            Quaternion target = Quaternion.LookRotation(dir, Vector3.up);
            transform.rotation = Quaternion.RotateTowards(transform.rotation, target, turnSpeed * Time.deltaTime);
        }

        // --- Di chuyển ---
        float speed = crouching ? crouchSpeed : runSpeed;
        if (cc.isGrounded && verticalVelocity < 0f) verticalVelocity = -2f;
        verticalVelocity += gravity * Time.deltaTime;

        Vector3 velocity = (moving ? dir * speed : Vector3.zero) + Vector3.up * verticalVelocity;
        cc.Move(velocity * Time.deltaTime);

        // --- Trạng thái -> NetworkVariable (chỉ ghi khi đổi) ---
        MoveState state = crouching
            ? (moving ? MoveState.CrouchWalk : MoveState.CrouchIdle)
            : (moving ? MoveState.Running : MoveState.Idle);
        if (netState.Value != (byte)state) netState.Value = (byte)state;
    }

    // Thu thấp capsule nhưng giữ nguyên đáy (chân không bị nhấc lên).
    void SetCapsule(bool crouch)
    {
        capsuleCrouched = crouch;
        float h = crouch ? standHeight * crouchHeightRatio : standHeight;
        float bottom = standCenter.y - standHeight * 0.5f;
        cc.height = h;
        cc.center = new Vector3(standCenter.x, bottom + h * 0.5f, standCenter.z);
    }

    void ShowAnimation()
    {
        int s = netState.Value;
        if (s == lastShownState) return;
        lastShownState = s;

        if (animator != null) animator.SetInteger(MoveStateParam, s);
        if (IsOwner) OnLocalStateChanged?.Invoke((MoveState)s);
    }
}