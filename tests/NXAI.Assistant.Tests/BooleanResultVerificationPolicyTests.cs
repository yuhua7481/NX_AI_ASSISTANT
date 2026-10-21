using NXAI.Assistant.Core;
using Xunit;

namespace NXAI.Assistant.Tests;

/// <summary>
/// Phase 8.8 boolean postcondition policy (offline): the live failure
/// matrix from 2026-10-20 (unite 2→1 ok; disjoint subtract committed with
/// count unchanged and was misreported "实体数量为 2，预期 1" then rolled back)
/// is pinned here as policy facts. The no-effect state is honest, distinct
/// from success AND from verification failure; effective outcomes keep the
/// full accounting + containment checks (never weakened).
/// </summary>
public class BooleanResultVerificationPolicyTests
{
    private static double[] Box(double x0, double y0, double z0, double x1, double y1, double z1) =>
        new[] { x0, y0, z0, x1, y1, z1 };

    private static BooleanResultFacts Facts(
        BooleanOperationType op,
        int before, int after,
        bool targetAlive = true, bool toolAlive = true,
        int newBodies = 0,
        double[]? targetBefore = null, double[]? targetAfter = null,
        double[]? toolBefore = null, double[]? result = null) =>
        new(op, before, after, targetAlive, toolAlive, newBodies,
            targetBefore, targetAfter, toolBefore, result);

    // ---------- stage 1: accounting ----------

    [Fact]
    public void EffectiveSubtract_NewResultBody_Succeeds()
    {
        // NX in-place update variant: target identity survives, tool
        // consumed, no new body.
        var d = BooleanResultVerificationPolicy.ClassifyAccounting(Facts(
            BooleanOperationType.Subtract, before: 2, after: 1,
            targetAlive: true, toolAlive: false, newBodies: 0));
        Assert.Equal(BooleanVerificationOutcome.Success, d.Outcome);
        Assert.Equal(BooleanResultBody.TargetIdentity, d.ResultBodySource);
    }

    [Fact]
    public void EffectiveIntersect_NewBodyEmitted_Succeeds()
    {
        var d = BooleanResultVerificationPolicy.ClassifyAccounting(Facts(
            BooleanOperationType.Intersect, before: 2, after: 1,
            targetAlive: false, toolAlive: false, newBodies: 1));
        Assert.Equal(BooleanVerificationOutcome.Success, d.Outcome);
        Assert.Equal(BooleanResultBody.NewBody, d.ResultBodySource);
    }

    [Fact]
    public void EffectiveUnite_ToolIdentitySurvives_Succeeds()
    {
        var d = BooleanResultVerificationPolicy.ClassifyAccounting(Facts(
            BooleanOperationType.Unite, before: 3, after: 2,
            targetAlive: false, toolAlive: true, newBodies: 0));
        Assert.Equal(BooleanVerificationOutcome.Success, d.Outcome);
        Assert.Equal(BooleanResultBody.ToolIdentity, d.ResultBodySource);
    }

    [Fact]
    public void DisjointSubtract_LiveFailureMatrix_IsNoEffect_NotFailure()
    {
        // The 2026-10-20 19:19 live case: subtract committed, both bodies
        // intact, target box unchanged. Previously "实体数量为 2，预期 1" +
        // misattributed verification failure. Now: honest no-effect verdict
        // (the executor rolls the committed no-op feature back — restored
        // geometry AND feature history — and never reports success).
        var target = Box(0, 0, 0, 10, 10, 10);
        var d = BooleanResultVerificationPolicy.ClassifyAccounting(Facts(
            BooleanOperationType.Subtract, before: 2, after: 2,
            targetAlive: true, toolAlive: true, newBodies: 0,
            targetBefore: target, targetAfter: target));
        Assert.Equal(BooleanVerificationOutcome.NoEffect, d.Outcome);
        Assert.NotEqual(BooleanVerificationOutcome.Success, d.Outcome);
    }

    [Fact]
    public void DisjointIntersect_IsNoEffect()
    {
        var target = Box(0, 0, 0, 10, 10, 10);
        var d = BooleanResultVerificationPolicy.ClassifyAccounting(Facts(
            BooleanOperationType.Intersect, before: 2, after: 2,
            targetAlive: true, toolAlive: true, newBodies: 0,
            targetBefore: target, targetAfter: target));
        Assert.Equal(BooleanVerificationOutcome.NoEffect, d.Outcome);
    }

