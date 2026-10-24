using System.Text.RegularExpressions;

namespace NXAI.Assistant.Core;

/// <summary>
/// One ranked axis candidate for a revolve: the axis mode, whether the axis
/// truly satisfies the revolve tool's geometric preconditions for THIS sketch
/// (axis lies in the sketch plane), the profile's closest distance to the
/// axis line, the expected revolve scale about that axis (max radius over
/// the whole profile), and that scale relative to the profile's own size —
/// the outlier signal: a candidate whose revolve dwarfs the profile by a
/// large factor is geometrically legal but produces a thin ring instead of
/// a body of the sketch's own scale.
/// </summary>
public sealed record RevolveAxisCandidate(
    RevolveAxisMode Mode,
    bool Feasible,
    double DistanceToProfileMm,
    string Reason,
    double MaxRadiusMm = 0,
    double ExpectedScaleRatio = 0);

/// <summary>Why the advisor proposes a replacement (None = keep requested).</summary>
public enum RevolveAxisCorrectionKind
{
    /// <summary>The requested axis satisfies preconditions and is scale-reasonable.</summary>
    None,

    /// <summary>The requested axis cannot lie in the sketch plane — firing it
    /// is a guaranteed preconditions_changed.</summary>
    InfeasibleAxis,

    /// <summary>The requested axis is legal but would produce a revolve far
    /// larger than the profile itself (e.g. a 20×10 profile 150 mm from the
    /// axis becoming a ~310 mm ring); a candidate of the profile's own scale
    /// exists and is unambiguously better.</summary>
    ScaleOutlier,

    /// <summary>Feasible candidates are comparable — the design intent does
    /// not single one out. The requested (feasible) axis is kept and the
    /// user may want to specify the axis explicitly.</summary>
    AmbiguousKeepRequested,
}

/// <summary>
/// The advisor's decision for one create_revolve request against one known
/// sketch. Imperative when the requested axis is infeasible or an outlier;
/// advisory otherwise.
/// <para>RankedCandidates: feasible candidates only, best first.</para>
/// <para>SuggestedReplacement: non-null only when CorrectionKind is
/// InfeasibleAxis or ScaleOutlier — the candidate the advisor would use.</para>
/// <para>Explanation: human-readable (Chinese), safe for chat log and for
/// the model-facing tool result.</para>
/// </summary>
public sealed record RevolveAxisAdvice(
    bool RequestedAxisFeasible,
    IReadOnlyList<RevolveAxisCandidate> RankedCandidates,
    RevolveAxisCandidate? SuggestedReplacement,
    string Explanation,
    RevolveAxisCorrectionKind CorrectionKind = RevolveAxisCorrectionKind.None)
{
    public static readonly RevolveAxisAdvice NotAssessable = new(
        true, Array.Empty<RevolveAxisCandidate>(), null,
        "草图信息不足，无法预先评估旋转轴可行性。");
}

/// <summary>
/// Pre-execution revolve-axis feasibility and ranking.
///
/// Purpose: the create_revolve tool requires the axis to lie IN the sketch
/// plane (verified at execution in NxToolExecutor). For a WCS-plane sketch
/// the plane orientation is fully determined by plane_mode (XY: u=X,v=Y,n=Z;
/// XZ: u=X,v=Z,n=-Y; YZ: u=Y,v=Z,n=X), so axis feasibility is decidable
/// WITHOUT calling NX: an absolute coordinate axis is feasible iff it is
/// parallel to the plane (the plane passes through the absolute origin where
/// all WCS axes live). For this XY case the Z axis is NEVER feasible — the
/// exact live failure this class prevents (model picked wcs_z on an XY
/// rectangle; preconditions_changed; user had to correct by hand).
///
/// Deterministic, no NX calls, no hidden mutation: the advisor only RANKS
/// and EXPLAINS. Replacing the model's axis is a separate, logged decision
/// made by the caller under an explicit mode policy.
/// </summary>
public static partial class RevolveAxisAdvisor
{
    /// <summary>
    /// Selected-object phrase pattern: a selection word (选中/选定/已选/选择),
    /// an optional 的, an optional 基准 (the WHOLE word optional — 基准?
    /// would wrongly make 基 mandatory), then the object word (轴/直线/线).
    /// Matches 按选定轴 / 按我选定的轴 / 选中的基准轴 / 选择的直线 / 已选轴 —
    /// and rejects 选定尺寸 (the object word must directly follow within the
    /// tight window).
    /// </summary>
    [GeneratedRegex(@"(选中|选定|已选|选择)的?(基准)?(轴|直线|线)")]
    private static partial Regex SelectedObjectPattern();

