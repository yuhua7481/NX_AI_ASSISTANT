using NXAI.Assistant.Core;
using Xunit;

namespace NXAI.Assistant.Tests;

/// <summary>
/// Phase 10.2: full 18-tool self-check coverage — scheduling, report
/// completeness, tool-coverage accounting and isolation contracts (offline
/// pins on the implementation source and the shared DTOs; the actual
/// NX2512 execution of every case is the self-check button's live run, a
/// separate acceptance item that offline tests can never substitute).
/// </summary>
public class GeometrySelfCheckCoverageTests
{
    private static string SelfCheckSource =>
        File.ReadAllText(Path.Combine(
            AppContext.BaseDirectory, "..", "..", "..", "..", "..",
            "src", "NXAI.Assistant.NxHost.NX2512", "NxGeometrySelfCheck.cs"));

    private static string RegistrySource =>
        File.ReadAllText(Path.Combine(
            AppContext.BaseDirectory, "..", "..", "..", "..", "..",
            "src", "NXAI.Assistant.Core", "CreateBlockTool.cs"));

    // ---------- scheduling: every tool group has a registered case ----------

    [Fact]
    public void RunAll_Schedules_AllThirteenCases()
    {
        // 5 original + 8 new: cone taper, booleans, simple hole, standard
        // holes, edge finishes, linear pattern, circular pattern, mirror,
        // negative sweep.
        Assert.Contains("CheckConeTaper(tempPart)", SelfCheckSource);
        Assert.Contains("CheckBooleanOperations(tempPart)", SelfCheckSource);
        Assert.Contains("CheckSimpleHole(tempPart)", SelfCheckSource);
        Assert.Contains("CheckStandardHoles(tempPart)", SelfCheckSource);
        Assert.Contains("CheckEdgeFinishes(tempPart)", SelfCheckSource);
        Assert.Contains("CheckLinearPattern(tempPart)", SelfCheckSource);
        Assert.Contains("CheckCircularPattern(tempPart)", SelfCheckSource);
        Assert.Contains("CheckMirror(tempPart)", SelfCheckSource);
        Assert.Contains("CheckNegativeAndRollback(tempPart)", SelfCheckSource);
    }

    [Fact]
    public void CoverageCounter_MatchesRegistryToolCount()
    {
        // 18 registered tools (counted from the registry source) — the
        // self-check's RegisteredToolCount constant must equal it.
        int registered = System.Text.RegularExpressions.Regex.Matches(
            RegistrySource, @"registry\.Register\(new ToolDefinition\(").Count;
        Assert.Equal(18, registered);
        Assert.Contains("RegisteredToolCount = 18", SelfCheckSource);
    }

    // ---------- every tool appears in some case ----------

    [Fact]
    public void Cases_Exercise_AllEighteenTools()
    {
        // Tool names via their production executors called inside cases:
        // the executors are the ONLY path to the builders (no mocks).
        foreach (var executor in new[]
        {
            "ExecuteCreateBlock", "ExecuteCreateCylinder", "ExecuteCreateCone",
            "ExecuteCreateSphere", "ExecuteCreateSketch", "ExecuteCreateExtrude",
            "ExecuteCreateRevolve", "ExecuteBooleanOperation", "ExecuteCreateHole",
            "ExecuteCreateStandardHole", "ExecuteCreateFillet", "ExecuteCreateChamfer",
            "ExecuteCreateLinearPattern", "ExecuteCreateCircularPattern",
            "ExecuteCreateMirror",
        })
        {
            Assert.True(SelfCheckSource.Contains(executor),
                $"self-check never calls {executor}");
        }
        // Boolean's three operations
        Assert.Contains("BooleanOperationType.Unite", SelfCheckSource);
        Assert.Contains("BooleanOperationType.Subtract", SelfCheckSource);
        Assert.Contains("BooleanOperationType.Intersect", SelfCheckSource);
        // Pattern/mirror both modes
        Assert.Contains("LinearPatternMode.Geometry", SelfCheckSource);
        Assert.Contains("LinearPatternMode.Feature", SelfCheckSource);
        Assert.Contains("CircularPatternMode.Geometry", SelfCheckSource);
        Assert.Contains("CircularPatternMode.Feature", SelfCheckSource);
        Assert.Contains("MirrorMode.Geometry", SelfCheckSource);
        Assert.Contains("MirrorMode.Feature", SelfCheckSource);
    }