    [Fact]
    public void Unite_CountUnchanged_IsFailure_NeverNoEffect()
    {
        // Unite cannot be a no-op; an unchanged count is a real failure.
        var target = Box(0, 0, 0, 10, 10, 10);
        var d = BooleanResultVerificationPolicy.ClassifyAccounting(Facts(
            BooleanOperationType.Unite, before: 2, after: 2,
            targetAlive: true, toolAlive: true, newBodies: 0,
            targetBefore: target, targetAfter: target));
        Assert.Equal(BooleanVerificationOutcome.Failure, d.Outcome);
    }

    [Fact]
    public void Subtract_CountUnchanged_TargetChanged_IsFailure()
    {
        // Count unchanged but the target MOVED: neither effective nor
        // no-effect — the state is unexplainable, refuse.
        var d = BooleanResultVerificationPolicy.ClassifyAccounting(Facts(
            BooleanOperationType.Subtract, before: 2, after: 2,
            targetAlive: true, toolAlive: true, newBodies: 0,
            targetBefore: Box(0, 0, 0, 10, 10, 10),
            targetAfter: Box(5, 0, 0, 15, 10, 10)));
        Assert.Equal(BooleanVerificationOutcome.Failure, d.Outcome);
    }

    [Fact]
    public void CountDropsByTwo_IsFailure()
    {
        var d = BooleanResultVerificationPolicy.ClassifyAccounting(Facts(
            BooleanOperationType.Unite, before: 3, after: 1,
            targetAlive: false, toolAlive: false, newBodies: 0));
        Assert.Equal(BooleanVerificationOutcome.Failure, d.Outcome);
    }

    [Fact]
    public void Effective_CountDrop_AmbiguousIdentities_IsFailure()
    {
        // Both identities gone, no new body — result unattributable.
        var d = BooleanResultVerificationPolicy.ClassifyAccounting(Facts(
            BooleanOperationType.Subtract, before: 2, after: 1,
            targetAlive: false, toolAlive: false, newBodies: 0));
        Assert.Equal(BooleanVerificationOutcome.Failure, d.Outcome);
    }

    [Fact]
    public void NoEffect_RequiresUnreadableTargetBox_ToBeFailure()
    {
        // Count unchanged but the target box is unreadable: the no-effect
        // claim is unverifiable → fail (never fake a no-effect pass).
        var d = BooleanResultVerificationPolicy.ClassifyAccounting(Facts(
            BooleanOperationType.Subtract, before: 2, after: 2,
            targetAlive: true, toolAlive: true, newBodies: 0,
            targetBefore: null, targetAfter: null));
        Assert.Equal(BooleanVerificationOutcome.Failure, d.Outcome);
    }

    // ---------- stage 2: containment (necessary conditions kept) ----------

    [Fact]
    public void Containment_Subtract_ResultInsideTarget_Ok()
    {
        var ok = BooleanResultVerificationPolicy.VerifyContainment(Facts(
            BooleanOperationType.Subtract, 2, 1,
            result: Box(0, 0, 0, 8, 8, 8),
            targetBefore: Box(0, 0, 0, 10, 10, 10),
            toolBefore: Box(20, 20, 20, 30, 30, 30)));
        Assert.True(ok.Ok);
    }

    [Fact]
    public void Containment_Subtract_ResultEscapesTarget_Fails()
    {
        var ok = BooleanResultVerificationPolicy.VerifyContainment(Facts(
            BooleanOperationType.Subtract, 2, 1,
            result: Box(0, 0, 0, 12, 8, 8),
            targetBefore: Box(0, 0, 0, 10, 10, 10),
            toolBefore: Box(20, 20, 20, 30, 30, 30)));
        Assert.False(ok.Ok);
        Assert.Contains("目标体", ok.Reason);
    }