    /// <summary>
    /// Feasible candidates whose expected scale is within this factor of the
    /// best are considered comparable — no single geometric winner.
    /// </summary>
    public const double AmbiguityRatioFactor = 2.0;

    /// <summary>
    /// A feasible candidate whose expected revolve scale is this factor or
    /// more beyond the best candidate — while the best stays within the
    /// profile's own scale — is a geometric outlier the advisor may replace
    /// (only when the axis was not user-specified and the mode allows).
    /// </summary>
    public const double OutlierRatioFactor = 3.0;

    /// <summary>
    /// The "profile's own scale" bound on the best candidate's scale ratio:
    /// a replacement is only proposed when the alternative actually produces
    /// a body of the sketch's own order of magnitude (ratio ≤ this bound).
    /// </summary>
    public const double ProfileOwnScaleBound = 3.0;

    /// <summary>
    /// Assessed sketch facts. For WCS-plane sketches everything is known
    /// from the create_sketch parameters; for user-selected planes the
    /// orientation is only known inside NX, so the advisor reports
    /// NotAssessable and the runtime check stays the authority.
    /// </summary>
    public sealed record SketchFacts(
        SketchPlaneMode PlaneMode,
        SketchProfileType ProfileType,
        double RectangleWidth,
        double RectangleHeight,
        double CircleDiameter,
        double CenterU,
        double CenterV);

    /// <summary>
    /// WCS-plane frames (identical to the executor's ResolveWcsFrame — kept
    /// in Core so the advice and the executor cannot drift apart). Returns
    /// false for SelectedPlane (orientation lives only in NX).
    /// </summary>
    public static bool TryGetWcsPlaneFrame(
        SketchPlaneMode mode,
        out Vector u, out Vector v, out Vector normal)
    {
        switch (mode)
        {
            case SketchPlaneMode.WcsXY:
                u = new Vector(1, 0, 0); v = new Vector(0, 1, 0); normal = new Vector(0, 0, 1);
                return true;
            case SketchPlaneMode.WcsXZ:
                u = new Vector(1, 0, 0); v = new Vector(0, 0, 1); normal = new Vector(0, -1, 0);
                return true;
            case SketchPlaneMode.WcsYZ:
                u = new Vector(0, 1, 0); v = new Vector(0, 0, 1); normal = new Vector(1, 0, 0);
                return true;
            default:
                u = v = normal = default;
                return false;
        }
    }


    /// <summary>
    /// Profile extent facts used for the distance ranking: the profile
    /// center (in sketch-local u/v) and its half-extents toward u/v.
    /// </summary>
    private static void ProfileCenterAndReach(
        SketchFacts s, out Vector centerUv, out double halfU, out double halfV)
    {
        centerUv = new Vector(s.CenterU, s.CenterV, 0);
        if (s.ProfileType == SketchProfileType.Rectangle)
        {
            halfU = s.RectangleWidth / 2.0;
            halfV = s.RectangleHeight / 2.0;
        }
        else
        {
            halfU = halfV = s.CircleDiameter / 2.0;
        }
    }

