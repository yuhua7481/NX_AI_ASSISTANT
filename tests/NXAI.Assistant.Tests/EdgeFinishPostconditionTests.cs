using Xunit;

namespace NXAI.Assistant.Tests;

/// <summary>
/// Phase 9.4.1 regression tests for the edge-finish postcondition verdict
/// (the 2026-10-23 live failure: a healthy BLEND feature was rolled back
/// because the old code string-matched FeatureType=="EDGE_BLEND" and did an
/// unstable JournalIdentifier round-trip). The verdict is now a pure
/// function (NxToolExecutor.EdgeFinishPostcondition) with every
/// sub-condition testable offline. NXOpen.xml facts pinned here: the real
/// committed type for a fillet is Features.EdgeBlend (FeatureType string
/// "BLEND"), for a chamfer Features.Chamfer ("CHAMFER"); Feature.GetFaces
/// ("faces created by the feature") must be non-empty.
/// </summary>
public class EdgeFinishPostconditionTests
{
    private const string LiveFailureNote =
        "2026-10-23 live: commit returned EdgeBlend BLEND(2) type=BLEND, alive=True, " +
        "bodies 1→1 — must PASS.";

    [Fact]
    public void HealthyBlend_LiveFailureCase_Passes()
    {
        var (pass, reason) = NxHostVerdicts.EdgeFinishPostcondition(
            committed: true, alive: true, typeMatches: true,
            faceCount: 1, bodiesBefore: 1, bodiesAfter: 1);
        Assert.True(pass, LiveFailureNote);
        Assert.Equal("ok", reason);
    }

    [Fact]
    public void HealthyBlend_WithBodiesUnchanged_Passes()
    {
        var (pass, _) = NxHostVerdicts.EdgeFinishPostcondition(
            true, alive: true, typeMatches: true, faceCount: 12, 2, 2);
        Assert.True(pass);
    }

    [Fact]
    public void NoCommittedFeature_Fails()
    {
        var (pass, reason) = NxHostVerdicts.EdgeFinishPostcondition(
            committed: false, alive: false, typeMatches: false, faceCount: -1, 1, 1);
        Assert.False(pass);
        Assert.Contains("CommitFeature", reason);
    }

    [Fact]
    public void DeadFeature_Fails()
    {
        var (pass, reason) = NxHostVerdicts.EdgeFinishPostcondition(
            true, alive: false, typeMatches: true, faceCount: 1, 1, 1);
        Assert.False(pass);
        Assert.Contains("失效", reason);
    }

    [Fact]
    public void WrongFeatureType_Fails()
    {
        // A chamfer tool committing some other feature class must fail.
        var (pass, reason) = NxHostVerdicts.EdgeFinishPostcondition(
            true, alive: true, typeMatches: false, faceCount: 2, 1, 1);
        Assert.False(pass);
        Assert.Contains("不匹配", reason);
    }

    [Fact]
    public void ZeroFaces_Fails_AsNothingBuilt()
    {
        var (pass, reason) = NxHostVerdicts.EdgeFinishPostcondition(
            true, alive: true, typeMatches: true, faceCount: 0, 1, 1);
        Assert.False(pass);
        Assert.Contains("未产生任何面", reason);
    }

    [Fact]
    public void BodyCountChanged_Fails()
    {
        var (pass, reason) = NxHostVerdicts.EdgeFinishPostcondition(
            true, alive: true, typeMatches: true, faceCount: 2, 1, 2);
        Assert.False(pass);
        Assert.Contains("实体数量变化", reason);
    }

    [Fact]
    public void Verdict_CoversEverySubCondition_WithDistinctReasons()
    {
        // Each failing sub-condition yields a DISTINCT reason so a real
        // failure names its actual cause (no swallowed diagnostics).
        var reasons = new HashSet<string>
        {
            NxHostVerdicts.EdgeFinishPostcondition(false, true, true, 1, 1, 1).Reason,
            NxHostVerdicts.EdgeFinishPostcondition(true, false, true, 1, 1, 1).Reason,
            NxHostVerdicts.EdgeFinishPostcondition(true, true, false, 1, 1, 1).Reason,
            NxHostVerdicts.EdgeFinishPostcondition(true, true, true, 0, 1, 1).Reason,
            NxHostVerdicts.EdgeFinishPostcondition(true, true, true, 1, 1, 2).Reason,
        };
        Assert.Equal(5, reasons.Count);
    }

    [Fact]
    public void ManagedFeatureTypes_DocumentedInExecutorSource()
    {
        // The fix relies on `is EdgeBlend` / `is Chamfer` in the executor —
        // verify by source inspection (the test project has no NXOpen
        // reference; the executor project compiles against NX2512's own
        // assemblies, so the type check is compile-time guaranteed there).
        var src = System.IO.File.ReadAllText(
            System.IO.Path.Combine(AppContext.BaseDirectory,
                "..", "..", "..", "..", "..", "src", "NXAI.Assistant.NxHost.NX2512",
                "EdgeFinishToolExecutor.cs"));
        Assert.Contains("is NXOpen.Features.EdgeBlend", src);
        Assert.Contains("is NXOpen.Features.Chamfer", src);
        // The old broken string comparison must be gone (comments may
        // still reference the history — only executable comparisons count).
        Assert.DoesNotContain("== \"EDGE_BLEND\"", src);
        Assert.DoesNotContain("FeatureType ?? \"\").ToUpperInvariant()", src);
    }
}

/// <summary>Test-facing shim for the internal verdict on the NxHost executor.</summary>
public static class NxHostVerdicts
{
    public static (bool Pass, string Reason) EdgeFinishPostcondition(
        bool committed, bool alive, bool typeMatches, int faceCount,
        int bodiesBefore, int bodiesAfter) =>
        NXAI.Assistant.NxHost.NX2512.NxToolExecutor.EdgeFinishPostcondition(
            committed, alive, typeMatches, faceCount, bodiesBefore, bodiesAfter);

    public static (bool Pass, string Reason) LinearPatternPostcondition(
        bool committed, bool alive, bool typeMatches, int instanceCount, int requestedCount,
        int bodiesBefore, int bodiesAfter, int expectedNewBodies, bool isGeometryMode) =>
        NXAI.Assistant.NxHost.NX2512.NxToolExecutor.LinearPatternPostcondition(
            committed, alive, typeMatches, instanceCount, requestedCount,
            bodiesBefore, bodiesAfter, expectedNewBodies, isGeometryMode);

    public static (bool Pass, string Reason) MirrorPostcondition(
        bool committed, bool alive, bool typeMatches, int featureBodyCount,
        int seedCount, int bodiesBefore, int bodiesAfter, int expectedNewBodies,
        bool isGeometryMode) =>
        NXAI.Assistant.NxHost.NX2512.NxToolExecutor.MirrorPostcondition(
            committed, alive, typeMatches, featureBodyCount, seedCount,
            bodiesBefore, bodiesAfter, expectedNewBodies, isGeometryMode);

    public static (bool Pass, string Reason) CircularPatternPostcondition(
        bool committed, bool alive, bool typeMatches, int instanceCount, int requestedCount,
        int bodiesBefore, int bodiesAfter, int expectedNewBodies, bool isGeometryMode) =>
        NXAI.Assistant.NxHost.NX2512.NxToolExecutor.CircularPatternPostcondition(
            committed, alive, typeMatches, instanceCount, requestedCount,
            bodiesBefore, bodiesAfter, expectedNewBodies, isGeometryMode);
}