    [Fact]
    public void Containment_Intersect_ResultInsideBoth_Ok()
    {
        var ok = BooleanResultVerificationPolicy.VerifyContainment(Facts(
            BooleanOperationType.Intersect, 2, 1,
            result: Box(2, 2, 2, 8, 8, 8),
            targetBefore: Box(0, 0, 0, 10, 10, 10),
            toolBefore: Box(1, 1, 1, 9, 9, 9)));
        Assert.True(ok.Ok);
    }

    [Fact]
    public void Containment_Intersect_ResultOutsideTool_Fails()
    {
        var ok = BooleanResultVerificationPolicy.VerifyContainment(Facts(
            BooleanOperationType.Intersect, 2, 1,
            result: Box(2, 2, 2, 10.5, 8, 8),
            targetBefore: Box(0, 0, 0, 12, 10, 10),
            toolBefore: Box(1, 1, 1, 9, 9, 9)));
        Assert.False(ok.Ok);
    }

    [Fact]
    public void Containment_Unite_ResultContainsBothInputs_Ok()
    {
        var ok = BooleanResultVerificationPolicy.VerifyContainment(Facts(
            BooleanOperationType.Unite, 2, 1,
            result: Box(0, 0, 0, 30, 30, 30),
            targetBefore: Box(0, 0, 0, 10, 10, 10),
            toolBefore: Box(20, 20, 20, 30, 30, 30)));
        Assert.True(ok.Ok);
    }

    [Fact]
    public void Containment_Unite_ResultMissesToolBox_Fails()
    {
        // Necessary condition must cover BOTH inputs: a result containing
        // only the target (tool escaped) is not a provable unite.
        var ok = BooleanResultVerificationPolicy.VerifyContainment(Facts(
            BooleanOperationType.Unite, 2, 1,
            result: Box(0, 0, 0, 10, 10, 10),
            targetBefore: Box(0, 0, 0, 10, 10, 10),
            toolBefore: Box(20, 20, 20, 30, 30, 30)));
        Assert.False(ok.Ok);
        Assert.Contains("并集", ok.Reason);
    }

    [Fact]
    public void Containment_UnreadableBoxes_Fail_NeverPass()
    {
        var ok = BooleanResultVerificationPolicy.VerifyContainment(Facts(
            BooleanOperationType.Subtract, 2, 1,
            result: null,
            targetBefore: Box(0, 0, 0, 10, 10, 10),
            toolBefore: Box(20, 20, 20, 30, 30, 30)));
        Assert.False(ok.Ok);
        Assert.Contains("无法读取", ok.Reason);
    }

    [Fact]
    public void Containment_ToleranceBoundary_Ok()
    {
        // Within the documented 0.01mm tolerance the containment holds.
        var ok = BooleanResultVerificationPolicy.VerifyContainment(Facts(
            BooleanOperationType.Subtract, 2, 1,
            result: Box(0, 0, 0, 10.005, 10, 10),
            targetBefore: Box(0, 0, 0, 10, 10, 10),
            toolBefore: Box(20, 20, 20, 30, 30, 30)));
        Assert.True(ok.Ok);
    }

    [Fact]
    public void SecondSubtract_AfterFirstConsumedTool_CannotReuseDeadToolRef()
    {
        // The 2026-10-21 13:51 live sequence: after the first subtract
        // (CYLINDER(2) − BLOCK(1) → SUBTRACT(3), tool consumed), the part
        // has ONE body. A second subtract naming the dead BLOCK(1) must be
        // refused upstream (body ref MISS) — the accounting policy never
        // sees a two-body fact set to bless. Here: a one-candidate fact set
        // cannot satisfy the "both identities alive" no-effect shape, and a
        // fact set with a dead tool that loses the target identity is a
        // failure, never a success.
        var oneBodyLeft = Facts(
            BooleanOperationType.Subtract, before: 1, after: 1,
            targetAlive: true, toolAlive: false, newBodies: 0,
            targetBefore: Box(0, 0, 0, 10, 10, 10),
            targetAfter: Box(0, 0, 0, 10, 10, 10));
        var d = BooleanResultVerificationPolicy.ClassifyAccounting(oneBodyLeft);
        // Count unchanged with a consumed tool matches NEITHER the effective
        // (count −1) nor the no-effect (both alive) shape.
        Assert.Equal(BooleanVerificationOutcome.Failure, d.Outcome);
    }
}
