using NXAI.Assistant.Core;
using Xunit;

namespace NXAI.Assistant.Tests;

/// <summary>
/// selected_axis feature-resolution policy — the decision core for resolving
/// a selected feature node (live case: Features.AssociativeLine) to its
/// rotation-axis line. The policy is pure and NXOpen-free: the NxHost
/// collector gathers the observable facts, this decides. Every case maps to
/// a named diagnosis; there is no first-pick and no axis fallback anywhere.
/// </summary>
public class SelectedAxisResolutionPolicyTests
{
    private static SelectedFeatureFacts Facts(
        bool alive = true,
        bool suppressed = false,
        bool entitiesReadable = true,
        int liveLines = 1,
        int deadLines = 0,
        int others = 0,
        string type = "ASSOCIATIVE LINE",
        string id = "FEATURE(1)") => new(
        type, id, alive, suppressed, entitiesReadable, liveLines, deadLines, others);

    // ------------------------------------------------------- success paths

    [Fact]
    public void AssociativeLine_WithSingleLiveLine_ResolvesSuccessfully()
    {
        // The live case: one associative-line feature owning exactly one
        // live Line — the user's pick resolves to that line.
        var decision = SelectedAxisResolutionPolicy.Resolve(Facts());
        Assert.True(decision.Success);
        Assert.Equal(0, decision.LineIndex);
        Assert.Null(decision.Error);
    }

    [Fact]
    public void Feature_WithOneLiveLineAndOneArc_ResolvesTheLine()
    {
        // A feature may carry non-line entities alongside its line; the line
        // is still the unique usable axis object.
        var decision = SelectedAxisResolutionPolicy.Resolve(Facts(others: 1));
        Assert.True(decision.Success);
        Assert.Equal(0, decision.LineIndex);
    }

    // ------------------------------------------------------- failure paths

    [Fact]
    public void DeadFeature_FailsWithDiagnosis_NoSuccess()
    {
        var decision = SelectedAxisResolutionPolicy.Resolve(Facts(alive: false));
        Assert.False(decision.Success);
        Assert.Null(decision.LineIndex);
        Assert.Contains("已失效", decision.Error);
    }

    [Fact]
    public void SuppressedFeature_FailsWithActionableDiagnosis()
    {
        var decision = SelectedAxisResolutionPolicy.Resolve(Facts(suppressed: true));
        Assert.False(decision.Success);
        Assert.Contains("抑制", decision.Error);
        // The diagnosis points at a concrete remedy, not a silent fallback.
        Assert.Contains("直接选中直线", decision.Error);
    }

    [Fact]
    public void UnreadableEntities_FailsExplicitly()
    {
        var decision = SelectedAxisResolutionPolicy.Resolve(Facts(entitiesReadable: false));
        Assert.False(decision.Success);
        Assert.Contains("关联几何失败", decision.Error);
    }

    [Fact]
    public void FeatureWithoutLineGeometry_FailsNamingTheContent()
    {
        // A non-linear feature (e.g. a block) — nothing usable inside.
        var decision = SelectedAxisResolutionPolicy.Resolve(Facts(liveLines: 0, others: 3));
        Assert.False(decision.Success);
        Assert.Contains("没有可用的关联直线几何", decision.Error);
        Assert.Contains("非直线对象 3 个", decision.Error);
    }

    [Fact]
    public void FeatureWithMultipleLiveLines_RefusesFirstPick()
    {
        // Two live lines: the axis intent is ambiguous. The policy must NOT
        // pick the first — the user decides which one they meant.
        var decision = SelectedAxisResolutionPolicy.Resolve(Facts(liveLines: 2));
        Assert.False(decision.Success);
        Assert.Null(decision.LineIndex);
        Assert.Contains("2 条可用直线", decision.Error);
        Assert.Contains("无法确定", decision.Error);
    }

    [Fact]
    public void FeatureWithLiveAndDeadLine_RefusesAsAmbiguous()
    {
        // One live + one dead line is NOT the unique-line case: "which one
        // did the user mean" cannot be answered from the feature alone.
        var decision = SelectedAxisResolutionPolicy.Resolve(Facts(liveLines: 1, deadLines: 1));
        Assert.False(decision.Success);
        Assert.Contains("已失效 1 条", decision.Error);
    }

    [Fact]
    public void AllDeadLines_ReportedAsNoUsableGeometry()
    {
        var decision = SelectedAxisResolutionPolicy.Resolve(Facts(liveLines: 0, deadLines: 2));
        Assert.False(decision.Success);
        Assert.Contains("已失效 2 个", decision.Error);
    }

    // --------------------------------------- selection-mismatch diagnosis

    [Fact]
    public void SelectionMismatch_NamesEveryCategory()
    {
        // The exact live shape: 1 selected object, but 0 usable kinds (the
        // object was an unresolvable feature node) — the message must say so.
        string message = SelectedAxisResolutionPolicy.DescribeSelectionMismatch(
            datumAxisCount: 0, edgeCount: 0, directLineCount: 0,
            resolvedFeatureLineCount: 0, totalSelectedObjects: 1,
            featureDiagnoses: new[] { "选中的特征 ASSOCIATIVE LINE（F1）没有可用的关联直线几何" });
        Assert.Contains("0 个基准轴", message);
        Assert.Contains("0 条边", message);
        Assert.Contains("0 条直线", message);
        Assert.Contains("0 个可解析的关联直线特征", message);
        Assert.Contains("共 1 个对象", message);
        Assert.Contains("ASSOCIATIVE LINE", message);
    }

    [Fact]
    public void SelectionMismatch_TwoResolvedLines_ReportsBoth()
    {
        string message = SelectedAxisResolutionPolicy.DescribeSelectionMismatch(
            datumAxisCount: 0, edgeCount: 0, directLineCount: 0,
            resolvedFeatureLineCount: 2, totalSelectedObjects: 2,
            featureDiagnoses: Array.Empty<string>());
        Assert.Contains("2 个可解析的关联直线特征", message);
        Assert.Contains("共 2 个对象", message);
    }
}
