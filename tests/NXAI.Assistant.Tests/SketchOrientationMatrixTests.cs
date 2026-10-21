using Xunit;

namespace NXAI.Assistant.Tests;

/// <summary>
/// Sketch.Orientation.Element matrix convention regression (2026-10-21 live
/// failure: a tilted datum plane's line landed off the sketch plane —
/// "对象不在草图平面中"). The live log pinned the convention: the ROWS of
/// the Matrix3x3 are the local axes. The live matrix from the failing
/// session is restated here as raw numbers (no NXOpen needed) and the
/// row/column extractions are compared against the datum plane frame the
/// executor itself had derived (u=(1,0,0) v=(0,0.574,0.819) n=(0,-0.819,0.574)).
///
/// NXOpen coverage boundary: this file proves the extraction math against
/// the live-captured matrix; that the .NET Matrix3x3 field layout maps
/// one-to-one onto the live numbers is a live-NX fact already confirmed by
/// the 2026-10-21 session log (the logged values came from those fields).
/// </summary>
public class SketchOrientationMatrixTests
{
    // Live 2026-10-21 16:41 tilted datum plane session: the sketch was
    // created FROM this plane, so Orientation.Element's rows must reproduce
    // the plane frame (up to the normal orientation NX normalized — the
    // sketch plane is the same plane either way).
    private static readonly (double Xx, double Xy, double Xz,
        double Yx, double Yy, double Yz,
        double Zx, double Zy, double Zz) Live = (1, 0, 0, 0, 0.574, 0.819, 0, -0.819, 0.574);

    private static ((double X, double Y, double Z) U,
        (double X, double Y, double Z) V,
        (double X, double Y, double Z) N) RowExtract(
        (double Xx, double Xy, double Xz, double Yx, double Yy, double Yz, double Zx, double Zy, double Zz) m) =>
        ((m.Xx, m.Xy, m.Xz), (m.Yx, m.Yy, m.Yz), (m.Zx, m.Zy, m.Zz));

    private static ((double X, double Y, double Z) U,
        (double X, double Y, double Z) V,
        (double X, double Y, double Z) N) ColumnExtract(
        (double Xx, double Xy, double Xz, double Yx, double Yy, double Yz, double Zx, double Zy, double Zz) m) =>
        ((m.Xx, m.Yx, m.Zx), (m.Xy, m.Yy, m.Zy), (m.Xz, m.Yz, m.Zz));

    private static ((double, double, double), (double, double, double)) Cross(
        (double X, double Y, double Z) a, (double X, double Y, double Z) b) =>
        ((a.Y * b.Z - a.Z * b.Y, a.Z * b.X - a.X * b.Z, a.X * b.Y - a.Y * b.X), (0, 0, 0));

    [Fact]
    public void RowExtraction_ReproducesTiltedDatumPlaneFrame()
    {
        var (u, v, n) = RowExtract(Live);
        // The selected-plane frame the executor logged for this datum plane.
        Assert.Equal((1, 0, 0), (u.X, u.Y, u.Z));
        Assert.Equal((0, 0.574, 0.819), (v.X, v.Y, v.Z));
        Assert.Equal((0, -0.819, 0.574), (n.X, n.Y, n.Z));
    }

    [Fact]
    public void ColumnExtraction_FlippedNormal_MirrorsV_OnTiltedPlane()
    {
        var (_, v, n) = ColumnExtract(Live);
        // Documents the bug: column extraction gives n=(0,+0.819,0.574) —
        // the flipped normal — and a mirrored v. Exactly what the live log
        // recorded before the fix.
        Assert.Equal((0, 0.819, 0.574), (n.X, n.Y, n.Z));
        Assert.Equal((0, 0.574, -0.819), (v.X, v.Y, v.Z));
    }

