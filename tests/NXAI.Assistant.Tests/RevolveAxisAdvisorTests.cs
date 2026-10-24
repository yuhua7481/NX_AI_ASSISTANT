using NXAI.Assistant.Core;
using Xunit;

namespace NXAI.Assistant.Tests;

/// <summary>
/// RevolveAxisAdvisor — pre-execution revolve-axis scale ranking, decided
/// from sketch facts alone (no NX). All absolute axes are FEASIBLE (the
/// old coplanarity gate was a project-side restriction proven wrong
/// against live NX2512: NX accepts user-selected axes off the sketch
/// plane). What the advisor judges is the EXPECTED REVOLVE SCALE: a legal
/// axis whose revolve dwarfs the profile is a scale outlier, not
/// infeasible. The live Z-on-XY case is still caught before the executor
/// — as a scale correction, with X ranked first.
/// </summary>
public class RevolveAxisAdvisorTests
{
    private static RevolveAxisAdvisor.SketchFacts XyRectangle(
        double width = 20, double height = 10,
        double centerU = 150, double centerV = 10) => new(
        SketchPlaneMode.WcsXY, SketchProfileType.Rectangle,
        width, height, 0, centerU, centerV);

    private static RevolveAxisAdvisor.SketchFacts XyCircle(
        double diameter = 30, double centerU = 40, double centerV = 0) => new(
        SketchPlaneMode.WcsXY, SketchProfileType.Circle,
        0, 0, diameter, centerU, centerV);

    // ------------------------------------- the exact live case (Z on XY)

    [Fact]
    public void XySketch_RequestedZ_IsScaleOutlier_And_XRankedFirst()
    {
        // The live case: 20×10 rectangle at (u150, v10) on the XY plane.
        // Z (through the origin, normal to the plane) is geometrically
        // FEASIBLE per live NX — but its revolve envelope radius ~160.7 mm
        // is ~10× the profile scale (X: 15 mm) → scale outlier, X suggested.
        var advice = RevolveAxisAdvisor.Assess(XyRectangle(), RevolveAxisMode.WcsZ);

        Assert.True(advice.RequestedAxisFeasible); // feasible ≠ advisable
        Assert.Equal(RevolveAxisCorrectionKind.ScaleOutlier, advice.CorrectionKind);
        Assert.NotNull(advice.SuggestedReplacement);
        // X has the smallest envelope (15 mm); Y (160) and Z (160.7) dwarf
        // the profile. X must rank first.
        Assert.Equal(RevolveAxisMode.WcsX, advice.SuggestedReplacement!.Mode);
        Assert.Equal(RevolveAxisMode.WcsX, advice.RankedCandidates[0].Mode);
        Assert.True(advice.RankedCandidates[0].MaxRadiusMm < 16);
        // Explanation names the scale problem and the alternative.
        Assert.Contains("Z 轴", advice.Explanation);
        Assert.Contains("尺度异常", advice.Explanation);
        Assert.Contains("X 轴", advice.Explanation);
    }

    [Fact]
    public void XySketch_RequestedX_IsBest_NoReplacement()
    {
        var advice = RevolveAxisAdvisor.Assess(XyRectangle(), RevolveAxisMode.WcsX);
        Assert.True(advice.RequestedAxisFeasible);
        Assert.Equal(RevolveAxisCorrectionKind.None, advice.CorrectionKind);
        Assert.Null(advice.SuggestedReplacement);
        Assert.Equal(3, advice.RankedCandidates.Count); // all axes feasible
        Assert.Equal(RevolveAxisMode.WcsX, advice.RankedCandidates[0].Mode);
    }

    [Fact]
    public void AllAxes_AreFeasible_IncludingPlaneNormal()
    {
        // The removed restriction, pinned by test: the plane-normal axis
        // (Z on XY) is FEASIBLE — its earlier refusal was project-side.
        foreach (var mode in new[] { RevolveAxisMode.WcsX, RevolveAxisMode.WcsY, RevolveAxisMode.WcsZ })
        {
            var advice = RevolveAxisAdvisor.Assess(XyRectangle(), mode);
            Assert.True(advice.RequestedAxisFeasible);
        }
    }

