using System.Text.Json;
using NXAI.Assistant.Core;
using Xunit;

namespace NXAI.Assistant.Tests;

/// <summary>
/// RevolveEnvelopeMath — exact 360° envelope of a circular profile about an
/// arbitrary axis. Root cause fixed here (live 2026-10-20 10:43): 4-point
/// sampling (center ± r·e1/±r·e2) underestimated maxR by ~2.9mm for a d20
/// circle revolved about a tilted selected line (~44.6° between the true
/// extreme and the samples), wrongly rolling back the committed body.
///
/// Reference solutions are INDEPENDENT of the code under test:
///   - axial extent: closed form ±r·|n×u| — recomputed inline per case;
///   - radial maximum: dense 100k-point scan (θ step ≈ 6.3e-5 rad →
///     Lipschitz error ≤ r·step/2 ≈ 0.00016 mm, two orders below the
///     tolerance) computed with straightforward trigonometry, not by
///     calling the branch-and-bound.
/// </summary>
public class RevolveEnvelopeMathTests
{
    private static Vector Unit(double x, double y, double z)
    {
        var l = Math.Sqrt(x * x + y * y + z * z);
        return new Vector(x / l, y / l, z / l);
    }

    /// <summary>
    /// Independent reference: dense scan of dist(θ) over the full circle
    /// (100,000 samples) and the closed-form axial extent. Returns the
    /// reference envelope computed WITHOUT RevolveEnvelopeMath.
    /// </summary>
    private static (double MaxR, double MinX, double MinY, double MinZ, double MaxX, double MaxY, double MaxZ)
        ReferenceSolution(
        Vector c, double r,
        Vector e1, Vector e2,
        Vector ap, Vector u)
    {
        var w = c - ap;
        double maxR = 0;
        var lo = new double[] { double.MaxValue, double.MaxValue, double.MaxValue };
        var hi = new double[] { double.MinValue, double.MinValue, double.MinValue };
        double[] uArr = { u.X, u.Y, u.Z };
        double[] s = {
            Math.Sqrt(Math.Max(0, 1 - u.X * u.X)),
            Math.Sqrt(Math.Max(0, 1 - u.Y * u.Y)),
            Math.Sqrt(Math.Max(0, 1 - u.Z * u.Z)),
        };
        const int n = 100_000;
        double[] apArr = { ap.X, ap.Y, ap.Z };
        for (int i = 0; i < n; i++)
        {
            double theta = 2 * Math.PI * i / n;
            var p = w + new Vector(e1.X * r * Math.Cos(theta), e1.Y * r * Math.Cos(theta), e1.Z * r * Math.Cos(theta))
                      + new Vector(e2.X * r * Math.Sin(theta), e2.Y * r * Math.Sin(theta), e2.Z * r * Math.Sin(theta));
            var pu = p.Dot(u);
            var perp = p - new Vector(u.X * pu, u.Y * pu, u.Z * pu);
            maxR = Math.Max(maxR, perp.Length);
            // Each profile point sweeps a full circle of radius |perp| in the
            // plane ⊥ u at height pu: per world axis it spans
            // ap_i + u_i·pu ± s_i·|perp|. Accumulate the swept AABB directly
            // (independent of the algorithm under test).
            for (int a = 0; a < 3; a++)
            {
                double spanPoint = apArr[a] + uArr[a] * pu;
                lo[a] = Math.Min(lo[a], spanPoint - s[a] * perp.Length);
                hi[a] = Math.Max(hi[a], spanPoint + s[a] * perp.Length);
            }
        }
        return (maxR, lo[0], lo[1], lo[2], hi[0], hi[1], hi[2]);
    }

    private static RevolveEnvelopeMath.CircleFacts Circle(
        double cx, double cy, double cz, double radius,
        double e1x, double e1y, double e1z, double e2x, double e2y, double e2z) =>
        new(new Vector(cx, cy, cz), radius,
            new Vector(e1x, e1y, e1z),
            new Vector(e2x, e2y, e2z));

