using Xunit;

namespace NXAI.Assistant.Tests;

/// <summary>
/// Regression guard for the live caseB failure of 2026-10-18: the revolve
/// envelope calculator's Arc branch built box-corner points (center ± r in
/// ALL three axes), which fall OUTSIDE the circle's own plane. For the XZ
/// circle (center (45,0,0), r=5) it manufactured a Y=±5 point, inflating the
/// radial reach to sqrt(50²+5²)=50.249 — the correct torus (bbox ±50) was
/// rejected and rolled back after a successful CommitFeature. This test
/// re-states the extreme-point math (center ± r·xDir / ± r·yDir on the arc's
/// own plane, Arc.Matrix columns = local axes) as pure math and asserts the
/// computed envelope for the exact live geometry: circle on XZ (n=(0,1,0))
/// revolved around absolute Z. The envelope math itself is mirrored from
/// ComputeRevolveEnvelopeAabb (NxHost, NXOpen-typed and not unit-testable
/// directly).
/// </summary>
public class RevolveEnvelopeAabbTests
{
    [Fact]
    public void CaseB_XzCircle45r5_AroundZ360_EnvelopeIsTorusBox()
    {
        // XZ plane sketch: local X=(1,0,0), local Y=(0,0,1) (=world Z),
        // normal n=(0,-1,0) — the arc was built with these as (u,v).
        var (xDir, yDir) = ((1.0, 0.0, 0.0), (0.0, 0.0, 1.0));
        var center = (45.0, 0.0, 0.0);   // U=45 on the XZ plane
        double r = 5.0;

        // Extreme points ON the arc's own plane (the fixed math).
        var pts = new List<(double, double, double)>
        {
            (center.Item1 + r * xDir.Item1, center.Item2 + r * xDir.Item2, center.Item3 + r * xDir.Item3),
            (center.Item1 - r * xDir.Item1, center.Item2 - r * xDir.Item2, center.Item3 - r * xDir.Item3),
            (center.Item1 + r * yDir.Item1, center.Item2 + r * yDir.Item2, center.Item3 + r * yDir.Item3),
            (center.Item1 - r * yDir.Item1, center.Item2 - r * yDir.Item2, center.Item3 - r * yDir.Item3),
        };

        // Revolve envelope math mirrored from ComputeRevolveEnvelopeAabb:
        // axis = absolute Z through origin.
        var (axU, axV, axW) = (0.0, 0.0, 1.0);
        double zMin = double.MaxValue, zMax = double.MinValue, maxR = 0;
        foreach (var p in pts)
        {
            var rel = (p.Item1 - 0, p.Item2 - 0, p.Item3 - 0);
            double z = rel.Item3;
            var perp = (rel.Item1, rel.Item2, 0.0);
            maxR = Math.Max(maxR, Math.Sqrt(perp.Item1 * perp.Item1 + perp.Item2 * perp.Item2));
            zMin = Math.Min(zMin, z);
            zMax = Math.Max(zMax, z);
        }
        double sX = Math.Sqrt(Math.Max(0, 1 - axU * axU));
        double sY = Math.Sqrt(Math.Max(0, 1 - axV * axV));
        double sZ = Math.Sqrt(Math.Max(0, 1 - axW * axW));
        double tLo = Math.Min(zMin, zMax), tHi = Math.Max(zMin, zMax);

        double minX = 0 + axU * tLo - sX * maxR;
        double maxX = 0 + axU * tHi + sX * maxR;
        double minY = 0 + axV * tLo - sY * maxR;
        double maxY = 0 + axV * tHi + sY * maxR;
        double minZ = 0 + axW * tLo - sZ * maxR;
        double maxZ = 0 + axW * tHi + sZ * maxR;

        // The analytic torus box — center ± r·xDir gives X∈[40,50],
        // center ± r·yDir gives Z∈[-5,5]; maxR = 50 → XY reach exactly 50.
        Assert.Equal(50.0, maxR, 9);
        Assert.Equal(-50, minX, 9);
        Assert.Equal(50, maxX, 9);
        Assert.Equal(-50, minY, 9);
        Assert.Equal(50, maxY, 9);
        Assert.Equal(-5, minZ, 9);
        Assert.Equal(5, maxZ, 9);
    }

    [Fact]
    public void BoxCornerPoints_WouldInflateRadialReach_TheLiveFailure()
    {
        // Documents the bug this fixes: adding (c ± r) in all three axes —
        // the old Arc branch — produces a point NOT on the circle and a
        // radial reach of sqrt(50²+5²) ≈ 50.249, which exceeded the 0.01mm
        // verification tolerance and rolled back a correct torus.
        var center = (45.0, 0.0, 0.0);
        double r = 5.0;
        var bogusPoint = (center.Item1 + r, center.Item2 + r, center.Item3 + r);
        double bogusReach = Math.Sqrt((bogusPoint.Item1 - 0) * (bogusPoint.Item1 - 0) +
                                      (bogusPoint.Item2 - 0) * (bogusPoint.Item2 - 0));
        double perWorldAxis = bogusReach * Math.Sqrt(1 - 0); // axis = Z: sX = sY = 1
        Assert.True(perWorldAxis > 50.248 && perWorldAxis < 50.250,
            "box-corner math must inflate to ~50.249 — the observed failure value");
    }
}