    /// <summary>
    /// Closest distance from the profile region to an axis line through the
    /// absolute origin along <paramref name="axisDir"/> (a unit WORLD
    /// vector — not necessarily in the sketch plane; the coplanarity
    /// restriction was removed after live NX2512 verification).
    ///
    /// World profile points: p(tu,tv) = u·(cu + tu·halfU) + v·(cv + tv·halfV)
    /// for tu,tv ∈ {−1,+1} (rectangle corners; for a circle the same box
    /// bound is the correct circumscribed support). The distance from a
    /// point p to the axis line {t·d} is |p − (p·d)d|. The region-min is
    /// computed over the support: min over the box of the distance function
    /// (a convex function of the corner parameterization? No — the distance
    /// to a LINE is convex, so its minimum over the box is attained
    /// anywhere in the interior if the axis passes through, else on the
    /// boundary; enumerating corners is NOT sufficient for a true minimum
    /// of a convex function... but the distance to a line through the
    /// ORIGIN restricted to the rectangle's span is piecewise linear in
    /// the coordinates along the box faces, and the true min over a box of
    /// |f| where f is linear is attained at a corner or at the zero set —
    /// clamped at 0 covers the zero-set case, so corner enumeration plus
    /// the 0 clamp is exact here).
    /// </summary>
    public static double ProfileAxisDistanceMm(SketchFacts s, Vector axisDir, Vector u, Vector v)
    {
        ProfileCenterAndReach(s, out var center, out double halfU, out double halfV);
        double min = double.MaxValue;
        foreach (var tu in new[] { -1.0, 1.0 })
        {
            foreach (var tv in new[] { -1.0, 1.0 })
            {
                var p = (center.X + tu * halfU) * u + (center.Y + tv * halfV) * v;
                var along = p.Dot(axisDir) * axisDir;
                var perp = p - along;
                min = Math.Min(min, perp.Length);
            }
        }
        // The region may CONTAIN a point of the axis (distance 0) even when
        // all four corners are off the axis — the clamp covers that case.
        return Math.Max(0, min == double.MaxValue ? 0 : min);
    }

    /// <summary>
    /// Expected revolve scale about an axis through the absolute origin
    /// along <paramref name="axisDir"/> (unit, WORLD): the maximum distance
    /// from any profile point to the axis = the envelope radius of the
    /// full 360° solid. For a circle this uses the true extreme
    /// (center-distance + radius) rather than the corner bound.
    /// </summary>
    public static double MaxProfileRadiusAboutAxisMm(
        SketchFacts s, Vector axisDir, Vector u, Vector v)
    {
        ProfileCenterAndReach(s, out var center, out double halfU, out double halfV);
        var centerWorld = center.X * u + center.Y * v;
        double centerDistance = (centerWorld - centerWorld.Dot(axisDir) * axisDir).Length;
        if (s.ProfileType == SketchProfileType.Circle)
        {
            return centerDistance + halfU;
        }
        // Rectangle: the farthest point of the box from the line — the
        // distance function is convex, its maximum over the box is attained
        // at a corner.
        double best = 0;
        foreach (var tu in new[] { -1.0, 1.0 })
        {
            foreach (var tv in new[] { -1.0, 1.0 })
            {
                var p = (center.X + tu * halfU) * u + (center.Y + tv * halfV) * v;
                var along = p.Dot(axisDir) * axisDir;
                var perp = p - along;
                best = Math.Max(best, perp.Length);
            }
        }
        return best;
    }