    // ---------------------------------- the live failure reproduced exactly

    [Fact]
    public void TiltedSelectedLine_D20Circle_NoLongerUnderestimatesZSpan()
    {
        // The live case (2026-10-20 10:43): XY-plane circle d20, selected
        // line axis point (-201.964,-9.060,0) dir (0.701,0.713,0) — the
        // axis perpendicular sits ~44.6° between the 4 old sample points.
        // The old algorithm returned maxR=144.723 (Z span 289.446); the
        // REAL NX body had Z span 295.192 (maxR=147.596). The exact math
        // must match the independent reference within tolerance.
        var circle = Circle(0, 0, 0, 10, 1, 0, 0, 0, 1, 0);
        // Circle center unknown from the log alone; the failure implies the
        // profile's center-to-axis distance D ≈ 137.6 (real maxR 147.596 −
        // r 10). Place the circle center on the X axis at ap.x + 137.596
        // along the axis direction — any placement with the same D yields
        // the same maxR, which is the invariant under test.
        var ap = new Vector(-201.964, -9.060, 0);
        var u = Unit(0.701, 0.713, 0);
        // center = ap + u·137.596 (on-axis) → maxR = r = 10. For the live
        // maxR=147.596 the center must be OFF-axis; take the perpendicular
        // offset along the in-plane perpendicular of u:
        var perpDir = Unit(-u.Y, u.X, 0);
        // Move the center perpendicular to the axis by D_perp: real maxR =
        // D_perp + r → D_perp = 137.596.
        var c = ap + new Vector(u.X * 10, u.Y * 10, u.Z * 10)
                  + new Vector(perpDir.X * 137.596, perpDir.Y * 137.596, perpDir.Z * 137.596);
        var facts = new RevolveEnvelopeMath.CircleFacts(c, 10,
            new Vector(1, 0, 0), new Vector(0, 1, 0));

        double maxR = RevolveEnvelopeMath.MaxRadialDistance(facts,
            new RevolveEnvelopeMath.AxisLine(ap, u));
        var (refMaxR, _, _, _, _, _, _) = ReferenceSolution(c, 10,
            new Vector(1, 0, 0), new Vector(0, 1, 0),
            ap, u);

        Assert.Equal(147.596, maxR, 3);            // within MaxRadialError of truth
        Assert.Equal(refMaxR, maxR, 3);            // matches independent dense scan
        // The old 4-point value was 144.723 — the fix must EXCEED it.
        Assert.True(maxR > 144.723 + 1.0);
    }

