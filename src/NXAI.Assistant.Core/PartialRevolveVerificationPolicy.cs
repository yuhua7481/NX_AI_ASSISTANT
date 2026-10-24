namespace NXAI.Assistant.Core;

/// <summary>
/// The NX-observable facts of a committed partial-angle revolve, collected
/// read-only by the host. The policy below decides success/failure —
/// offline-testable, no NXOpen dependency.
/// </summary>
public sealed record CommittedRevolveFacts(
    int BodiesBefore,
    int BodiesAfter,
    int NewBodyCount,
    bool NewBodyAlive,
    bool OwnershipReadable,
    bool OwnedByCommittedFeature,
    int FeatureOwnedBodyCount,
    bool SheetReadable,
    bool NewBodyIsSheet,
    bool BoxReadable,
    bool BoxFinite);

/// <summary>
/// Partial-angle revolve postcondition policy (FACT-BASED — no analytic
/// prediction). Root cause history: the sketch-curve-derived analytic
/// envelope proved unreliable in both sketch read modes against live NX2512
/// (60°: deactivated read failed outright; 270°: activated read returned a
/// bound geometrically impossible as a sweep bound — Z span 12.5mm vs the
/// real body's 29.6mm), so no envelope check runs for partial angles.
/// The verified facts, each independently checkable in NX:
///   1. exactly one new body (identity diff vs the pre-attempt set);
///   2. that body is alive;
///   3. it belongs to the feature this commit returned
///      (Feature.GetBodies — verified NXOpen API);
///   4. it is a solid, not a sheet (Body.IsSheetBody — verified);
///   5. its bounding box reads back finite (AskBoundingBox — verified).
/// Failure of any fact fails the attempt (the caller rolls back per the
/// UndoMark mechanism) — nothing here fakes success, and an unreadable
/// ownership relation fails SAFE (the body count alone never proves
/// success).
/// </summary>
public static class PartialRevolveVerificationPolicy
{
    public static (bool Success, string? Error) Verify(CommittedRevolveFacts f)
    {
        if (f.BodiesAfter != f.BodiesBefore + 1)
            return (false, $"实体数量为 {f.BodiesAfter}，预期 {f.BodiesBefore + 1}");
        if (f.NewBodyCount != 1)
            return (false, "发现多个新实体，无法确定本次创建结果");
        if (!f.NewBodyAlive)
            return (false, "新实体对象已失效");
        if (!f.OwnershipReadable)
            return (false, "无法读取本次旋转特征的关联实体");
        if (!f.OwnedByCommittedFeature)
            return (false, f.FeatureOwnedBodyCount > 0
                ? "新实体不属于本次提交的旋转特征（无法确认归属）"
                : "新实体不属于本次提交的旋转特征（该特征没有关联实体）");
        if (!f.SheetReadable)
            return (false, "无法读取新实体类型");
        if (f.NewBodyIsSheet)
            return (false, "本次旋转产生的是片体（sheet body）而非实体，操作被拒绝");
        if (!f.BoxReadable)
            return (false, "无法读取新实体包围盒");
        if (!f.BoxFinite)
            return (false, "新实体包围盒包含非有限值（无效几何）");
        return (true, null);
    }
}