    // ------------------------------------- generic plane/axis relations

    [Fact]
    public void XzSketch_AllAxesFeasible_NormalAxisLandsInAmbiguityBand()
    {
        // XZ plane, 20×10 rectangle centered at the origin. The Y axis (the
        // plane normal through the origin) is now FEASIBLE. Its revolve
        // radius (√(10²+5²)≈11.2) is 2.24× the best candidate (X: 5) —
        // between the ambiguity (2×) and outlier (3×) factors → the
        // requested axis is KEPT with the uncertainty stated. A flat
        // rectangle genuinely does not single out an axis: the normal-axis
        // revolve is a legitimate cone-ish solid of comparable scale.
        var facts = new RevolveAxisAdvisor.SketchFacts(
            SketchPlaneMode.WcsXZ, SketchProfileType.Rectangle, 20, 10, 0, 0, 0);
        var y = RevolveAxisAdvisor.Assess(facts, RevolveAxisMode.WcsY);
        Assert.True(y.RequestedAxisFeasible);
        Assert.Equal(RevolveAxisCorrectionKind.AmbiguousKeepRequested, y.CorrectionKind);
        Assert.Null(y.SuggestedReplacement);
        // X (the best candidate) is a clean None; Z (exactly 2× the best,
        // at the ambiguity boundary — 10 vs 5, both legitimate) is kept
        // with the uncertainty named rather than force-classed.
        Assert.Equal(RevolveAxisCorrectionKind.None,
            RevolveAxisAdvisor.Assess(facts, RevolveAxisMode.WcsX).CorrectionKind);
        Assert.Equal(RevolveAxisCorrectionKind.AmbiguousKeepRequested,
            RevolveAxisAdvisor.Assess(facts, RevolveAxisMode.WcsZ).CorrectionKind);
    }

    [Fact]
    public void YzSketch_AllAxesFeasible_OffCenterNormalIsOutlier()
    {
        // YZ plane, circle d30 at (u25, v0) — i.e. 25 mm along the sketch's
        // first named axis (Y world). The X axis (plane normal) revolve:
        // every profile point is ≥ sqrt(25²-15²)=20 from the X axis line,
        // max = sqrt(25²+15²)≈29.2; ratio 29.2/15≈1.95 vs the best
        // in-plane axis (Z: max 40-15=25... compute: center (0,25,0) in
        // world; Z axis max distance = sqrt(25²+15²)≈29.2 — same ratio).
        // Ratios comparable → ambiguous keep; NOTHING is infeasible.
        var facts = new RevolveAxisAdvisor.SketchFacts(
            SketchPlaneMode.WcsYZ, SketchProfileType.Circle, 0, 0, 30, 25, 0);
        Assert.True(RevolveAxisAdvisor.Assess(facts, RevolveAxisMode.WcsX).RequestedAxisFeasible);
        Assert.True(RevolveAxisAdvisor.Assess(facts, RevolveAxisMode.WcsY).RequestedAxisFeasible);
        Assert.True(RevolveAxisAdvisor.Assess(facts, RevolveAxisMode.WcsZ).RequestedAxisFeasible);
    }

    [Fact]
    public void SelectedPlaneSketch_IsNotAssessable_NoRewritePossible()
    {
        // The orientation of a user-selected plane lives only inside NX:
        // the advisor must refuse to judge (NotAssessable = feasible=true,
        // no suggestion) so the runtime check stays the only authority.
        var facts = new RevolveAxisAdvisor.SketchFacts(
            SketchPlaneMode.SelectedPlane, SketchProfileType.Rectangle, 20, 10, 0, 0, 0);
        var advice = RevolveAxisAdvisor.Assess(facts, RevolveAxisMode.WcsZ);
        Assert.True(advice.RequestedAxisFeasible); // advisory silence, not a verdict
        Assert.Null(advice.SuggestedReplacement);
        Assert.Empty(advice.RankedCandidates);
    }