    [Fact]
    public void TiltedAxis_EnvelopeMatchesDenseScan_ContainmentInvariant()
    {
        // Requirement 四's invariant, evaluated on the live-failure axis
        // (point (-201.964,-9.060,0), dir (0.701,0.713,0), d20 circle at
        // the live-scale offsets): the computed envelope must contain the
        // INDEPENDENT dense swept-AABB on every axis, and must exceed the
        // old 4-point maxR that caused the wrongful rollback.
        var circle = Circle(0, 0, 0, 10, 1, 0, 0, 0, 1, 0);
        var ap = new Vector(-201.964, -9.060, 0);
        var u = Unit(0.701, 0.713, 0);
        var perpDir = Unit(-u.Y, u.X, 0);
        var c = ap + new Vector(u.X * 141.8, u.Y * 141.8, u.Z * 141.8)
                  + new Vector(perpDir.X * 137.596, perpDir.Y * 137.596, perpDir.Z * 137.596);
        var facts = new RevolveEnvelopeMath.CircleFacts(c, 10,
            new Vector(1, 0, 0), new Vector(0, 1, 0));

        var env = RevolveEnvelopeMath.ComputeFullSweepEnvelope(facts,
            new RevolveEnvelopeMath.AxisLine(ap, u));
        var (_, dMinX, dMinY, dMinZ, dMaxX, dMaxY, dMaxZ) = ReferenceSolution(
            c, 10, new Vector(1, 0, 0), new Vector(0, 1, 0), ap, u);

        Assert.True(env.MinX <= dMinX + 0.01, $"MinX {env.MinX} > dense {dMinX}");
        Assert.True(env.MinY <= dMinY + 0.01, $"MinY {env.MinY} > dense {dMinY}");
        Assert.True(env.MinZ <= dMinZ + 0.01, $"MinZ {env.MinZ} > dense {dMinZ}");
        Assert.True(env.MaxX >= dMaxX - 0.01, $"MaxX {env.MaxX} < dense {dMaxX}");
        Assert.True(env.MaxY >= dMaxY - 0.01, $"MaxY {env.MaxY} < dense {dMaxY}");
        Assert.True(env.MaxZ >= dMaxZ - 0.01, $"MaxZ {env.MaxZ} < dense {dMaxZ}");

        // And the maxR must be a tight bound: within 0.01mm of the dense scan.
        double denseMaxR = 0;
        var w = c - ap;
        for (int i = 0; i < 200_000; i++)
        {
            double theta = 2 * Math.PI * i / 200_000;
            var p = w + new Vector(10 * Math.Cos(theta), 10 * Math.Sin(theta), 0);
            var pu = p.Dot(u);
            var perp = p - new Vector(u.X * pu, u.Y * pu, u.Z * pu);
            denseMaxR = Math.Max(denseMaxR, perp.Length);
        }
        double computedMaxR = RevolveEnvelopeMath.MaxRadialDistance(facts,
            new RevolveEnvelopeMath.AxisLine(ap, u));
        Assert.Equal(denseMaxR, computedMaxR, 2);
        // The old 4-point sampling underestimated by ~2.9mm on this shape.
        Assert.True(computedMaxR > 144.723 + 1.0);
    }

    // ----------------------------------- cross-checks on known geometry

    [Fact]
    public void CircleOnWcsXAxis_ExactSphereEnvelope()
    {
        // Circle d20 centered on the X axis (XY plane): revolving about X
        // gives a r=10 sphere → envelope [-10,10]³.
        var circle = Circle(30, 0, 0, 10, 1, 0, 0, 0, 1, 0);
        var env = RevolveEnvelopeMath.ComputeFullSweepEnvelope(circle,
            new RevolveEnvelopeMath.AxisLine(
                new Vector(0, 0, 0),
                new Vector(1, 0, 0)));
        // Axial: the circle spans x∈[20,40]; radial reach 10 → the sphere
        // bulges to [30−10, 30+10] on Y/Z and [20,40] on X.
        Assert.Equal(20, env.MinX, 3);
        Assert.Equal(40, env.MaxX, 3);
        Assert.Equal(-10, env.MinY, 3);
        Assert.Equal(10, env.MaxY, 3);
        Assert.Equal(-10, env.MinZ, 3);
        Assert.Equal(10, env.MaxZ, 3);
    }

    [Fact]
    public void TiltedAxis45Degrees_MatchesIndependentReference()
    {
        // 45° tilted axis in the XY plane, circle d20 in the XY plane
        // centered 100mm from the origin along X — the extreme sits exactly
        // between the old sample points; the envelope must match the dense
        // independent swept-AABB scan on every axis.
        var circle = Circle(100, 0, 0, 10, 1, 0, 0, 0, 1, 0);
        var ap = new Vector(0, -20, 0);
        var u = Unit(1, 1, 0);
        double maxR = RevolveEnvelopeMath.MaxRadialDistance(circle,
            new RevolveEnvelopeMath.AxisLine(ap, u));
        var (refMaxR, refMinX, refMinY, refMinZ, refMaxX, refMaxY, refMaxZ) = ReferenceSolution(
            circle.Center, circle.Radius,
            new Vector(1, 0, 0), new Vector(0, 1, 0),
            ap, u);
        Assert.Equal(refMaxR, maxR, 3);

        var env = RevolveEnvelopeMath.ComputeFullSweepEnvelope(circle,
            new RevolveEnvelopeMath.AxisLine(ap, u));
        // Every axis of the envelope must match the independent dense
        // swept-AABB scan within the stated error bound.
        Assert.Equal(refMinX, env.MinX, 2);
        Assert.Equal(refMaxX, env.MaxX, 2);
        Assert.Equal(refMinY, env.MinY, 2);
        Assert.Equal(refMaxY, env.MaxY, 2);
        Assert.Equal(refMinZ, env.MinZ, 2);
        Assert.Equal(refMaxZ, env.MaxZ, 2);
    }

