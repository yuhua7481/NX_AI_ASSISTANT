namespace NXAI.Assistant.Core;

/// <summary>
/// Exact world-frame envelope of a circular profile revolved 360° about an
/// arbitrary (possibly non-axis-aligned) axis line.
///
/// Root cause this fixes (live 2026-10-20 10:43): the previous
/// implementation sampled only the four circle points center ± r·e1 /
/// ± r·e2. For a tilted axis the true radial maximum lies at an arbitrary
/// angle on the circle (live: ~44.6° between the samples), underestimating
/// maxR by ~2.9mm for a d20 circle — the 360° postcondition then rejected
/// the correctly committed body.
///
/// The math here is closed-form where closed form exists and otherwise a
/// deterministic Lipschitz branch-and-bound with a stated error bound:
///   - axial extent: the projection of ANY circle point onto the axis is
///     t(θ) = w·u + r(α cosθ + β sinθ) with α = e1·u, β = e2·u, so the
///     exact axial span is [w·u − r·k, w·u + r·k] with k = √(α²+β²)
///     (= |n×u|, n the circle normal) — no sampling;
///   - radial maximum: dist(θ) = |perp(w + r v(θ))| where w = C−ap and
///     v(θ) = cosθ·e1 + sinθ·e2. |d dist/dθ| ≤ r (the parametrization
///     speed), so a branch-and-bound over θ whose segment upper bound is
///     "segment max sample + r·segment half-width" converges to the true
///     maximum within a stated ε; the refinement order (always split the
     ///     segment with the largest bound gap) makes the step count
///     deterministic.
/// </summary>
public static class RevolveEnvelopeMath
{
    /// <summary>Convergence target for the radial maximum (millimeters).
    /// The returned maxR is within MaxRadialError of the true value.</summary>
    public const double MaxRadialError = 0.0005;

    /// <summary>
    /// A circle: center C, radius r, orthonormal in-plane directions
    /// e1/e2 (so normal n = e1×e2).
    /// </summary>
    public readonly record struct CircleFacts(
        Vector Center, double Radius, Vector E1, Vector E2);

    /// <summary>
    /// An axis line: point A, unit direction U.
    /// </summary>
    public readonly record struct AxisLine(Vector Point, Vector UnitDirection);

