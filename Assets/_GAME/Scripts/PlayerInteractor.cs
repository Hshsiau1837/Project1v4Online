using Unity.Netcode;
using UnityEngine;

/// <summary>
/// Gắn vào prefab Survivor (và Hunter, script tự bỏ qua nếu không phải Survivor).
/// Chỉ chạy trên máy của chủ nhân nhân vật: nhấn F gần máy/cổng/hầm để bắt đầu, thanh tiến độ tự chạy;
/// di chuyển hoặc nhấn F lần nữa để buông.
/// </summary>
[RequireComponent(typeof(PlayerRoleInfo))]
public class PlayerInteractor : NetworkBehaviour
{
    [SerializeField] KeyCode interactKey = KeyCode.F;

    PlayerRoleInfo role;
    SurvivorState state;
    NetInteractable nearest, current;
    float nextScan;

    void Awake()
    {
        role = GetComponent<PlayerRoleInfo>();
        state = GetComponent<SurvivorState>();   // có thể null nếu chưa gắn
    }

    public override void OnNetworkSpawn()
    {
        enabled = IsOwner;   // người khác không chạy Update/OnGUI của script này
    }

    void Update()
    {
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
            if (moving || tooFar || current.Done.Value || !current.IsAvailable || Input.GetKeyDown(interactKey))
            {
                current.StopUseServerRpc();
                current = null;
            }
            return;
        }

        if (nearest != null && !moving && Input.GetKeyDown(interactKey))
        {
            current = nearest;
            current.StartUseServerRpc();
        }
    }

    NetInteractable FindNearest()
    {
        NetInteractable best = null;
        float bestDist = float.MaxValue;
        foreach (NetInteractable it in FindObjectsByType<NetInteractable>(FindObjectsSortMode.None))
        {
            if (!it.IsSpawned || !it.IsAvailable) continue;
            float d = it.FlatDistance(transform.position);
            if (d <= it.Range && d < bestDist) { bestDist = d; best = it; }
        }
        return best;
    }

    void OnGUI()
    {
        NetInteractable target = current != null ? current : nearest;
        if (target == null) return;

        float progress = Mathf.Clamp01(target.Progress.Value);
        int pct = Mathf.RoundToInt(progress * 100f);
        string text = current != null
            ? $"Đang làm... {pct}%   (di chuyển hoặc nhấn F để dừng)"
            : $"[F] {target.PromptText}  ({pct}%)";

        const float w = 320f, h = 18f;
        float x = Screen.width / 2f - w / 2f;
        float y = Screen.height - 90f;

        GUI.Label(new Rect(x, y - 28f, w + 80f, 24f), text);

        Color old = GUI.color;
        GUI.color = new Color(0f, 0f, 0f, 0.6f);                       // nền thanh
        GUI.DrawTexture(new Rect(x, y, w, h), Texture2D.whiteTexture);
        GUI.color = current != null ? new Color(0.3f, 0.9f, 0.4f) : new Color(0.8f, 0.8f, 0.8f);
        GUI.DrawTexture(new Rect(x + 2f, y + 2f, (w - 4f) * progress, h - 4f), Texture2D.whiteTexture);
        GUI.color = old;
    }
}