    [Fact]
    public void XzPlaneCircle_AboutYAxis_FullSweepEnvelope()
    {
        // XZ-plane circle d20 centered (30,0,0), axis = world Y. The circle
        // lies in a plane CONTAINING the axis and every profile point has
        // y=0 — each point sweeps a full circle of radius = its distance to
        // Y (from 20 to 40) IN ITS OWN y=0 PLANE, so the swept solid spans
        // X∈[-40,40], Z∈[-40,40] and stays at Y=0 exactly (the sweep never
        // leaves the circle's own plane when the axis lies in it).
        var circle = Circle(30, 0, 0, 10, 1, 0, 0, 0, 0, 1);
        var env = RevolveEnvelopeMath.ComputeFullSweepEnvelope(circle,
            new RevolveEnvelopeMath.AxisLine(
                new Vector(0, 0, 0),
                new Vector(0, 1, 0)));
        Assert.Equal(-40, env.MinX, 3);
        Assert.Equal(40, env.MaxX, 3);
        Assert.Equal(0, env.MinY, 3);
        Assert.Equal(0, env.MaxY, 3);
        Assert.Equal(-40, env.MinZ, 3);
        Assert.Equal(40, env.MaxZ, 3);
    }

    // --------------------------------------------- invalid-input rejection

    [Fact]
    public void ZeroAxisDirection_IsRejected()
    {
        var circle = Circle(0, 0, 0, 10, 1, 0, 0, 0, 1, 0);
        Assert.Throws<ArgumentException>(() => RevolveEnvelopeMath.MaxRadialDistance(
            circle, new RevolveEnvelopeMath.AxisLine(
                new Vector(0, 0, 0),
                new Vector(0, 0, 0))));
    }

    [Fact]
    public void NegativeRadius_IsRejected()
    {
        var circle = Circle(0, 0, 0, -5, 1, 0, 0, 0, 1, 0);
        Assert.Throws<ArgumentOutOfRangeException>(() => RevolveEnvelopeMath.MaxRadialDistance(
            circle, new RevolveEnvelopeMath.AxisLine(
                new Vector(0, 0, 0),
                new Vector(1, 0, 0))));
    }

    [Fact]
    public void NonFiniteRadius_IsRejected()
    {
        var circle = Circle(0, 0, 0, double.NaN, 1, 0, 0, 0, 1, 0);
        Assert.Throws<ArgumentOutOfRangeException>(() => RevolveEnvelopeMath.MaxRadialDistance(
            circle, new RevolveEnvelopeMath.AxisLine(
                new Vector(0, 0, 0),
                new Vector(1, 0, 0))));
    }

    [Fact]
    public void ZeroRadius_DegenerateCircle_ProducesPointEnvelope()
    {
        // r=0: the "circle" is a point — maxR = center distance, axial span
        // degenerate to the center's projection. Must not throw.
        var circle = Circle(30, 0, 0, 0, 1, 0, 0, 0, 1, 0);
        double maxR = RevolveEnvelopeMath.MaxRadialDistance(circle,
            new RevolveEnvelopeMath.AxisLine(
                new Vector(0, 0, 0),
                new Vector(0, 1, 0)));
        Assert.Equal(30, maxR, 3);
    }
}
