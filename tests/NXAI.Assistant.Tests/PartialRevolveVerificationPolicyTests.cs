using NXAI.Assistant.Core;
using Xunit;

namespace NXAI.Assistant.Tests;

/// <summary>
/// Partial-angle revolve postcondition policy — FACT-BASED verification
/// (live root cause 2026-10-19: sketch-curve-derived analytic envelopes
/// proved unreliable against real NX commits in both sketch read modes, so
/// no envelope check runs for <360°). The policy decides from
/// NX-observable facts collected by the host. Every failure names its fact;
/// nothing here can fake success.
/// </summary>
public class PartialRevolveVerificationPolicyTests
{
    private static CommittedRevolveFacts Facts(
        int bodiesBefore = 0,
        int bodiesAfter = 1,
        int newBodyCount = 1,
        bool newBodyAlive = true,
        bool ownershipReadable = true,
        bool ownedByCommittedFeature = true,
        int featureOwnedBodyCount = 1,
        bool sheetReadable = true,
        bool newBodyIsSheet = false,
        bool boxReadable = true,
        bool boxFinite = true) => new(
        bodiesBefore, bodiesAfter, newBodyCount,
        newBodyAlive, ownershipReadable, ownedByCommittedFeature, featureOwnedBodyCount,
        sheetReadable, newBodyIsSheet, boxReadable, boxFinite);

    // ------------------------------------------------------- success paths

    [Fact]
    public void CorrectCommittedSolid_Passes()
    {
        // The 60°/270° live shape: commit succeeded, one new live solid
        // owned by the committed feature, finite box → success.
        var (success, error) = PartialRevolveVerificationPolicy.Verify(Facts());
        Assert.True(success);
        Assert.Null(error);
    }

    [Fact]
    public void ExistingBodies_PlusOneNew_Passes()
    {
        // The part already had bodies; exactly one more appeared.
        var (success, _) = PartialRevolveVerificationPolicy.Verify(
            Facts(bodiesBefore: 3, bodiesAfter: 4));
        Assert.True(success);
    }

    // ------------------------------------------------------- failure paths

    [Fact]
    public void NoNewBody_Fails()
    {
        // Commit reported success but no geometry appeared.
        var (success, error) = PartialRevolveVerificationPolicy.Verify(
            Facts(bodiesAfter: 0, newBodyCount: 0));
        Assert.False(success);
        Assert.Contains("实体数量", error);
    }

    [Fact]
    public void MultipleNewBodies_Fail()
    {
        var (success, error) = PartialRevolveVerificationPolicy.Verify(Facts(newBodyCount: 2));
        Assert.False(success);
        Assert.Contains("多个新实体", error);
    }

    [Fact]
    public void DeadNewBody_Fails()
    {
        var (success, error) = PartialRevolveVerificationPolicy.Verify(Facts(newBodyAlive: false));
        Assert.False(success);
        Assert.Contains("已失效", error);
    }

    [Fact]
    public void UnreadableOwnership_FailsSafe()
    {
        // Feature.GetBodies threw: the body count alone NEVER proves
        // success — this must fail (safe), not pass.
        var (success, error) = PartialRevolveVerificationPolicy.Verify(
            Facts(ownershipReadable: false));
        Assert.False(success);
        Assert.Contains("无法读取本次旋转特征的关联实体", error);
    }

    [Fact]
    public void ForeignBody_Fails()
    {
        // The new body exists but the committed feature does not own it.
        var (success, error) = PartialRevolveVerificationPolicy.Verify(
            Facts(ownedByCommittedFeature: false, featureOwnedBodyCount: 1));
        Assert.False(success);
        Assert.Contains("不属于本次提交的旋转特征", error);
    }

    [Fact]
    public void FeatureWithNoBodies_Fails()
    {
        var (success, error) = PartialRevolveVerificationPolicy.Verify(
            Facts(ownedByCommittedFeature: false, featureOwnedBodyCount: 0));
        Assert.False(success);
        Assert.Contains("没有关联实体", error);
    }

    [Fact]
    public void SheetBody_Fails()
    {
        // A 270° sweep of a failed/open section can yield a sheet — rejected.
        var (success, error) = PartialRevolveVerificationPolicy.Verify(Facts(newBodyIsSheet: true));
        Assert.False(success);
        Assert.Contains("片体", error);
    }

    [Fact]
    public void UnreadableSheetState_FailsSafe()
    {
        var (success, error) = PartialRevolveVerificationPolicy.Verify(Facts(sheetReadable: false));
        Assert.False(success);
        Assert.Contains("无法读取新实体类型", error);
    }

    [Fact]
    public void UnreadableBox_Fails()
    {
        var (success, error) = PartialRevolveVerificationPolicy.Verify(Facts(boxReadable: false));
        Assert.False(success);
        Assert.Contains("无法读取新实体包围盒", error);
    }

    [Fact]
    public void NonFiniteBox_Fails()
    {
        var (success, error) = PartialRevolveVerificationPolicy.Verify(Facts(boxFinite: false));
        Assert.False(success);
        Assert.Contains("非有限值", error);
    }
}