    [Fact]
    public void NullSketchFacts_IsNotAssessable()
    {
        var advice = RevolveAxisAdvisor.Assess(null, RevolveAxisMode.WcsZ);
        Assert.True(advice.RequestedAxisFeasible);
        Assert.Null(advice.SuggestedReplacement);
    }

    // ------------------------------------- distance ranking semantics

    [Fact]
    public void DistanceRanking_PrefersCompactAxis_OnCircleProfile()
    {
        // Circle d30 centered at u=40, v=0 (on the X axis). Revolve radius
        // about X is 15 (the sphere case); about Y is 55 (40 + 15). X must
        // come first by the scale ranking.
        var advice = RevolveAxisAdvisor.Assess(XyCircle(), RevolveAxisMode.WcsZ);
        Assert.Equal(RevolveAxisMode.WcsX, advice.SuggestedReplacement!.Mode);
        Assert.Equal(15, advice.RankedCandidates[0].MaxRadiusMm, 6);
        Assert.Equal(55, advice.RankedCandidates[1].MaxRadiusMm, 6);
    }

    [Fact]
    public void DistanceRanking_TouchingProfile_ClampsAtZero()
    {
        // Rectangle from u=0..20, v=0..10 (center 10,5): the Y axis touches
        // its edge (u=0) — the profile-min-axis distance clamps at 0. All
        // distances stay non-negative; the requested Z (scale ratio 2.24 vs
        // best 1.0) lands in the ambiguity band (between 2× and 3×), so it
        // is KEPT with the uncertainty stated — no silent rewrite, no
        // infeasibility verdict.
        var facts = new RevolveAxisAdvisor.SketchFacts(
            SketchPlaneMode.WcsXY, SketchProfileType.Rectangle, 20, 10, 0, 10, 5);
        var advice = RevolveAxisAdvisor.Assess(facts, RevolveAxisMode.WcsZ);
        Assert.Equal(RevolveAxisCorrectionKind.AmbiguousKeepRequested, advice.CorrectionKind);
        Assert.Null(advice.SuggestedReplacement);
        Assert.All(advice.RankedCandidates, c => Assert.True(c.DistanceToProfileMm >= 0));
    }

    // ------------------------------------- live case 2: legal-but-outlier

    [Fact]
    public void XyRectangle_FarFromYAxis_YIsScaleOutlier_XSuggested()
    {
        // The second live case: the model picks Y (legal) for the 20×10
        // rectangle at (u150, v10). Revolving about Y gives an envelope
        // radius ~160 mm (a ~320 mm ring); about X only 15 mm. The Y ratio
        // (16) is > 3× the X ratio (1.5) → scale outlier, X suggested.
        var advice = RevolveAxisAdvisor.Assess(XyRectangle(), RevolveAxisMode.WcsY);

        Assert.True(advice.RequestedAxisFeasible); // Y is legal
        Assert.Equal(RevolveAxisCorrectionKind.ScaleOutlier, advice.CorrectionKind);
        Assert.NotNull(advice.SuggestedReplacement);
        Assert.Equal(RevolveAxisMode.WcsX, advice.SuggestedReplacement!.Mode);
        Assert.Contains("尺度异常", advice.Explanation);
    }

    [Fact]
    public void XyRectangle_NearXAxis_XIsNotOutlier()
    {
        // Requesting X directly: feasible AND of the profile's own scale —
        // no correction, no ambiguity notice.
        var advice = RevolveAxisAdvisor.Assess(XyRectangle(), RevolveAxisMode.WcsX);
        Assert.True(advice.RequestedAxisFeasible);
        Assert.Equal(RevolveAxisCorrectionKind.None, advice.CorrectionKind);
        Assert.Null(advice.SuggestedReplacement);
    }

