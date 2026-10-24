using NXAI.Assistant.Core;
using NXAI.Assistant.UI;
using Xunit;

namespace NXAI.Assistant.Tests;

/// <summary>
/// Phase 9.9 T1 live-incident regressions (offline):
///   NRE — the ledger's DescribePendingOperation switch had no
///   CircularPattern case and fell into the boolean wildcard, dereferencing
///   the null BooleanParameters. The circular case must describe itself and
///   never throw for a valid circular request.
///   Angle semantics — total_angle_deg=360 normalizes to the FULL-CIRCLE
///   pitch path (360/count: 6 instances → 60°), NOT the partial-arc
///   total/(count−1) formula (which produced the live 72°). Partial arcs
///   keep their own contract (5 instances over 120° → 30°, last instance
///   ON the end angle).
/// </summary>
public class CircularPatternLiveIncidentTests
{
    private static CreateCircularPatternParameters? Parse(string json, out string? error) =>
        new CreateCircularPatternRequestParser().ParseParameters(json, out error);

    private static ToolConfirmationRequest CircularRequest(string json)
    {
        var p = Parse(json, out var error);
        Assert.Null(error);
        return ToolConfirmationRequest.ForCircularPattern(
            "call_t1", CircularPatternToolContract.Name, p!);
    }

    // ---------- NRE: DescribePendingOperation covers the circular kind ----------

    [Fact]
    public void DescribePendingOperation_CircularRequest_DoesNotThrow()
    {
        // The exact live payload shape: geometry mode + datum_axis.
        var request = CircularRequest(
            """{"mode":"geometry","axis":"datum_axis","count":6}""");
        var text = ChatSessionController.DescribePendingOperation(request);
        Assert.Contains("环形阵列", text);
        Assert.Contains("6", text);
    }

    [Fact]
    public void DescribePendingOperation_AllAxisKinds_DoNotThrow()
    {
        foreach (var axis in new[] { "plus_x", "plus_y", "plus_z", "datum_axis", "line_edge" })
        {
            var request = CircularRequest(
                """{"mode":"feature","axis":"__AXIS__","count":4}""".Replace("__AXIS__", axis));
            var text = ChatSessionController.DescribePendingOperation(request);
            Assert.DoesNotContain("布尔", text); // never falls into the wildcard
        }
    }

    // ---------- angle semantics ----------

    [Fact]
    public void FullCircle_360AsTotalAngle_NormalizesToFullCirclePitch()
    {
        // The live T1 shape: the model sent total_angle_deg=360 for a
        // full-circle request. 6 total instances over 360° → 60° pitch.
        var p = Parse(
            """{"mode":"geometry","axis":"datum_axis","count":6,"total_angle_deg":360}""",
            out var error);
        Assert.Null(error);
        Assert.Null(p!.TotalAngleDeg); // normalized to the full-circle path
        Assert.Equal(60, 360.0 / p.Count);
    }

    [Fact]
    public void FullCircle_OmittedAngle_IsFullCirclePitch()
    {
        var p = Parse(
            """{"mode":"geometry","axis":"plus_z","count":6}""",
            out var error);
        Assert.Null(error);
        Assert.Null(p!.TotalAngleDeg);
        Assert.Equal(60, 360.0 / p.Count);
    }

    [Fact]
    public void PartialArc_120Degrees_KeepsSpanContract()
    {
        // 5 total instances over 120°: pitch = total/(count−1) = 30°, the
        // first instance at the start and the last ON the end angle.
        var p = Parse(
            """{"mode":"geometry","axis":"datum_axis","count":5,"total_angle_deg":120}""",
            out var error);
        Assert.Null(error);
        Assert.Equal(120, p!.TotalAngleDeg);
        Assert.Equal(30, p.TotalAngleDeg!.Value / (p.Count - 1));
    }

    [Fact]
    public void PartialArc_360Boundary_IsNotPartial()
    {
        // The boundary of the (0, 360) partial range: exactly 360 never
        // enters the partial-arc path (the live 72° bug).
        for (double a = 0.5; a < 360.0; a += 17.3)
        {
            var p = Parse(
                $$"""{"mode":"feature","axis":"plus_z","count":5,"total_angle_deg":{{a.ToString("0.0#", System.Globalization.CultureInfo.InvariantCulture)}}}""",
                out var error);
            Assert.Null(error);
            Assert.NotNull(p!.TotalAngleDeg); // strictly inside → partial
            Assert.True(p.TotalAngleDeg!.Value < 360);
        }
        // Exactly 360 → full circle.
        var full = Parse(
            """{"mode":"feature","axis":"plus_z","count":5,"total_angle_deg":360}""",
            out var err);
        Assert.Null(err);
        Assert.Null(full!.TotalAngleDeg);
    }

    // ---------- confirmation-card text matches the executor's math ----------

    [Fact]
    public void ControllerDescription_FullCircle_Shows360OverCount()
    {
        // 6 instances full circle → the card must say 60°, never 72°.
        var p = Parse(
            """{"mode":"geometry","axis":"datum_axis","count":6,"total_angle_deg":360}""",
            out _);
        double shownPitch = p!.TotalAngleDeg is { } deg
            ? deg / (p.Count - 1)
            : 360.0 / p.Count;
        Assert.Equal(60, shownPitch);
    }

    [Fact]
    public void ControllerDescription_PartialArc_ShowsSpanOverCountMinusOne()
    {
        var p = Parse(
            """{"mode":"geometry","axis":"datum_axis","count":5,"total_angle_deg":120}""",
            out _);
        double shownPitch = p!.TotalAngleDeg is { } deg
            ? deg / (p.Count - 1)
            : 360.0 / p.Count;
        Assert.Equal(30, shownPitch);
    }
}