    // ---------- negative and rollback sweep ----------

    [Fact]
    public void NegativeSweep_CoversEmptyAmbiguousAndStale()
    {
        Assert.Contains("caseM.neg.empty", SelfCheckSource);
        Assert.Contains("caseM.neg.ambiguous", SelfCheckSource);
        Assert.Contains("caseM.neg.stale", SelfCheckSource);
        // The sweep asserts ZERO geometry landed from refused calls.
        Assert.Contains("caseM.neg.geometry", SelfCheckSource);
    }

    [Fact]
    public void Selection_IsDrivenThroughRealNxManager_ClearFirst()
    {
        // The selection-dependent tools must see the REAL NX selection —
        // driven via RequestSelections/ClearGlobalSelectionList (the
        // NXOpenUI.dll reflection-verified members), never mocked results.
        // Phase 10.4 P1: SelectInNx must CLEAR before setting — the live
        // failure showed the previous op's 12 dead edges leaking into the
        // chamfer call ("24 object(s)").
        Assert.Contains("RequestSelections(", SelfCheckSource);
        Assert.Contains("ClearGlobalSelectionList()", SelfCheckSource);
        Assert.Matches(
            @"ClearGlobalSelectionList\(\);\s*?
\s*selection\.RequestSelections",
            SelfCheckSource);
    }

    [Fact]
    public void FeatureCircularPattern_AssertsBodyGrowthPerLiveEvidence()
    {
        // Phase 10.4 P2: the live run PROVED a circular FEATURE pattern of
        // the block body grows the part by count−1 (commit alive,
        // instances=6, bodies 31→34 for count=4). The assertion must pin
        // that verified growth — not the linear-hole "unchanged" semantics.
        Assert.Contains("beforeFeat + 3", SelfCheckSource);
        Assert.DoesNotContain("特征圆形阵列后实体数应不变", SelfCheckSource);
    }

    // ---------- isolation and restore ----------

    [Fact]
    public void Isolation_TempPartClosedWithoutSave_AndUserPartRestored()
    {
        Assert.Contains("CloseModified.CloseModified", SelfCheckSource);
        Assert.Contains("SetDisplay(originalDisplayed", SelfCheckSource);
        Assert.Contains("SetWork(originalWorkPart)", SelfCheckSource);
        Assert.Contains("File.Delete(tempCopyPath)", SelfCheckSource);
    }

    // ---------- report DTO: first-page fields ----------

    [Fact]
    public void ReportDto_CarriesCoverageFields()
    {
        var type = typeof(GeometrySelfCheckReport);
        Assert.NotNull(type.GetProperty("RegisteredToolCount"));
        Assert.NotNull(type.GetProperty("CoveredToolCount"));
        Assert.NotNull(type.GetProperty("NegativePassedCount"));
        Assert.NotNull(type.GetProperty("NegativeFailedCount"));
        Assert.NotNull(type.GetProperty("CleanupResult"));
    }

    [Fact]
    public void CleanupResult_DerivedFromCleanupCases()
    {
        // The RunAll return wires CleanupResult from the cleanup/restore
        // case failures — a failed close/restore must surface on page one.
        Assert.Contains("cleanupFailures.Count == 0", SelfCheckSource);
        Assert.Contains("CleanupResult = cleanupResult", SelfCheckSource);
    }

    [Fact]
    public void CoveredToolCount_ComputationPinsGroups()
    {
        // The accounting reads the case-name groups; every group maps to a
        // fixed tool count (4 primitives, 5 holes, 2 edges, patterns, mirror, boolean).
        Assert.Contains("covered += 4;", SelfCheckSource);
        Assert.Contains("covered += 5;", SelfCheckSource);
        Assert.Contains("covered += 2;", SelfCheckSource);
        Assert.Contains("Math.Min(covered, RegisteredToolCount)", SelfCheckSource);
    }
}
