namespace NXAI.Assistant.Core;

/// <summary>Which body is the boolean result, from the accounting facts.</summary>
public enum BooleanResultBody
{
    /// <summary>The single body that did not exist before the attempt.</summary>
    NewBody,
    /// <summary>The target survived an in-place update (identity preserved).</summary>
    TargetIdentity,
    /// <summary>The tool identity survived (atypical; validated by containment).</summary>
    ToolIdentity,
}

/// <summary>The verification verdict for one committed boolean attempt.</summary>
public enum BooleanVerificationOutcome
{
    /// <summary>Accounting + containment satisfied.</summary>
    Success,
    /// <summary>NX completed the operation with no geometric effect
    /// (count unchanged, both bodies intact, target box unchanged) —
    /// disjoint subtract/intersect. Never reported as success.</summary>
    NoEffect,
    /// <summary>The result contradicts the requested operation.</summary>
    Failure,
}

/// <summary>
/// Facts the NX host observes about one committed boolean attempt (all
/// read-only; the policy decides). Identity aliveness means: the body object
/// with that pre-attempt JournalIdentifier is still alive in workPart.Bodies.
/// NewBodyCount: bodies after the attempt whose identity is not in the
/// pre-attempt set. Boxes are UF AskBoundingBox min/max arrays (6 doubles) or
/// null when unreadable.
/// </summary>
public sealed record BooleanResultFacts(
    BooleanOperationType Operation,
    int BodiesBefore,
    int BodiesAfter,
    bool TargetIdentityAlive,
    bool ToolIdentityAlive,
    int NewBodyCount,
    double[]? TargetBoxBefore,
    double[]? TargetBoxAfter,
    double[]? ToolBoxBefore,
    double[]? ResultBox);

public sealed record BooleanAccountingDecision(
    BooleanVerificationOutcome Outcome,
    string Reason,
    BooleanResultBody? ResultBodySource);

public static class BooleanResultVerificationPolicy
{
    public const double BoxTolerance = 0.01;

    /// <summary>
    /// Stage 1 — accounting. NX boolean semantics (live-verified 2026-10-20):
    /// a committed boolean with real geometric effect consumes exactly one
    /// body (n → n−1), either by destroying one identity or by emitting one
    /// new body. A committed boolean with NO geometric effect (disjoint
    /// subtract/intersect) leaves the count and both identities intact —
    /// that is classified honestly as NoEffect, not as success and not as a
    /// generic count mismatch. Unite never has a no-effect outcome (even
    /// disjoint solids merge into one multi-lump body), so a unite with an
    /// unchanged count is a verification failure.
    /// </summary>
    public static BooleanAccountingDecision ClassifyAccounting(BooleanResultFacts facts)
    {
        if (facts.BodiesAfter == facts.BodiesBefore - 1)
        {
            if (facts.NewBodyCount == 1)
                return new(BooleanVerificationOutcome.Success, "", BooleanResultBody.NewBody);
            if (facts.NewBodyCount == 0)
            {
                if (facts.TargetIdentityAlive && !facts.ToolIdentityAlive)
                    return new(BooleanVerificationOutcome.Success, "", BooleanResultBody.TargetIdentity);
                if (!facts.TargetIdentityAlive && facts.ToolIdentityAlive)
                    return new(BooleanVerificationOutcome.Success, "", BooleanResultBody.ToolIdentity);
                return new(BooleanVerificationOutcome.Failure,
                    $"实体数量减少 1，但目标体与工具体的存在状态（{Alive(facts.TargetIdentityAlive)}/{Alive(facts.ToolIdentityAlive)}）无法确定结果实体。", null);
            }
            return new(BooleanVerificationOutcome.Failure,
                $"实体数量减少 1，但发现 {facts.NewBodyCount} 个新实体，结果归属不明。", null);
        }

        if (facts.BodiesAfter == facts.BodiesBefore)
        {
            bool noEffectPlausible = facts.TargetIdentityAlive && facts.ToolIdentityAlive
                && BoxesMatch(facts.TargetBoxBefore, facts.TargetBoxAfter);
            if (facts.Operation == BooleanOperationType.Unite)
            {
                return new(BooleanVerificationOutcome.Failure,
                    noEffectPlausible
                        ? "并集后实体数量未减少且目标体未变化——并集不可能无几何效果，结果不可信。"
                        : "并集后实体数量未减少，且部件状态与并集预期不符。",
                    null);
            }
            if (noEffectPlausible)
            {
                string op = facts.Operation == BooleanOperationType.Subtract ? "差集" : "交集";
                return new(BooleanVerificationOutcome.NoEffect,
                    $"{op}已提交但未观察到可验证的几何效果：实体数量未变化、两个输入体均存活、" +
                    "目标体包围盒未变化（这些事实不能单独证明工具体与目标体互不相交，" +
                    "只能说明本次提交没有产生可确认的模型改变）。", null);
            }
            return new(BooleanVerificationOutcome.Failure,
                "实体数量未减少，且部件状态既不符合布尔生效也不符合无效果的特征。", null);
        }

        return new(BooleanVerificationOutcome.Failure,
            $"实体数量为 {facts.BodiesAfter}，预期 {facts.BodiesBefore - 1}（布尔生效）或 {facts.BodiesBefore}（无几何效果）。", null);
    }

    /// <summary>
    /// Stage 2 — geometric containment (necessary conditions, unchanged from
    /// the previous contract): unite result ⊇ target; subtract result ⊆
    /// target; intersect result ⊆ both. Unreadable boxes fail — never pass.
    /// </summary>
    public static (bool Ok, string Reason) VerifyContainment(BooleanResultFacts facts)
    {
        if (facts.ResultBox is not { } result || facts.TargetBoxBefore is not { } target || facts.ToolBoxBefore is not { } tool)
            return (false, "无法读取布尔运算前后的包围盒");
        if (result.Length != 6 || target.Length != 6 || tool.Length != 6)
            return (false, "无法读取布尔运算前后的包围盒");

        bool Contains(double[] outer, double[] inner) =>
            inner[0] >= outer[0] - BoxTolerance && inner[1] >= outer[1] - BoxTolerance && inner[2] >= outer[2] - BoxTolerance &&
            inner[3] <= outer[3] + BoxTolerance && inner[4] <= outer[4] + BoxTolerance && inner[5] <= outer[5] + BoxTolerance;

        return facts.Operation switch
        {
            // Unite must contain BOTH inputs' boxes (necessary condition — a
            // partial-overlap unite can only grow or keep each input's extent).
            BooleanOperationType.Unite => Contains(result, target) && Contains(result, tool)
                ? (true, "")
                : (false, "并集结果包围盒未同时包含目标体与工具体包围盒"),
            // Necessary conditions only: box containment cannot prove full
            // geometric correctness; volume-level equality stays a live
            // acceptance item.
            BooleanOperationType.Subtract => Contains(target, result)
                ? (true, "")
                : (false, "差集结果包围盒未保持在目标体范围内"),
            _ => Contains(target, result) && Contains(tool, result)
                ? (true, "")
                : (false, "交集结果包围盒未同时包含于目标体与工具体范围内"),
        };
    }

    private static bool BoxesMatch(double[]? before, double[]? after)
    {
        if (before is null || after is null || before.Length != 6 || after.Length != 6)
            return false;
        for (int i = 0; i < 6; i++)
        {
            if (!double.IsFinite(before[i]) || !double.IsFinite(after[i]))
                return false;
            if (Math.Abs(before[i] - after[i]) > BoxTolerance)
                return false;
        }
        return true;
    }

    private static string Alive(bool alive) => alive ? "存活" : "已消耗";
}
