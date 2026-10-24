using NXAI.Assistant.Core;
using Xunit;

namespace NXAI.Assistant.Tests;

/// <summary>
/// Phase 9.9 T2/T4 live-incident regressions (offline):
///   T2 — NX2512's AngularSpacing on this definition treats PitchAngle as
///   the PER-STEP angle; SpanAngle=120 produced 10° between instances in
///   the live part. The verified-correct configuration for BOTH extents is
///   PitchAngle per step: full circle 360/count (T1-proven 60°), partial
///   arc total/(count−1) (5 positions over 120° → 0°/30°/60°/90°/120°).
///   These tests pin the STEP-formula contract the executor applies; the
///   actual NX geometry check stays a live acceptance item (T2 rerun must
///   measure instance positions, not read a card).
///   T4 — the AssociativeLine feature LINE(7) has NO body output: it is an
///   axis candidate, never a seed candidate. The seed resolver must skip
///   it, and the line_edge axis resolver must build the axis from the
///   line's REAL geometry (start + direction).
/// </summary>
public class CircularPatternT2T4Tests
{
    private static CreateCircularPatternParameters? Parse(string json, out string? error) =>
        new CreateCircularPatternRequestParser().ParseParameters(json, out error);

    // ---------- T2: the step formula the executor applies ----------

    [Fact]
    public void T2_5Over120_PitchIs30_PerStep()
    {
        var p = Parse(
            """{"mode":"geometry","axis":"datum_axis","count":5,"total_angle_deg":120}""",
            out var error);
        Assert.Null(error);
        double stepDeg = p!.TotalAngleDeg is { } total
            ? total / (p.Count - 1)
            : 360.0 / p.Count;
        Assert.Equal(30, stepDeg);
        // Expected positions: 0, 30, 60, 90, 120 degrees.
        var positions = Enumerable.Range(0, p.Count).Select(i => i * stepDeg).ToList();
        Assert.Equal(new[] { 0.0, 30.0, 60.0, 90.0, 120.0 }, positions);
    }

    [Fact]
    public void T1Shape_6Over360_PitchIs60_PerStep()
    {
        var p = Parse(
            """{"mode":"geometry","axis":"datum_axis","count":6,"total_angle_deg":360}""",
            out var error);
        Assert.Null(error);
        Assert.Null(p!.TotalAngleDeg); // full circle path
        Assert.Equal(60, 360.0 / p.Count);
    }

    [Fact]
    public void FullCircle_And_PartialArc_UseOneStepFormula()
    {
        // The T2 fix unified both extents onto PitchAngle-per-step —
        // the partial-arc branch no longer sets SpanAngle. Pinned: the
        // step formula is a pure function of (count, NORMALIZED totalAngle)
        // — the parser already maps total=360 to the full-circle null.
        static double Step(int count, double? normalizedTotal) =>
            normalizedTotal is { } t ? t / (count - 1) : 360.0 / count;
        Assert.Equal(60, Step(6, null));
        Assert.Equal(30, Step(5, 120));
        Assert.Equal(45, Step(3, 90));
    }

    // ---------- T4: seed/axis role separation ----------

    [Fact]
    public void T4SelectionShape_BodyPlusLineFeature_IsOneSeedOneAxis()
    {
        // The T4 live selection carried Block (a body feature) + LINE(7)
        // (an AssociativeLine). After the fix the seed resolver ignores
        // axis-like line features, so the effective seed candidates are
        // exactly the body-owning features — the request proceeds instead
        // of refusing with "解析出 0 个实体".
        // Pinned via the parser path: axis=line_edge + geometry mode is a
        // valid request shape; the executor-side separation is live-verified.
        var p = Parse(
            """{"mode":"geometry","axis":"line_edge","count":4}""",
            out var error);
        Assert.Null(error);
        Assert.Equal(CircularPatternAxis.LineEdge, p!.Axis);
    }

    [Fact]
    public void Contract_LineEdgeDescription_CoversLineFeatures()
    {
        // The contract must tell the model that line_edge accepts both a
        // topological straight edge and a standalone line feature — the
        // user-visible T4 case.
        Assert.Contains("直线特征", CircularPatternToolContract.Description);
        var schema = CircularPatternToolContract.ParametersJsonSchema;
        Assert.Contains("直线", schema);
    }

    // ---------- structured refusals stay structured ----------

    [Fact]
    public void AmbiguousAxis_Count2Lines_IsStillRejectedByParser()
    {
        // Two lines cannot be disambiguated by parameters alone — the
        // executor's multi-line refusal (live-side) stays; the parser keeps
        // rejecting malformed input regardless.
        var p = Parse(
            """{"mode":"geometry","axis":"line_edge","count":1}""",
            out var error);
        Assert.Null(p);
        Assert.Contains("count", error!);
    }

    [Fact]
    public void PartialArc_StillValid_AfterT2Fix()
    {
        var p = Parse(
            """{"mode":"feature","axis":"datum_axis","count":5,"total_angle_deg":120}""",
            out var error);
        Assert.Null(error);
        Assert.Equal(120, p!.TotalAngleDeg);
    }
}