    /// <summary>
    /// Full assessment. requested is the axis the model (or user) asked for;
    /// knownSketch null (unknown/selected-plane sketch) yields NotAssessable.
    /// Candidates are ALL three WCS axes ranked best-first among feasible
    /// ones; the requested axis keeps its true feasibility verdict.
    /// </summary>
    public static RevolveAxisAdvice Assess(
        SketchFacts? knownSketch, RevolveAxisMode requested)
    {
        if (knownSketch == null)
            return RevolveAxisAdvice.NotAssessable;
        if (!TryGetWcsPlaneFrame(knownSketch.PlaneMode, out var planeU, out var planeV, out var normal))
            return RevolveAxisAdvice.NotAssessable; // selected_plane: NX decides

        // Absolute coordinate axes through the origin: the plane contains
        // the origin, so an axis is in-plane iff it is perpendicular to the
        // plane normal.
        var axes = new (RevolveAxisMode Mode, Vector Dir, string Name)[]
        {
            (RevolveAxisMode.WcsX, new Vector(1, 0, 0), "绝对坐标 X 轴"),
            (RevolveAxisMode.WcsY, new Vector(0, 1, 0), "绝对坐标 Y 轴"),
            (RevolveAxisMode.WcsZ, new Vector(0, 0, 1), "绝对坐标 Z 轴"),
        };

        var candidates = new List<RevolveAxisCandidate>();
        double intrinsicScale = IntrinsicScaleMm(knownSketch);
        foreach (var (mode, dir, name) in axes)
        {
            // All three absolute axes through the origin are geometrically
            // feasible revolve axes: live NX2512 verification (2026-10-19)
            // proved axes need NOT lie in the sketch plane — the earlier
            // coplanarity rule was a project-side restriction, not an NX
            // one. Ranking is purely by the EXPECTED REVOLVE SCALE (the
            // envelope radius about that axis) relative to the profile's
            // own size: a candidate that would dwarf the profile is legal
            // but flagged as a scale outlier, never as "infeasible".
            double dist = ProfileAxisDistanceMm(knownSketch, dir, planeU, planeV);
            double maxRadius = MaxProfileRadiusAboutAxisMm(knownSketch, dir, planeU, planeV);
            double ratio = intrinsicScale > 1e-9 ? maxRadius / intrinsicScale : 0;
            string relation = normal.Dot(dir) > 1e-9
                ? "（轴垂直于草图平面）"
                : "（轴位于草图平面内）";
            candidates.Add(new RevolveAxisCandidate(mode, true, dist,
                $"{name}{relation}，轮廓到轴最近 {dist:F1} 毫米，旋转体外接半径约 {maxRadius:F1} 毫米。",
                maxRadius, ratio));
        }

        var ranked = candidates
            .Where(c => c.Feasible)
            .OrderBy(c => c.MaxRadiusMm)
            .ThenBy(c => c.DistanceToProfileMm)
            .ToList();
        // requested may be SelectedAxis (the model's default when the user
        // names no axis) — it is not among the WCS candidates; treat it as
        // "no usable requested candidate" and let the ranking decide.
        var requestedCandidate = candidates.FirstOrDefault(c => c.Mode == requested);
        var all = candidates.ToList();
        if (requestedCandidate == null)
        {
            // Model-default selected_axis with a known sketch: the advisor's
            // answer is simply the best-ranked candidate.
            if (ranked.Count == 0)
                return RevolveAxisAdvice.NotAssessable;
            return new RevolveAxisAdvice(true, ranked, null,
                $"请求未指定具体旋转轴；按预期旋转尺度排序的候选：{string.Join("；", ranked.Select(c => Describe(c.Mode) + $"（外接半径约 {c.MaxRadiusMm:F1} 毫米）"))}。建议采用 {Describe(ranked[0].Mode)}。",
                RevolveAxisCorrectionKind.None);
        }

        // All candidates are now feasible (the coplanarity gate is gone —
        // proven wrong against live NX2512); the requested axis is judged
        // purely on expected scale. InfeasibleAxis remains as a correction
        // kind for the selected-axis resolution path (object-level failures
        // handled by the executor), not for WCS-axis plane relations.
        if (ranked.Count == 0)
        {
            return new RevolveAxisAdvice(true, ranked, null,
                $"无可用候选轴信息，保留请求的{Describe(requested)}。");
        }

        // Judge the requested axis's expected scale against the best-scaled
        // candidate. A candidate whose revolve dwarfs the profile by
        // OutlierRatioFactor or more — while another candidate stays within
        // the profile's own scale band — is legal but a geometric outlier
        // (the live case: a 20×10 rectangle 150 mm from the Y axis producing
        // a ~320 mm ring when a ~30 mm body was the evident intent).
        // Candidates within AmbiguityRatioFactor of the best are comparable:
        // the intent does not single one out, so the requested axis is kept
        // and the uncertainty is stated.
        double bestRatio = ranked[0].ExpectedScaleRatio;
        double requestedRatio = requestedCandidate.ExpectedScaleRatio;
        if (requestedRatio <= bestRatio * AmbiguityRatioFactor)
        {
            // Comparable to the best. When the requested axis IS the best
            // (or ties it), the choice is clean; when other candidates come
            // close, several revolve outcomes are all reasonable — keep the
            // requested axis and say the choice was not forced by geometry.
            bool requestedIsBest =
                requestedCandidate.Mode == ranked[0].Mode ||
                (ranked.Count > 1 &&
                 ranked[1].ExpectedScaleRatio > requestedRatio * AmbiguityRatioFactor);
            if (requestedIsBest)
            {
                return new RevolveAxisAdvice(true, ranked, null,
                    $"请求的{Describe(requested)}满足旋转前置条件，预期旋转尺度与最优候选相当" +
                    $"（外接半径约 {requestedCandidate.MaxRadiusMm:F1} 毫米）。");
            }
            return new RevolveAxisAdvice(true, ranked, null,
                $"请求的{Describe(requested)}满足旋转前置条件，但 {Describe(ranked[0].Mode)} 等候选的预期旋转尺度" +
                $"（外接半径约 {ranked[0].MaxRadiusMm:F1} 毫米）与其相当，多个可行轴的旋转结果均合理，" +
                "未自动更换轴；如需特定几何结果请在请求中明确指定旋转轴。",
                RevolveAxisCorrectionKind.AmbiguousKeepRequested);
        }
        if (requestedRatio >= bestRatio * OutlierRatioFactor &&
            bestRatio <= ProfileOwnScaleBound)
        {
            var best = ranked[0];
            string detail = $"请求的{Describe(requested)}合法但尺度异常：绕它旋转的外接半径约 " +
                $"{requestedCandidate.MaxRadiusMm:F1} 毫米（轮廓自身尺度的 {requestedRatio:F1} 倍），" +
                $"而 {Describe(best.Mode)} 的外接半径约 {best.MaxRadiusMm:F1} 毫米（{bestRatio:F1} 倍），" +
                $"与轮廓尺寸匹配。建议改用 {Describe(best.Mode)}。";
            return new RevolveAxisAdvice(true, ranked, best, detail,
                RevolveAxisCorrectionKind.ScaleOutlier);
        }
        // Between the factors: scale differs but not decisively — state the
        // uncertainty, keep the requested (feasible) axis.
        return new RevolveAxisAdvice(true, ranked, null,
                $"请求的{Describe(requested)}满足旋转前置条件，但预期旋转尺度（外接半径约 " +
                $"{requestedCandidate.MaxRadiusMm:F1} 毫米）明显大于最优候选" +
                $"（{Describe(ranked[0].Mode)}，约 {ranked[0].MaxRadiusMm:F1} 毫米）。" +
                "多个可行轴的旋转结果均合理或差异不决定性，未自动更换轴；" +
                "如需特定几何结果请在请求中明确指定旋转轴。",
            RevolveAxisCorrectionKind.AmbiguousKeepRequested);
    }