    [Fact]
    public void CircleCenteredOnAxis_PassesAlongOtherAxis_KeptNotRewritten()
    {
        // Circle d30 at (40, 0): about X → sphere-ish body (ratio 1); about
        // Y → ring radius 55 (ratio 3.67). Y is > 3× X and X ≤ bound → Y is
        // a scale outlier and X is suggested (legal-but-badly-scaled axis).
        var advice = RevolveAxisAdvisor.Assess(XyCircle(), RevolveAxisMode.WcsY);
        Assert.Equal(RevolveAxisCorrectionKind.ScaleOutlier, advice.CorrectionKind);
        Assert.Equal(RevolveAxisMode.WcsX, advice.SuggestedReplacement!.Mode);
    }

    [Fact]
    public void ComparableScaleCandidates_KeepRequestedAndMarkAmbiguous()
    {
        // Profile centered between X and Y at the same distance: both axes
        // produce the same envelope scale — the intent does not single one
        // out. Requested Y (feasible) must be KEPT with Ambiguous kind.
        var facts = new RevolveAxisAdvisor.SketchFacts(
            SketchPlaneMode.WcsXY, SketchProfileType.Circle, 0, 0, 30, 40, 40);
        var advice = RevolveAxisAdvisor.Assess(facts, RevolveAxisMode.WcsY);
        Assert.True(advice.RequestedAxisFeasible);
        Assert.Equal(RevolveAxisCorrectionKind.AmbiguousKeepRequested, advice.CorrectionKind);
        Assert.Null(advice.SuggestedReplacement);
        Assert.Contains("未自动更换轴", advice.Explanation);
    }

    // ------------------------------------- off-plane axes (post-fix)

    [Fact]
    public void OffPlaneNormalAxis_IsFeasibleAndRanked_NeverInfeasible()
    {
        // The removed restriction, pinned explicitly: an axis normal to the
        // sketch plane (Y on an XZ sketch — the second live case's shape)
        // is FEASIBLE. Its scale is ranked like any other candidate; nothing
        // mentions "不满足旋转前置条件" or "不在草图平面内" as a refusal.
        var facts = new RevolveAxisAdvisor.SketchFacts(
            SketchPlaneMode.WcsXZ, SketchProfileType.Circle, 0, 0, 30, 20, 0);
        var advice = RevolveAxisAdvisor.Assess(facts, RevolveAxisMode.WcsY);
        Assert.True(advice.RequestedAxisFeasible);
        Assert.DoesNotContain("不满足旋转前置条件", advice.Explanation);
        // All three candidates carry the feasible flag.
        Assert.All(advice.RankedCandidates, c => Assert.True(c.Feasible));
        Assert.Equal(3, advice.RankedCandidates.Count);
    }

    // ------------------------------------- user-intent detection

