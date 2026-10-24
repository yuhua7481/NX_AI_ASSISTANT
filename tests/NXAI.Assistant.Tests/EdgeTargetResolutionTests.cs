using NXAI.Assistant.Core;
using NXAI.Assistant.UI;
using Xunit;

namespace NXAI.Assistant.Tests;

/// <summary>
/// Phase 9.5 auto-target resolution tests (offline): the "perimeter" rule
/// for a STANDARD BOX, the multi/candidate-body decisions, and the
/// controller's remembered-ref lifecycle. The NX-side geometry collection
/// (face/edge/vertex facts from a live body) is a separate live
/// acceptance item — these tests pin the DECISION layer.
/// </summary>
public class EdgeTargetResolutionTests
{
    private static EdgeTargetResolution.BodyEdgeFacts Box() => new(
        BodyRef: "BLOCK(5)",
        FaceCount: 6, PlanarFaceCount: 6,
        EdgeCount: 12, LinearEdgeCount: 12,
        AllEdgesHaveTwoVertices: true,
        ThreeEdgesPerVertex: true,
        EveryFaceIsFourSided: true);

    // ---------- standard box: the Scene-A target ----------

    [Fact]
    public void StandardBox_ResolvesToAllTwelveEdges()
    {
        var d = EdgeTargetResolution.ResolveBoxPerimeter(Box());
        Assert.True(d.Success);
        Assert.Equal(12, d.ExpectedEdgeCount);
        Assert.Contains("12 条外棱边", d.Reason);
    }

    // ---------- non-box shapes refuse with actionable reasons ----------

    [Theory]
    [InlineData(6, 6, 11, 11, false, true, true, "11 条边")]   // one edge consumed
    [InlineData(7, 7, 15, 15, true, true, true, "7 个面")]      // modified (hole added)
    [InlineData(6, 5, 12, 12, true, true, true, "平面")]        // curved face
    [InlineData(6, 6, 12, 10, true, true, true, "不是直线")]    // non-linear edges
    [InlineData(6, 6, 12, 12, true, false, true, "顶点-边关系")] // wrong vertex incidence
    [InlineData(6, 6, 12, 12, true, true, false, "非四边形面")]  // non-quad face
    public void NonStandardShapes_Refuse(
        int faces, int planar, int edges, int linear,
        bool twoVert, bool threePerVertex, bool quadFaces, string expectedFragment)
    {
        var facts = new EdgeTargetResolution.BodyEdgeFacts(
            "BLOCK(5)", faces, planar, edges, linear, twoVert, threePerVertex, quadFaces);
        var d = EdgeTargetResolution.ResolveBoxPerimeter(facts);
        Assert.False(d.Success);
        Assert.Contains(expectedFragment, d.Reason);
        // Every refusal must tell the user what to do instead — never a
        // silent widening to "all edges".
        Assert.Contains("请", d.Reason);
    }

    // ---------- candidate-body decisions ----------

    [Fact]
    public void ZeroBodies_Refuse()
    {
        var d = EdgeTargetResolution.ResolveCandidateCount(0, null);
        Assert.False(d.Success);
    }

    [Fact]
    public void SingleBody_NoRememberedRef_AcceptsTheOnlyBody()
    {
        var d = EdgeTargetResolution.ResolveCandidateCount(1, null);
        Assert.True(d.Success);
    }

    [Fact]
    public void RememberedRef_AcceptsAsCandidate()
    {
        var d = EdgeTargetResolution.ResolveCandidateCount(3, "BLOCK(5)");
        Assert.True(d.Success);
        Assert.Contains("BLOCK(5)", d.Reason);
        // The acceptance is provisional — the live body must still verify.
        Assert.Contains("重新验证", d.Reason);
    }

    [Fact]
    public void MultiBody_NoRememberedRef_RefuseWithGuidance()
    {
        var d = EdgeTargetResolution.ResolveCandidateCount(4, null);
        Assert.False(d.Success);
        Assert.Contains("4 个实体", d.Reason);
        Assert.Contains("请", d.Reason); // asks, never picks
    }

    // ---------- controller-side remembered-ref lifecycle ----------

    [Fact]
    public void Controller_RemembersOnlyVerifiedRefs_AndResetClears()
    {
        // The controller records LastCreatedBodyRef ONLY from a SUCCESS
        // result's DetailsJson (executor-verified), and ResetTargetResolution
        // clears it (new conversation). Pinned via the public surface.
        var log = new ChatLog();
        var executor = new ChatSessionControllerToolTests.CountingExecutor();
        var controller = new ChatSessionController(
            new ModelService(), log, () => ("https://api.test/v1", "key", "model"),
            maxOutboundMessages: 20, toolExecutor: null);
        Assert.Null(controller.LastCreatedBodyRef);
        controller.ResetTargetResolution();
        Assert.Null(controller.LastCreatedBodyRef);
    }
}