    /// <summary>
    /// The profile's intrinsic scale: the largest half-extent of the profile
    /// itself (rectangle half-dimensions, circle radius). The yardstick a
    /// "natural" revolve is measured against — a candidate's expected revolve
    /// radius should be of this order, not orders larger.
    /// </summary>
    private static double IntrinsicScaleMm(SketchFacts s) =>
        s.ProfileType == SketchProfileType.Rectangle
            ? Math.Max(s.RectangleWidth, s.RectangleHeight) / 2.0
            : s.CircleDiameter / 2.0;

    /// <summary>
    /// Expected revolve scale about an in-plane axis through the plane
    /// origin: the maximum distance from any profile point to the axis
    /// (= the envelope radius of the full 360° solid). Computed from the
    /// profile's support in the plane: for the rectangle the worst corner
    /// distance; for the circle the center-to-axis distance plus radius.
    /// </summary>
    private static double MaxProfileRadiusAboutAxisMm(SketchFacts s, Vector axisDirInPlane)
    {
        ProfileCenterAndReach(s, out var center, out double halfU, out double halfV);
        if (s.ProfileType == SketchProfileType.Circle)
        {
            // Nearest the axis line gets to the center, plus the radius.
            double du = axisDirInPlane.Y, dv = -axisDirInPlane.X;
            double centerDistance = Math.Abs(center.X * du + center.Y * dv);
            return centerDistance + halfU;
        }
        // Rectangle: the farthest corner of the rectangle from the axis
        // line. The rectangle spans [cu−hu, cu+hu]×[cv−hv, cv+hv] in the
        // sketch plane; distance of a point (u,v) to the line through the
        // origin along d is |u·d_y − v·d_x|. The maximum over the box is
        // attained at a corner.
        double du2 = axisDirInPlane.Y, dv2 = -axisDirInPlane.X;
        double best = 0;
        foreach (var su in new[] { -1.0, 1.0 })
        {
            foreach (var sv in new[] { -1.0, 1.0 })
            {
                double u = center.X + su * halfU;
                double v = center.Y + sv * halfV;
                double d = Math.Abs(u * du2 + v * dv2);
                if (d > best)
                    best = d;
            }
        }
        return best;
    }

