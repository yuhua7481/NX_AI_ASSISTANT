using System.Text.RegularExpressions;

namespace NXAI.Assistant.Core;

public sealed record ModelingClaimVerdict(
    bool IsModelingSuccessClaim,
    bool ShouldBlock,
    string? ReplacementMessage);

/// <summary>
/// Controller-side guard against the model declaring modeling success
/// without a real tool execution (2026-10-21 live failure: a second
/// subtract request produced NO tool call, the model reused the prior
/// SUBTRACT(3) feature in its text, and the UI showed it as success).
///
/// Decisions use ONLY the current turn's structured execution receipts —
/// never conversation history. A turn with zero executions cannot make a
/// modeling-success text trustworthy; a turn whose executions ALL failed
/// cannot have its failure overwritten by success wording. Ordinary
/// Q+A/clarification text without a modeling-success claim is untouched.
/// This is a controller display policy, not a prompt change.
/// </summary>
public static partial class ModelingClaimGuard
{
    // Modeling outcome verbs / success markers combined with modeling nouns.
    // Deliberately conservative: both a verb phrase AND a modeling noun must
    // appear, so ordinary explanations mentioning e.g. "差集" as a concept do
    // not trip the guard.
    [GeneratedRegex(
        "(已完成|已创建|已生成|已执行|已建模|已减去|已合并|已求交|已切除|已成功|操作成功|创建成功|执行成功|建模成功|完成了|已完成|完成布尔|布尔完成|差集完成|并集完成|交集完成|完成差集|完成并集|完成交集|完成拉伸|完成旋转|完成草图|完成创建)")]
    private static partial Regex SuccessVerb();

    [GeneratedRegex(
        "(布尔|并集|差集|交集|草图|拉伸|旋转|长方体|方块|圆柱|圆锥|圆台|球体|球|基准面|特征|实体)")]
    private static partial Regex ModelingNoun();

    public static ModelingClaimVerdict Evaluate(
        string? finalText,
        IReadOnlyList<(string ToolName, string StatusWireName)> executions)
    {
        if (string.IsNullOrWhiteSpace(finalText))
            return new(false, false, null);
        if (!SuccessVerb().IsMatch(finalText) || !ModelingNoun().IsMatch(finalText))
            return new(false, false, null);

        if (executions.Count == 0)
        {
            // Zero real executions this turn: a modeling-success claim has
            // nothing to rest on. Replace with a verifiable controller hint.
            return new(true, true,
                "本轮未执行建模工具，未确认发生几何修改，不能认定操作成功。请重新发送建模指令。");
        }

        bool anySuccess = executions.Any(e => e.StatusWireName == ToolExecutionStatusWire.Success);
        if (!anySuccess)
        {
            // Every real execution failed (or was refused): the model's
            // unverified success wording must NOT be displayed at all —
            // showing it above any correction still reads as success to the
            // user (2026-10-21 residual risk). Replace it with the
            // controller's verdict; the failure details stay available in
            // the structured execution records the caller renders separately.
            return new(true, true,
                "本轮建模操作均失败或被拒绝，未确认几何修改成功。请查看执行结果并根据错误信息重试。");
        }

        // At least one real success this turn: the claim has a basis; the
        // structured execution receipt is appended by the caller as before.
        return new(true, false, null);
    }

    private static class ToolExecutionStatusWire
    {
        public const string Success = "success";
    }
}
