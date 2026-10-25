/// <summary>
/// Máy mã hóa. Gắn NetworkObject + script này vào CipherMachine_1..7 trong scene Match.
/// Đủ 5 máy thì còi vang và các máy còn lại bị khóa (đúng luật Identity V).
/// </summary>
public class CipherMachine : NetInteractable
{
    public override string PromptText => "Giải mã máy";
    public override bool TerrorShockOnUse => true;

    public override bool IsAvailable =>
        !Done.Value && !(MatchManager.Instance != null && MatchManager.Instance.GatesPowered.Value);

    protected override void OnCompleted()
    {
        if (MatchManager.Instance != null) MatchManager.Instance.OnMachineCompleted();
    }
}