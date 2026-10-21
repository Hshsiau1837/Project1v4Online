using Unity.Netcode;
using Unity.Netcode.Components;
using UnityEngine;

/// <summary>
/// Di chuyển người chơi qua mạng (Owner-authoritative).
/// </summary>
[RequireComponent(typeof(CharacterController))]
public class NetworkPlayerMovement : NetworkBehaviour
{
    public enum MoveState : byte { Idle = 0, Running = 1, CrouchIdle = 2, CrouchWalk = 3, Vaulting = 4 }

    public static event System.Action<MoveState> OnLocalStateChanged;

    [Header("Tốc độ")]
    [SerializeField] float runSpeed = 4f;
    [SerializeField] float crouchSpeed = 2.5f;
    [SerializeField] float turnSpeed = 720f;
    [SerializeField] float gravity = -20f;

    [Header("Phím Ngồi (Bấm C để Bật/Tắt)")]
    [SerializeField] KeyCode crouchKey = KeyCode.C;

    [Header("Crouch")]
    [Range(0.4f, 0.9f)]
    [SerializeField] float crouchHeightRatio = 0.6f;

    [Header("Animation")]
    [SerializeField] Animator animator;

    [Header("Vị trí spawn")]
    [SerializeField] Vector3 spawnOrigin = new Vector3(0f, 0.1f, 0f);
    [SerializeField] float spawnSpacing = 2f;

    readonly NetworkVariable<byte> netState = new NetworkVariable<byte>(
        0, NetworkVariableBase.DefaultReadPerm, NetworkVariableWritePermission.Owner);

    static readonly int MoveStateParam = Animator.StringToHash("moveState");

    CharacterController cc;
    Transform cam;
    float standHeight;
    Vector3 standCenter;
    float verticalVelocity;
    bool crouching;
    bool capsuleCrouched;
    int lastShownState = -1;

    bool locked;
    float speedMultiplier = 1f;

    public bool IsVaulting { get; private set; }

    public void SetLocked(bool on)
    {
        if (locked == on) return;
        locked = on;
        if (on) StopMoving();
    }

    public void SetSpeedMultiplier(float m) { speedMultiplier = Mathf.Max(0f, m); }

    public void SetVaulting(bool on)
    {
        if (!IsOwner || IsVaulting == on) return;
        IsVaulting = on;
        if (on)
        {
            StopMoving();
            if (IsSpawned) netState.Value = (byte)MoveState.Vaulting;
        }
        else
        {
            verticalVelocity = 0f;
            if (IsSpawned) netState.Value = (byte)MoveState.Idle;
        }
    }

    public void SnapTo(Vector3 pos, float yaw)
    {
        bool wasEnabled = cc.enabled;
        cc.enabled = false;
        Quaternion rot = Quaternion.Euler(0f, yaw, 0f);
        transform.SetPositionAndRotation(pos, rot);
        NetworkTransform nt = GetComponent<NetworkTransform>();
        if (nt != null) nt.Teleport(pos, rot, transform.localScale);
        cc.enabled = wasEnabled;
        verticalVelocity = 0f;
    }

    void StopMoving()
    {
        crouching = false;
        if (capsuleCrouched) SetCapsule(false);
        if (IsOwner && IsSpawned && netState.Value != (byte)MoveState.Idle) netState.Value = (byte)MoveState.Idle;
    }

    void Awake()
    {
        cc = GetComponent<CharacterController>();
        if (animator == null) animator = GetComponentInChildren<Animator>();
        standHeight = cc.height;
        standCenter = cc.center;
    }

    public override void OnNetworkSpawn()
    {
        cc.enabled = false;
        if (!IsOwner) return;

        int slot = (int)(OwnerClientId % 5);
        Vector3 pos = spawnOrigin + Vector3.right * (spawnSpacing * slot);
        transform.position = pos;
        cc.enabled = true;

        if (Camera.main != null) cam = Camera.main.transform;

        var follow = FindFirstObjectByType<ThirdPersonCamera>();
        if (follow != null) follow.target = transform;
    }

    void Update()
    {
        if (!IsSpawned) return;
        if (IsOwner) OwnerUpdate();
        ShowAnimation();
    }

    void OwnerUpdate()
    {
        if (cam == null && Camera.main != null) cam = Camera.main.transform;

        if (locked || IsVaulting) return;

        // --- Crouch (Bấm C 1 lần là ngồi, bấm lại 1 lần là đứng) ---
        if (Input.GetKeyDown(crouchKey))
        {
            crouching = !crouching;
        }
        if (crouching != capsuleCrouched) SetCapsule(crouching);

        // --- Hướng di chuyển ---
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
        float speed = (crouching ? crouchSpeed : runSpeed) * speedMultiplier;
        if (cc.isGrounded && verticalVelocity < 0f) verticalVelocity = -2f;
        verticalVelocity += gravity * Time.deltaTime;

        Vector3 velocity = (moving ? dir * speed : Vector3.zero) + Vector3.up * verticalVelocity;
        cc.Move(velocity * Time.deltaTime);

        // --- Đồng bộ trạng thái Animation ---
        MoveState state = crouching
            ? (moving ? MoveState.CrouchWalk : MoveState.CrouchIdle)
            : (moving ? MoveState.Running : MoveState.Idle);

        if (netState.Value != (byte)state) netState.Value = (byte)state;
    }

    void SetCapsule(bool crouch)
    {
        capsuleCrouched = crouch;
        float h = crouch ? standHeight * crouchHeightRatio : standHeight;
        float bottom = standCenter.y - standHeight * 0.5f;
        cc.height = h;
        // Đã sửa lỗi h0.5f thành h * 0.5f
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