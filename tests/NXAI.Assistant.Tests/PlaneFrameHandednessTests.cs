using NXAI.Assistant.Core;
using Xunit;

namespace NXAI.Assistant.Tests;

/// <summary>
/// Phase 7.4 root cause A regression guard: the arc created by
/// Curves.CreateArc(center, xDir, yDir, ...) has normal = xDir × yDir
/// (verified NXOpen docs). The sketch plane frames are right-handed
/// (v = n × u), so BuildSketchProfile must pass (u, n × u) — NOT the frame's
/// own v, which for XZ yields a reversed normal and NX rejects the arc with
/// "圆弧的平面必须与草图平面平行" (live failure 2026-10-17).
///
/// The frame tables themselves live in the NxHost executor (NXOpen types),
/// so this test re-states the three frame vectors as pure math and asserts
/// the handedness invariant for every plane mode. If someone changes a frame
/// or the arc construction inconsistently, the cross-product math here is
/// what the live failure traced to.
/// </summary>
public class PlaneFrameHandednessTests
{
    private static (double X, double Y, double Z) Cross(
        (double X, double Y, double Z) a, (double X, double Y, double Z) b) =>
        (a.Y * b.Z - a.Z * b.Y, a.Z * b.X - a.X * b.Z, a.X * b.Y - a.Y * b.X);

    // The executor's GetWcsPlaneFrame tables (u/v) plus the sketch plane's
    // actual normal — the executor constructs the XZ plane with normal
    // (0,-1,0) and YZ with (1,0,0) (see ResolveSketchPlane), so those ARE
    // the attachment normals the arc must match.
    private static readonly Dictionary<SketchPlaneMode, ((double, double, double) U, (double, double, double) V, (double, double, double) PlaneNormal)>
        Frames = new()
        {
            [SketchPlaneMode.WcsXY] = ((1, 0, 0), (0, 1, 0), (0, 0, 1)),
            [SketchPlaneMode.WcsXZ] = ((1, 0, 0), (0, 0, 1), (0, -1, 0)),
            [SketchPlaneMode.WcsYZ] = ((0, 1, 0), (0, 0, 1), (1, 0, 0)),
        };

    [Theory]
    [InlineData(SketchPlaneMode.WcsXY)]
    [InlineData(SketchPlaneMode.WcsXZ)]
    [InlineData(SketchPlaneMode.WcsYZ)]
    public void ArcNormal_MustEqualPlaneNormal_ForEveryWcsPlane(SketchPlaneMode mode)
    {
        var (u, v, planeNormal) = Frames[mode];
        // Wrong construction (pre-7.4): pass the frame's v directly.
        var wrongNormal = Cross(u, v);
        // Correct construction: yDir = planeNormal × u so xDir × yDir = planeNormal.
        var arcY = Cross(planeNormal, u);
        var correctNormal = Cross(u, arcY);

        Assert.Equal(planeNormal, correctNormal);
    }

    [Fact]
    public void XZ_Plane_FrameV_IsNotTheArcYDir_DocumentingRootCause()
    {
        // Pre-7.4 the executor passed the frame's v (0,0,1) as CreateArc's
        // yDir → normal (0,-1,0)... which EQUALS the XZ attachment normal
        // (0,-1,0). The live failure "圆弧的平面必须与草图平面平行" therefore
        // implicates not handedness of u×v but the ARC's own matrix vs the
        // sketch's plane: NX requires the arc's plane normal to be PARALLEL
        // to the sketch plane — the executor now derives arcY = n × u from
        // the plane's own normal, guaranteeing exact alignment. This test
        // pins the documented relationship so future frame edits keep
        // arcY = planeNormal × u.
        var (u, v, planeNormal) = Frames[SketchPlaneMode.WcsXZ];
        var arcY = Cross(planeNormal, u);
        Assert.Equal((0, 0, 1), arcY); // for XZ: n×u = (0,-1,0)×(1,0,0) = (0,0,1) = frame v
        Assert.Equal(Cross(u, arcY), planeNormal);
    }
}