    /// <summary>
    /// Exact envelope of the full 360° sweep of the circle about the axis.
    ///
    /// Per world axis i the sweep of a profile point p spans
    /// [u_i·t_p − s_i·r_p, u_i·t_p + s_i·r_p] (t_p = axial coordinate,
    /// r_p = distance to the axis, s_i = √(1−u_i²)). The extrema over the
    /// circle are the optima of f±(θ) = u_i·t(θ) ± s_i·r_p(θ) — 1-D
    /// Lipschitz functions (|f±'| ≤ |u_i|·r·k + s_i·r with k = |n×u|),
    /// solved by the same deterministic branch-and-bound as the radial
    /// maximum. A "cylinder bound" (global t-range × global maxR) is WRONG
    /// for tilted axes: axial coordinate and radius are correlated along
    /// the circle, and the cylinder extends to coordinates no swept point
    /// reaches (found by the XZ-circle-about-Y test: it returned X∈[-40,40]
    /// for a torus whose true X range is [20,40]).
    /// </summary>
    public static (double MinX, double MinY, double MinZ, double MaxX, double MaxY, double MaxZ)
        ComputeFullSweepEnvelope(CircleFacts circle, AxisLine axis)
    {
        var u = axis.UnitDirection;
        var w = circle.Center - axis.Point;
        var wPerp = w - new Vector(u.X * w.Dot(u), u.Y * w.Dot(u), u.Z * w.Dot(u));

        double alpha = circle.E1.Dot(u), beta = circle.E2.Dot(u);
        double k = Math.Sqrt(alpha * alpha + beta * beta); // |n×u|
        double sX = Math.Sqrt(Math.Max(0, 1 - u.X * u.X));
        double sY = Math.Sqrt(Math.Max(0, 1 - u.Y * u.Y));
        double sZ = Math.Sqrt(Math.Max(0, 1 - u.Z * u.Z));
        double[] s = { sX, sY, sZ };
        double[] uArr = { u.X, u.Y, u.Z };
        double[] apArr = { axis.Point.X, axis.Point.Y, axis.Point.Z };
        double r = circle.Radius;

        // Point on the circle at parameter θ (world, relative to the axis point).
        Vector Point(double theta) =>
            w + new Vector(
                circle.E1.X * r * Math.Cos(theta) + circle.E2.X * r * Math.Sin(theta),
                circle.E1.Y * r * Math.Cos(theta) + circle.E2.Y * r * Math.Sin(theta),
                circle.E1.Z * r * Math.Cos(theta) + circle.E2.Z * r * Math.Sin(theta));

        // Per-axis extremal functions and their Lipschitz constants.
        double Extremal(int i, double theta, int sign)
        {
            var p = Point(theta);
            double t = p.Dot(u);
            var perp = p - new Vector(u.X * t, u.Y * t, u.Z * t);
            double rp = perp.Length;
            return uArr[i] * t + sign * s[i] * rp;
        }
        // |f±'| ≤ |u_i|·r·k + s_i·r (|t'| ≤ r·k; |r_p'| ≤ r).
        double Lipschitz(int i) => Math.Abs(uArr[i]) * r * k + s[i] * r;

        double Minimize(int i)
        {
            var segs = new List<(double Lo, double Hi, double Value)>();
            const int initial = 8;
            double width = 2 * Math.PI / initial;
            for (int s0 = 0; s0 < initial; s0++)
            {
                double lo = s0 * width, hi = lo + width, mid = lo + width / 2;
                segs.Add((lo, hi, Math.Min(Extremal(i, lo, -1), Math.Min(Extremal(i, mid, -1), Extremal(i, hi, -1)))));
            }
            double best = segs.Min(s => s.Value);
            while (true)
            {
                double worstGap = 0; int worstIdx = -1;
                for (int j = 0; j < segs.Count; j++)
                {
                    var seg = segs[j];
                    double bound = seg.Value - Lipschitz(i) * (seg.Hi - seg.Lo) / 2;
                    double gap = best - bound;
                    if (gap > worstGap) { worstGap = gap; worstIdx = j; }
                }
                if (worstGap <= MaxRadialError || worstIdx < 0) break;
                var target = segs[worstIdx];
                double mid = (target.Lo + target.Hi) / 2;
                double vm = Extremal(i, mid, -1);
                segs[worstIdx] = (target.Lo, mid, Math.Min(target.Value, vm));
                segs.Add((mid, target.Hi, Math.Min(vm, Extremal(i, target.Hi, -1))));
                best = segs.Min(s => s.Value);
            }
            return best;
        }

        double Maximize(int i)
        {
            var segs = new List<(double Lo, double Hi, double Value)>();
            const int initial = 8;
            double width = 2 * Math.PI / initial;
            for (int s0 = 0; s0 < initial; s0++)
            {
                double lo = s0 * width, hi = lo + width, mid = lo + width / 2;
                segs.Add((lo, hi, Math.Max(Extremal(i, lo, +1), Math.Max(Extremal(i, mid, +1), Extremal(i, hi, +1)))));
            }
            double best = segs.Max(s => s.Value);
            while (true)
            {
                double worstGap = 0; int worstIdx = -1;
                for (int j = 0; j < segs.Count; j++)
                {
                    var seg = segs[j];
                    double bound = seg.Value + Lipschitz(i) * (seg.Hi - seg.Lo) / 2;
                    double gap = bound - best;
                    if (gap > worstGap) { worstGap = gap; worstIdx = j; }
                }
                if (worstGap <= MaxRadialError || worstIdx < 0) break;
                var target = segs[worstIdx];
                double mid = (target.Lo + target.Hi) / 2;
                double vm = Extremal(i, mid, +1);
                segs[worstIdx] = (target.Lo, mid, Math.Max(target.Value, vm));
                segs.Add((mid, target.Hi, Math.Max(vm, Extremal(i, target.Hi, +1))));
                best = segs.Max(s => s.Value);
            }
            return best;
        }

        // The extremal functions are relative to the axis point — add the
        // axis point's world coordinates back to get the absolute envelope.
        double minX = axis.Point.X + Minimize(0), maxX = axis.Point.X + Maximize(0);
        double minY = axis.Point.Y + Minimize(1), maxY = axis.Point.Y + Maximize(1);
        double minZ = axis.Point.Z + Minimize(2), maxZ = axis.Point.Z + Maximize(2);
        return (minX, minY, minZ, maxX, maxY, maxZ);
    }