    [Fact]
    public void RowFrame_IsRightHandedOrthogonal()
    {
        var (u, v, n) = RowExtract(Live);
        double dotUV = u.X * v.X + u.Y * v.Y + u.Z * v.Z;
        double dotUN = u.X * n.X + u.Y * n.Y + u.Z * n.Z;
        double dotVN = v.X * n.X + v.Y * n.Y + v.Z * n.Z;
        Assert.Equal(0, dotUV, 12);
        Assert.Equal(0, dotUN, 12);
        Assert.Equal(0, dotVN, 12);
        // u × v = n (right-handed).
        var (cross, _) = Cross(u, v);
        Assert.Equal(n.X, cross.Item1, 12);
        Assert.Equal(n.Y, cross.Item2, 12);
        Assert.Equal(n.Z, cross.Item3, 12);
    }

    [Fact]
    public void LineEndpoints_InRowFrame_LieOnSketchPlane()
    {
        // The live failing request: line local (0,0)→(30,15) on the tilted
        // plane, sketch origin (0,4,30). Row-frame points must satisfy
        // (p−origin)·n = 0; column-frame points must NOT (that was the bug).
        var (uR, vR, nR) = RowExtract(Live);
        var (_, vC, _) = ColumnExtract(Live);
        var origin = (X: 0.0, Y: 4.0, Z: 30.0);

        double Off((double X, double Y, double Z) p, (double X, double Y, double Z) nrm) =>
            (p.X - origin.X) * nrm.X + (p.Y - origin.Y) * nrm.Y + (p.Z - origin.Z) * nrm.Z;

        var endRow = (X: origin.X + 30 * uR.X + 15 * vR.X,
            Y: origin.Y + 30 * uR.Y + 15 * vR.Y,
            Z: origin.Z + 30 * uR.Z + 15 * vR.Z);
        Assert.Equal(0, Off(endRow, nR), 9);

        var endCol = (X: origin.X + 30 * uR.X + 15 * vC.X,
            Y: origin.Y + 30 * uR.Y + 15 * vC.Y,
            Z: origin.Z + 30 * uR.Z + 15 * vC.Z);
        Assert.NotEqual(0, Off(endCol, nR), 6); // column frame put it off-plane
    }

    [Fact]
    public void ArcUsesSameRowConvention_CenterOnPlane()
    {
        // The arc center mapping must use the same row-extracted u/v as the
        // line endpoints — one convention for every curve kind.
        var (uR, vR, nR) = RowExtract(Live);
        var origin = (X: 0.0, Y: 4.0, Z: 30.0);
        var center = (X: origin.X + 10 * uR.X + (-5) * vR.X,
            Y: origin.Y + 10 * uR.Y + (-5) * vR.Y,
            Z: origin.Z + 10 * uR.Z + (-5) * vR.Z);
        double off = (center.X - origin.X) * nR.X + (center.Y - origin.Y) * nR.Y + (center.Z - origin.Z) * nR.Z;
        Assert.Equal(0, off, 9);
    }

    [Fact]
    public void AxisAlignedPlanes_UnaffectedByTheFix()
    {
        // Axis-aligned frames are symmetric — row and column extraction
        // agree; the fix cannot move WCS XY/XZ/YZ or horizontal-plane
        // behavior. Live-verified frames (Phase 8.6 tables).
        var xy = (Xx: 1.0, Xy: 0.0, Xz: 0.0, Yx: 0.0, Yy: 1.0, Yz: 0.0, Zx: 0.0, Zy: 0.0, Zz: 1.0);
        var (u, v, n) = RowExtract(xy);
        Assert.Equal((1, 0, 0), (u.X, u.Y, u.Z));
        Assert.Equal((0, 1, 0), (v.X, v.Y, v.Z));
        Assert.Equal((0, 0, 1), (n.X, n.Y, n.Z));
        var (cu, cv, cn) = ColumnExtract(xy);
        Assert.Equal((u.X, u.Y, u.Z), (cu.X, cu.Y, cu.Z));
        Assert.Equal((v.X, v.Y, v.Z), (cv.X, cv.Y, cv.Z));
        Assert.Equal((n.X, n.Y, n.Z), (cn.X, cn.Y, cn.Z));
    }
}