    [Theory]
    [InlineData("绕X轴旋转360度", RevolveAxisAdvisor.AxisIntentKind.UserNamedAxis)]
    [InlineData("绕 Y 轴旋转", RevolveAxisAdvisor.AxisIntentKind.UserNamedAxis)]
    [InlineData("以Z轴为旋转轴", RevolveAxisAdvisor.AxisIntentKind.UserNamedAxis)]
    [InlineData("沿Y轴方向旋转轮廓", RevolveAxisAdvisor.AxisIntentKind.UserNamedAxis)]
    [InlineData("使用我选中的基准轴旋转", RevolveAxisAdvisor.AxisIntentKind.UserNamedSelectedAxis)]
    [InlineData("现有草图按我选定的轴旋转180°创建实体", RevolveAxisAdvisor.AxisIntentKind.UserNamedSelectedAxis)]
    [InlineData("按选择的直线旋转", RevolveAxisAdvisor.AxisIntentKind.UserNamedSelectedAxis)]
    [InlineData("axis_mode=\"wcs_z\"", RevolveAxisAdvisor.AxisIntentKind.UserNamedAxis)]
    [InlineData("绕草图平面内的轴旋转360度", RevolveAxisAdvisor.AxisIntentKind.Unspecified)]
    [InlineData("用现有草图绕轴旋转 360°", RevolveAxisAdvisor.AxisIntentKind.Unspecified)]
    [InlineData("选择合适的轴旋转", RevolveAxisAdvisor.AxisIntentKind.Unspecified)]
    [InlineData("旋转360度生成旋转体", RevolveAxisAdvisor.AxisIntentKind.Unspecified)]
    [InlineData("", RevolveAxisAdvisor.AxisIntentKind.Unspecified)]
    public void ClassifyAxisIntent_DistinguishesTheThreeIntents(
        string text, RevolveAxisAdvisor.AxisIntentKind expected)
    {
        Assert.Equal(expected, RevolveAxisAdvisor.ClassifyAxisIntent(text));
        // The legacy predicate stays consistent with the classification.
        Assert.Equal(expected != RevolveAxisAdvisor.AxisIntentKind.Unspecified,
            RevolveAxisAdvisor.IsAxisExplicitlySpecified(text));
    }

    // --------------------------------- live bug 2026-10-20: 按选定轴 variants

    [Theory]
    [InlineData("在XY平面绘制直径20并按选定轴旋转360°")]           // the live failure (no 的)
    [InlineData("按选定轴旋转")]
    [InlineData("按已选轴旋转")]
    [InlineData("按我选定的轴旋转")]
    [InlineData("按选中的轴旋转")]
    [InlineData("按选择的直线旋转")]
    [InlineData("使用我选中的直线作为旋转轴")]
    [InlineData("使用当前选择的基准轴旋转")]
    [InlineData("selected_axis")]
    public void ClassifyAxisIntent_SelectedAxisVariants_AllRecognized(string text)
    {
        // Requirement 1: every established Chinese phrasing for "use my
        // selected object as the rotation axis" classifies as
        // UserNamedSelectedAxis — the live failure phrasing 按选定轴 (no 的)
        // included. None may fall through to Unspecified (which lets the
        // planner silently rewrite to a WCS axis) or to UserNamedAxis.
        Assert.Equal(RevolveAxisAdvisor.AxisIntentKind.UserNamedSelectedAxis,
            RevolveAxisAdvisor.ClassifyAxisIntent(text));
    }

    [Theory]
    [InlineData("使用选定尺寸绘制草图然后绕轴旋转360度")]   // 选定 modifies 尺寸
    [InlineData("按选定方式旋转")]
    [InlineData("已选择毫米作为单位，绕轴旋转")]
    public void ClassifyAxisIntent_UnrelatedSelectionUses_StayUnspecified(string text)
    {
        // Requirement 1: 选定/选择 describing OTHER objects (尺寸/方式/单位)
        // must not be mistaken for a selected-axis decision — the object
        // word (轴/直线/线) must directly follow the selection word.
        Assert.Equal(RevolveAxisAdvisor.AxisIntentKind.Unspecified,
            RevolveAxisAdvisor.ClassifyAxisIntent(text));
    }

    [Fact]
    public void SelectedAxisMode_WithUnspecifiedIntent_ReturnsBestRankedCandidate()
    {
        // The live failure's advisor shape: the model defaulted to
        // selected_axis; the sketch facts ARE known. Assess must NOT throw
        // (SelectedAxis is not among the WCS candidates) and must answer
        // with the scale ranking — the controller's normalization uses it.
        var advice = RevolveAxisAdvisor.Assess(XyRectangle(), RevolveAxisMode.SelectedAxis);
        Assert.True(advice.RequestedAxisFeasible);
        Assert.Equal(RevolveAxisCorrectionKind.None, advice.CorrectionKind);
        Assert.Equal(3, advice.RankedCandidates.Count);
        Assert.Equal(RevolveAxisMode.WcsX, advice.RankedCandidates[0].Mode);
        Assert.Contains("未指定具体旋转轴", advice.Explanation);
    }