    /// <summary>
    /// True when the user's request text explicitly names a rotation axis
    /// (X/Y/Z or selected-axis wording). A deterministic keyword check over
    /// the CURRENT user turn — the input to the caller's mode policy, never
    /// a prompt to the model. Ambiguous phrasings ("绕草图平面内的轴",
    /// "合适的轴") return false: the axis counts as model-inferred.
    /// </summary>
    /// <summary>
    /// Deterministic three-way axis-intent classification of the user's
    /// CURRENT request text (requirement 三). The MODEL's axis_mode is NOT
    /// the user's intent: a model fills axis_mode=selected_axis as its
    /// default when the request names no axis — treating that as a user
    /// decision blocked auto-selection behind "no usable selected object"
    /// (the live failure). Classification:
    ///   - UserNamedAxis: the text names an absolute axis (绕Z轴/X轴旋转/以X轴为…) —
    ///     use it verbatim, never rewritten by scale optimization;
    ///   - UserNamedSelectedAxis: the text asks for the user's selected
    ///     object (按我选定的轴/选中线/selected_axis) — use selected_axis and
    ///     diagnose if the selection is unusable, never auto-switch to WCS;
    ///   - Unspecified: no axis named at all ("绕轴旋转360°") — route to
    ///     candidate evaluation; the model's selected_axis default does NOT
    ///     change this classification.
    /// </summary>
    public enum AxisIntentKind
    {
        Unspecified,
        UserNamedAxis,
        UserNamedSelectedAxis,
    }