    /// <summary>
    /// Maximum distance from the circle to the axis LINE, within
    /// <see cref="MaxRadialError"/>. Deterministic branch-and-bound:
    /// dist(θ) = |perp(w + r·v(θ))| is r-Lipschitz in θ (the parametrization
    /// speed |dv/dθ| = 1 and perp is 1-Lipschitz), so "segment sample max +
    /// r·half-width" is a valid per-segment upper bound; always splitting
    /// the segment with the largest bound surplus terminates with a
    /// certificate gap ≤ MaxRadialError.
    /// </summary>
    public static double MaxRadialDistance(CircleFacts circle, AxisLine axis)
    {
        if (circle.Radius < 0 || double.IsNaN(circle.Radius) || double.IsInfinity(circle.Radius))
            throw new ArgumentOutOfRangeException(nameof(circle), "圆半径必须是非负有限值。");
        double ulen = axis.UnitDirection.Length;
        if (ulen < 1e-12 || !double.IsFinite(ulen))
            throw new ArgumentException("旋转轴方向向量不能为零向量。", nameof(axis));
        var u = new Vector(
            axis.UnitDirection.X / ulen, axis.UnitDirection.Y / ulen, axis.UnitDirection.Z / ulen);

        var w = circle.Center - axis.Point;
        // Radial part of w (constant term of the distance function).
        var wPerp = w - new Vector(u.X * w.Dot(u), u.Y * w.Dot(u), u.Z * w.Dot(u));

        double Sample(double theta)
        {
            var p = wPerp
                + new Vector(circle.E1.X * circle.Radius * Math.Cos(theta),
                             circle.E1.Y * circle.Radius * Math.Cos(theta),
                             circle.E1.Z * circle.Radius * Math.Cos(theta))
                + new Vector(circle.E2.X * circle.Radius * Math.Sin(theta),
                             circle.E2.Y * circle.Radius * Math.Sin(theta),
                             circle.E2.Z * circle.Radius * Math.Sin(theta));
            return p.Length;
        }

        const int initialSegments = 8;
        double width = 2 * Math.PI / initialSegments;
        // Each segment: [lo, hi), its best sampled value, its Lipschitz bound.
        var segs = new List<(double Lo, double Hi, double Value)>(initialSegments);
        for (int i = 0; i < initialSegments; i++)
        {
            double lo = i * width;
            double hi = lo + width;
            double mid = lo + width / 2;
            segs.Add((lo, hi, Math.Max(Sample(lo), Math.Max(Sample(mid), Sample(hi)))));
        }

        double best = segs.Max(s => s.Value);
        // Certificate: the largest (r·half-width) among segments whose bound
        // could still exceed `best`. Terminate when every segment's upper
        // bound − best ≤ MaxRadialError.
        while (true)
        {
            double worstGap = 0;
            int worstIdx = -1;
            for (int i = 0; i < segs.Count; i++)
            {
                var seg = segs[i];
                double bound = seg.Value + circle.Radius * (seg.Hi - seg.Lo) / 2;
                double gap = bound - best;
                if (gap > worstGap)
                {
                    worstGap = gap;
                    worstIdx = i;
                }
            }
            if (worstGap <= MaxRadialError || worstIdx < 0)
                break;
            var target = segs[worstIdx];
            double mid = (target.Lo + target.Hi) / 2;
            double vm = Sample(mid);
            segs[worstIdx] = (target.Lo, mid, Math.Max(target.Value, vm));
            segs.Add((mid, target.Hi, Math.Max(vm, Sample(target.Hi))));
            best = segs.Max(s => s.Value);
        }
        return best;
    }
}

// Vector (minimal 3-vector for Core math) is defined in RevolveAxisAdvisor.cs
// and reused here — a single definition for the whole assembly.