    [Fact]
    public void SelectedAxisMode_WithNullFacts_IsNotAssessable()
    {
        var advice = RevolveAxisAdvisor.Assess(null, RevolveAxisMode.SelectedAxis);
        Assert.True(advice.RequestedAxisFeasible);
        Assert.Empty(advice.RankedCandidates);
    }

    // ------------------------------------- requirement 五-5: the named case

    [Fact]
    public void XzCircle_AtX30_WhenAxisUnspecified_ProducesExplainableChoice()
    {
        // XZ plane, circle d10 centered at world (30,0,0) — the center sits
        // ON the X axis. About X: the circle sweeps a r=5 sphere-ish body
        // (envelope radius 5). About Y and Z: ring with outer radius 35
        // (center distance 30 + r 5). Ranking by max radius: X(5) < Y(35)
        // ≈ Z(35) — the X choice is decisive (a compact body of the
        // profile's own scale vs a 70mm ring). The explanation names the
        // scale reasoning — an auto-plan request with NO selected object
        // gets a concrete, justified answer.
        var facts = new RevolveAxisAdvisor.SketchFacts(
            SketchPlaneMode.WcsXZ, SketchProfileType.Circle, 0, 0, 10, 30, 0);
        var advice = RevolveAxisAdvisor.Assess(facts, RevolveAxisMode.SelectedAxis);

        Assert.Equal(3, advice.RankedCandidates.Count);
        Assert.Equal(RevolveAxisMode.WcsX, advice.RankedCandidates[0].Mode);
        Assert.Equal(5, advice.RankedCandidates[0].MaxRadiusMm, 3);
        Assert.Contains("未指定具体旋转轴", advice.Explanation);
        Assert.Contains("X 轴", advice.Explanation);
    }

    // --------------------------------- 三平面回归矩阵 (cross-plane matrix)

    /// <summary>
    /// The three-plane matrix core (requirement 四): identical local
    /// geometry (circle d10, center offset 35 along the plane's local U)
    /// on XY, XZ and YZ. The advisor must evaluate through each plane's
    /// REAL world frame — local U maps to world X (XY), X (XZ) and Y (YZ)
    /// respectively, so the compact axis DIFFERS per plane:
    ///   - XY:  center (35,0,0) on the X axis → X sweeps a r=5 compact
    ///     body; Y/Z give 40mm rings → X wins.
    ///   - XZ:  center (35,0,0) on the X axis → X wins (same as the named
    ///     case above but at U=35).
    ///   - YZ:  planeU=(0,1,0) → center (0,35,0) ON the Y axis → Y sweeps
    ///     the compact body; X/Z give 40mm rings → Y wins. Local U is the
    ///     world Y here — the explicit proof that local U is NOT assumed
    ///     to be world X.
    /// </summary>
    [Theory]
    [InlineData(SketchPlaneMode.WcsXY, RevolveAxisMode.WcsX)]
    [InlineData(SketchPlaneMode.WcsXZ, RevolveAxisMode.WcsX)]
    [InlineData(SketchPlaneMode.WcsYZ, RevolveAxisMode.WcsY)]
    public void ThreePlaneMatrix_CircleAtU35_CompactAxisWinsPerPlane(
        SketchPlaneMode planeMode, RevolveAxisMode expectedBest)
    {
        var facts = new RevolveAxisAdvisor.SketchFacts(
            planeMode, SketchProfileType.Circle, 0, 0, 10, 35, 0);
        foreach (var modelGuess in new[]
                 {
                     RevolveAxisMode.SelectedAxis, RevolveAxisMode.WcsX,
                     RevolveAxisMode.WcsY, RevolveAxisMode.WcsZ,
                 })
        {
            var advice = RevolveAxisAdvisor.Assess(facts, modelGuess);
            Assert.True(advice.RequestedAxisFeasible);
            Assert.Equal(3, advice.RankedCandidates.Count);
            Assert.Equal(expectedBest, advice.RankedCandidates[0].Mode);
            Assert.Equal(5, advice.RankedCandidates[0].MaxRadiusMm, 3);
            // Compact candidate is a decisive scale winner: for a WCS model
            // guess that differs, the advice proposes replacing it. A
            // SelectedAxis "guess" returns early (CorrectionKind.None, no
            // replacement) — its normalization to RankedCandidates[0] is
            // controller-level behavior, covered by the controller tests.
            if (modelGuess != expectedBest && modelGuess != RevolveAxisMode.SelectedAxis)
            {
                Assert.NotNull(advice.SuggestedReplacement);
                Assert.Equal(expectedBest, advice.SuggestedReplacement!.Mode);
                Assert.Equal(RevolveAxisCorrectionKind.ScaleOutlier, advice.CorrectionKind);
            }
            else
            {
                Assert.Null(advice.SuggestedReplacement);
            }
        }
    }

