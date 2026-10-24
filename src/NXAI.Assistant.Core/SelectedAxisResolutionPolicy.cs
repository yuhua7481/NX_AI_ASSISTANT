namespace NXAI.Assistant.Core;

/// <summary>
/// Facts the NX host can observe about ONE selected feature node (e.g.
/// Features.AssociativeLine) without making any decision itself. All
/// collection is read-only; the policy below decides.
/// </summary>
public sealed record SelectedFeatureFacts(
    string FeatureType,
    string FeatureId,
    bool Alive,
    bool Suppressed,
    bool EntitiesReadable,
    int LiveLineCount,
    int DeadLineCount,
    int OtherEntityCount);

/// <summary>
/// The decision for resolving one selected feature to a rotation-axis line.
/// <para>LineIndex: index of the usable line among the feature's live Line
/// entities when Success; null otherwise. The host maps this index back to
/// its own Line object list — the policy never touches NX objects.</para>
/// </summary>
public sealed record SelectedAxisFeatureDecision(
    bool Success,
    int? LineIndex,
    string? Error);

/// <summary>
/// selected_axis feature-resolution policy: the user picked a feature node
/// (the live case: Features.AssociativeLine selected from the Part
/// Navigator) as their rotation axis. The feature carries no axis geometry
/// itself; it must unambiguously own EXACTLY ONE live Line for the pick to
/// be usable. Strictness is the safety property here — each failure is a
/// named diagnosis, and there is never a first-object pick or a fallback to
/// world axes. The resulting line still goes through the same revolve
/// plane/geometry precondition checks as every other axis kind.
/// </summary>
public static class SelectedAxisResolutionPolicy
{
    /// <summary>
    /// Decides whether the feature can stand in for its line. Success means:
    /// alive, not suppressed, entities readable, exactly one live Line and
    /// no dead Line among them (a dead line alongside a live one would make
    /// "which one did the user mean" ambiguous).
    /// </summary>
    public static SelectedAxisFeatureDecision Resolve(SelectedFeatureFacts f)
    {
        if (!f.Alive)
        {
            return new SelectedAxisFeatureDecision(false, null,
                $"选中的特征 {f.FeatureType}（{f.FeatureId}）对象已失效，无法解析旋转轴。");
        }
        if (f.Suppressed)
        {
            return new SelectedAxisFeatureDecision(false, null,
                $"选中的特征 {f.FeatureType}（{f.FeatureId}）已被抑制，其几何当前不可用，" +
                "无法作为旋转轴。请先取消抑制或在图形区直接选中直线。");
        }
        if (!f.EntitiesReadable)
        {
            return new SelectedAxisFeatureDecision(false, null,
                $"读取特征 {f.FeatureType}（{f.FeatureId}）的关联几何失败，无法作为旋转轴。");
        }
        if (f.LiveLineCount == 1 && f.DeadLineCount == 0)
        {
            return new SelectedAxisFeatureDecision(true, 0, null);
        }
        if (f.LiveLineCount == 0)
        {
            return new SelectedAxisFeatureDecision(false, null,
                $"选中的特征 {f.FeatureType}（{f.FeatureId}）没有可用的关联直线几何" +
                $"（非直线对象 {f.OtherEntityCount} 个，已失效 {f.DeadLineCount} 个），无法作为旋转轴。" +
                "请在图形区直接选中一条直线、直边或基准轴。");
        }
        return new SelectedAxisFeatureDecision(false, null,
            $"选中的特征 {f.FeatureType}（{f.FeatureId}）关联了 {f.LiveLineCount} 条可用直线" +
            (f.DeadLineCount > 0 ? $"（另有已失效 {f.DeadLineCount} 条）" : "") +
            "，无法确定哪一条是你要的旋转轴。请在图形区直接选中目标直线。");
    }

    /// <summary>
    /// The selection-count diagnosis when the total usable axis objects is
    /// not exactly one. Names every category so the user sees what was
    /// actually selected (the live failure: 1 object, but 0 of any usable
    /// kind — the object was a feature node).
    /// </summary>
    public static string DescribeSelectionMismatch(
        int datumAxisCount, int edgeCount, int directLineCount,
        int resolvedFeatureLineCount, int totalSelectedObjects,
        IReadOnlyList<string> featureDiagnoses)
    {
        string featureNote = featureDiagnoses.Count > 0
            ? "；" + string.Join("；", featureDiagnoses)
            : "";
        return $"selected_axis 需要恰好选中一个基准轴、直边或草图直线" +
            $"（当前：{datumAxisCount} 个基准轴、{edgeCount} 条边、{directLineCount} 条直线、" +
            $"{resolvedFeatureLineCount} 个可解析的关联直线特征，共 {totalSelectedObjects} 个对象）{featureNote}。";
    }
}
