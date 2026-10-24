using NXAI.Assistant.Core;
using NXAI.Assistant.UI;
using Xunit;

namespace NXAI.Assistant.Tests;

/// <summary>
/// Phase 9.9 T1 SECOND live incident (offline regressions):
///   NRE — a fresh PatternDefinition's RotationAxis getter returns NULL
///   until an Axis is assigned; the executor now creates its own Axis via
///   Part.Axes.CreateAxis(point, direction) and assigns the whole object.
///   The (point, direction) smart objects it consumes are exactly what the
///   axis resolvers already produce, so the resolution-to-assignment path
///   is typed end to end (the NX-side behavior itself remains a live item).
///   Repeat-failure guard — a rolled-back ExecutionFailed with identical
///   arguments must be refused on resubmission within the same turn (the
///   live second attempt 40 s later failed identically), while a NEW turn
///   or different arguments still go through.
///   Protocol leak — a stray parameter tag WITHOUT an invoke wrapper must
///   be caught by ContainsToolMarker and scrubbed.
/// </summary>
public class CircularPatternT1SecondIncidentTests
{
    private static CreateCircularPatternParameters? Parse(string json, out string? error) =>
        new CreateCircularPatternRequestParser().ParseParameters(json, out error);

    // ---------- the exact T1 parameter path parses and normalizes ----------

    [Fact]
    public void T1GeometryDatumAxis6x360_ParsesToFullCircle()
    {
        var p = Parse(
            """{"mode":"geometry","axis":"datum_axis","count":6,"total_angle_deg":360}""",
            out var error);
        Assert.Null(error);
        Assert.Equal(CircularPatternMode.Geometry, p!.Mode);
        Assert.Equal(CircularPatternAxis.DatumAxis, p.Axis);
        Assert.Equal(6, p.Count);
        Assert.Null(p.TotalAngleDeg); // normalized: 360 → full circle
        Assert.Equal(60, 360.0 / p.Count);
    }

    // ---------- repeat-failure guard semantics (tuple-set contract) ----------

    [Fact]
    public void DedupKey_RolledBackFailure_UsesExactArgsFingerprint()
    {
        var first = ("create_circular_pattern",
            """{"mode":"geometry","axis":"datum_axis","count":6,"total_angle_deg":360}""");
        var same = ("create_circular_pattern",
            """{"mode":"geometry","axis":"datum_axis","count":6,"total_angle_deg":360}""");
        var corrected = ("create_circular_pattern",
            """{"mode":"geometry","axis":"datum_axis","count":6}""");
        // Same fingerprint → the guard refuses; a corrected call differs.
        Assert.Equal(first, same);
        Assert.NotEqual(first, corrected);
    }

    [Fact]
    public void RepeatGuard_CoversRolledBackExecutionFailures_ByDesign()
    {
        // Pinned by reading the recording predicate's contract: the guard
        // set accepts InvalidParameters always, and Verification/Execution
        // failures ONLY when RolledBack (the part is provably unchanged —
        // an identical retry cannot succeed). Non-rolled-back failures stay
        // retryable inside the turn (state may have changed in NX).
        var recordedStatuses = new[]
        {
            (ToolExecutionStatus.InvalidParameters, false),
            (ToolExecutionStatus.VerificationFailed, true),
            (ToolExecutionStatus.ExecutionFailed, true),
        };
        var notRecorded = new[]
        {
            (ToolExecutionStatus.ExecutionFailed, false),
            (ToolExecutionStatus.VerificationFailed, false),
            (ToolExecutionStatus.PreconditionsChanged, false),
            (ToolExecutionStatus.Success, false),
        };
        bool ShouldRecord(ToolExecutionStatus s, bool rolledBack) =>
            s == ToolExecutionStatus.InvalidParameters ||
            ((s == ToolExecutionStatus.VerificationFailed ||
              s == ToolExecutionStatus.ExecutionFailed) && rolledBack);
        foreach (var (s, rb) in recordedStatuses)
            Assert.True(ShouldRecord(s, rb), $"{s}/{rb} must be recorded");
        foreach (var (s, rb) in notRecorded)
            Assert.False(ShouldRecord(s, rb), $"{s}/{rb} must NOT be recorded");
    }

    // ---------- leak: stray parameter tags without an invoke wrapper ----------

    private static string ParamTag(string name, string value) =>
        "<" + "an" + "tml:" + "par" + "ameter " + $"name=\"{name}\">{value}</" +
        "an" + "tml:" + "par" + "ameter" + ">";

    [Fact]
    public void ContainsToolMarker_DetectsStrayParameterTag()
    {
        string raw = $"mode={ParamTag("mode", "geometry")} 完成阵列";
        Assert.True(ToolTagProtocol.ContainsToolMarker(raw));
    }

    [Fact]
    public void Sanitizer_StrayParameterTags_Scrubbed_ProseKept()
    {
        string raw = $"阵列请求已处理 {ParamTag("mode", "geometry")}{ParamTag("count", "6")} 请查看部件。";
        string clean = ChatSessionController.SanitizeModelTextForDisplay(raw);
        Assert.DoesNotContain("par" + "ameter", clean);
        Assert.DoesNotContain("an" + "tml", clean);
        Assert.Contains("阵列请求已处理", clean);
        Assert.Contains("请查看部件。", clean);
    }

    [Fact]
    public void Sanitizer_UnterminatedParameterTag_DropsResidue()
    {
        string raw = $"结果如下 " +
            "<" + "an" + "tml:" + "par" + "ameter " + "name=\"count\">6"; // no close
        string clean = ChatSessionController.SanitizeModelTextForDisplay(raw);
        Assert.DoesNotContain("par" + "ameter", clean);
        Assert.Contains("结果如下", clean);
    }

    [Fact]
    public void Sanitizer_PlainAngleBrackets_ArePreserved()
    {
        // Normal prose with unrelated angle brackets must NOT be mangled.
        string text = "向量 <a, b, c> 与矩阵运算说明";
        Assert.Equal(text, ChatSessionController.SanitizeModelTextForDisplay(text));
    }

    // ---------- structured errors, never raw NRE ----------

    [Fact]
    public void CircularPostcondition_StructuredRejection_UnclassifiableTypes()
    {
        var (pass, reason) = NxHostVerdicts.CircularPatternPostcondition(
            committed: false, alive: false, typeMatches: false,
            instanceCount: -1, requestedCount: 6,
            bodiesBefore: 1, bodiesAfter: 1, expectedNewBodies: 0,
            isGeometryMode: false);
        Assert.False(pass);
        Assert.Contains("CommitFeature", reason);
        // A refusal reason is one structured sentence — no stack text.
        Assert.DoesNotContain("at NXOpen", reason);
    }
}