    [Fact]
    public void ThreePlaneMatrix_OffDiagonalCenter_ChangesTheWinner()
    {
        // Requirement 四.7 (different profile centers): on the XY plane with
        // the circle at (u0, v35) — center on the Y axis — the compact axis
        // flips to Y. The world-frame conversion drives the choice, not a
        // per-plane constant.
        var facts = new RevolveAxisAdvisor.SketchFacts(
            SketchPlaneMode.WcsXY, SketchProfileType.Circle, 0, 0, 10, 0, 35);
        var advice = RevolveAxisAdvisor.Assess(facts, RevolveAxisMode.SelectedAxis);
        Assert.Equal(RevolveAxisMode.WcsY, advice.RankedCandidates[0].Mode);
        Assert.Equal(5, advice.RankedCandidates[0].MaxRadiusMm, 3);
    }

    [Fact]
    public void ThreePlaneMatrix_RectangleFarFromAxis_ScaleOutlierPerPlane()
    {
        // Rectangle 20×10 at local U=150 on each plane: the compact axis is
        // the one the offset is measured along — X (XY/XZ planes), Y (YZ
        // plane). Every plane's model-guessed non-compact axis is a scale
        // outlier with a concrete replacement.
        foreach (var (planeMode, compact) in new[]
                 {
                     (SketchPlaneMode.WcsXY, RevolveAxisMode.WcsX),
                     (SketchPlaneMode.WcsXZ, RevolveAxisMode.WcsX),
                     (SketchPlaneMode.WcsYZ, RevolveAxisMode.WcsY),
                 })
        {
            var facts = new RevolveAxisAdvisor.SketchFacts(
                planeMode, SketchProfileType.Rectangle, 20, 10, 0, 150, 10);
            var advice = RevolveAxisAdvisor.Assess(facts, RevolveAxisMode.SelectedAxis);
            Assert.Equal(compact, advice.RankedCandidates[0].Mode);
        }
    }

    [Fact]
    public void ThreePlaneMatrix_Ambiguity_IsPlaneIndependent()
    {
        // Circle d30 centered EQUIDISTANT from X and Y on the XY plane
        // (u=40, v=40): X and Z envelopes are comparable → Ambiguous
        // (keep the requested). The ambiguity semantics survive the
        // world-frame conversion.
        var facts = new RevolveAxisAdvisor.SketchFacts(
            SketchPlaneMode.WcsXY, SketchProfileType.Circle, 0, 0, 30, 40, 40);
        var advice = RevolveAxisAdvisor.Assess(facts, RevolveAxisMode.WcsY);
        Assert.Equal(RevolveAxisCorrectionKind.AmbiguousKeepRequested, advice.CorrectionKind);
        Assert.Null(advice.SuggestedReplacement);
    }
}