    /// <summary>The deterministic intent classification.</summary>
    public static AxisIntentKind ClassifyAxisIntent(string? userRequestText)
    {
        if (string.IsNullOrWhiteSpace(userRequestText))
            return AxisIntentKind.Unspecified;
        string text = userRequestText;

        // (2) Selected-object wording — checked FIRST: "按我选定的轴旋转"
        // names an object, not a coordinate axis, even though it contains
        // the word 轴. Live gap (fixed 2026-10-20): "按选定轴旋转" (no 的)
        // fell through to Unspecified and the planner silently rewrote the
        // axis to a WCS candidate. One exact regex covers all established
        // Chinese variants: a selection word (选中/选定/已选/选择), an
        // optional 的, an optional 基准 (datum), then the object word
        // (轴/直线/线). The tight adjacency prevents unrelated uses of
        // 选定 (e.g. "选定尺寸…绕轴旋转") from matching, and no model call
        // or NXOpen dependency is introduced.
        if (text.Contains("selected_axis"))
            return AxisIntentKind.UserNamedSelectedAxis;
        if (SelectedObjectPattern().IsMatch(text))
            return AxisIntentKind.UserNamedSelectedAxis;

        // (1) Named absolute axes.
        if (ContainsNamedAbsoluteAxis(text))
            return AxisIntentKind.UserNamedAxis;

        return AxisIntentKind.Unspecified;
    }

    /// <summary>
    /// Backward-compatible predicate: true for either explicit-user form.
    /// The controller additionally guards selected_axis mode by intent —
    /// a model-default selected_axis with Unspecified intent is NOT
    /// "user-specified".
    /// </summary>
    public static bool IsAxisExplicitlySpecified(string? userRequestText) =>
        ClassifyAxisIntent(userRequestText) != AxisIntentKind.Unspecified;

    /// <summary>True when the text names an absolute coordinate axis.</summary>
    private static bool ContainsNamedAbsoluteAxis(string text)
    {
        foreach (var keyword in new[] { "X", "Y", "Z", "x", "y", "z" })
        {
            string upper = keyword.ToUpperInvariant();
            foreach (var pattern in new Func<string, bool>[]
            {
                t => t.Contains($"绕{keyword}轴") || t.Contains($"绕 {keyword} 轴") ||
                     t.Contains($"绕{keyword} 轴") || t.Contains($"绕 {keyword}轴"),
                t => t.Contains($"{upper}轴旋转") || t.Contains($"{upper} 轴旋转") ||
                     t.Contains($"{upper}轴做") || t.Contains($"{upper}轴为") ||
                     t.Contains($"{upper}轴作为") || t.Contains($"以{upper}轴") ||
                     t.Contains($"以 {upper} 轴") || t.Contains($"{upper}轴方向"),
                t => t.Contains($"沿{upper}轴") || t.Contains($"axis_mode=\"wcs_{lower(keyword)}\""),
            })
            {
                if (pattern(text))
                    return true;
            }
        }
        return false;
        static string lower(string s) => s.ToLowerInvariant();
    }

    private static string Describe(RevolveAxisMode mode) => mode switch
    {
        RevolveAxisMode.WcsX => "绝对坐标 X 轴",
        RevolveAxisMode.WcsY => "绝对坐标 Y 轴",
        RevolveAxisMode.WcsZ => "绝对坐标 Z 轴",
        RevolveAxisMode.SelectedAxis => "用户选中的直线/基准轴",
        _ => mode.ToString(),
    };
}

/// <summary>Minimal 3-vector for Core modeling math (the NXOpen
/// Vector3d type is not referenceable from Core). Shared by the axis
/// advisor and the revolve envelope math.</summary>
public readonly record struct Vector(double X, double Y, double Z)
{
    public double Dot(Vector o) => X * o.X + Y * o.Y + Z * o.Z;
    public double Length => Math.Sqrt(X * X + Y * Y + Z * Z);
    public static Vector operator +(Vector a, Vector b) => new(a.X + b.X, a.Y + b.Y, a.Z + b.Z);
    public static Vector operator -(Vector a, Vector b) => new(a.X - b.X, a.Y - b.Y, a.Z - b.Z);
    public static Vector operator *(double s, Vector a) => new(s * a.X, s * a.Y, s * a.Z);
}
