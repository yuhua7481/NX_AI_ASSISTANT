using NXAI.Assistant.Core;
using Xunit;

namespace NXAI.Assistant.Tests;

/// <summary>
/// Phase 8.8: deterministic sketch-frame rules for the selected-plane path.
/// Pins the invariants the live fix depends on: right-handed frames
/// (u × v = n), axis stability for a fixed normal (no flip between repeated
/// executions on one plane), u unchanged when the normal flips (top vs
/// bottom face of the same slab), and refusal of degenerate normals.
/// </summary>
public class SelectedPlaneFrameMathTests
{
    private static (double X, double Y, double Z) Cross(
        (double X, double Y, double Z) a, (double X, double Y, double Z) b) =>
        (a.Y * b.Z - a.Z * b.Y, a.Z * b.X - a.X * b.Z, a.X * b.Y - a.Y * b.X);

    private static void AssertFrame(double nx, double ny, double nz)
    {
        var f = SelectedPlaneFrameMath.BuildFrame(nx, ny, nz);
        var u = (f.Ux, f.Uy, f.Uz);
        var v = (f.Vx, f.Vy, f.Vz);
        var n = Norm(nx, ny, nz);

        // Right-handed: u × v = n.
        var cross = Cross(u, v);
        Assert.Equal(n.X, cross.Item1, 12);
        Assert.Equal(n.Y, cross.Item2, 12);
        Assert.Equal(n.Z, cross.Item3, 12);

        // Orthonormal.
        double ulen = Math.Sqrt(u.Item1 * u.Item1 + u.Item2 * u.Item2 + u.Item3 * u.Item3);
        double vlen = Math.Sqrt(v.Item1 * v.Item1 + v.Item2 * v.Item2 + v.Item3 * v.Item3);
        Assert.Equal(1.0, ulen, 12);
        Assert.Equal(1.0, vlen, 12);
        Assert.Equal(0.0, u.Item1 * v.Item1 + u.Item2 * v.Item2 + u.Item3 * v.Item3, 12);
        Assert.Equal(0.0, u.Item1 * n.X + u.Item2 * n.Y + u.Item3 * n.Z, 12);
    }

    private static (double X, double Y, double Z) Norm(double x, double y, double z)
    {
        double l = Math.Sqrt(x * x + y * y + z * z);
        return (x / l, y / l, z / l);
    }

    [Theory]
    [InlineData(0, 0, 1)]     // top face normal +Z
    [InlineData(0, 0, -1)]    // bottom face normal -Z
    [InlineData(0, -1, 0)]    // XZ-plane convention normal (WCS XZ)
    [InlineData(1, 0, 0)]     // YZ-plane convention normal
    [InlineData(0, 1, 0)]
    [InlineData(1, 1, 1)]
    [InlineData(-1, 2, 3)]
    [InlineData(0.5773502, 0.5773502, 0.5773502)]
    public void Frame_IsRightHandedOrthonormal(double nx, double ny, double nz)
    {
        AssertFrame(nx, ny, nz);
    }

    [Fact]
    public void SameNormal_RepeatedCalls_GiveIdenticalFrame()
    {
        var a = SelectedPlaneFrameMath.BuildFrame(0.3, -0.5, 0.81);
        var b = SelectedPlaneFrameMath.BuildFrame(0.3, -0.5, 0.81);
        Assert.Equal(a, b);
    }

    [Fact]
    public void FlippedNormal_LeavesU_Unchanged()
    {
        // Top/bottom face of one slab: same plane, opposite normals. The
        // requirement "同一平面重复执行不出现不必要的坐标翻转" holds for the
        // normal direction the user picks.
        var top = SelectedPlaneFrameMath.BuildFrame(0, 0, 1);
        var bottom = SelectedPlaneFrameMath.BuildFrame(0, 0, -1);
        Assert.Equal(top.Ux, bottom.Ux, 12);
        Assert.Equal(top.Uy, bottom.Uy, 12);
        Assert.Equal(top.Uz, bottom.Uz, 12);
    }

    [Fact]
    public void AxisAlignedNormals_GetStableAxis_U()
    {
        // +Z/-Z normal → u = +X (smallest |n·axis|, X first on ties).
        var z = SelectedPlaneFrameMath.BuildFrame(0, 0, 1);
        Assert.Equal(1, z.Ux, 12);
        Assert.Equal(0, z.Uy, 12);
        Assert.Equal(0, z.Uz, 12);
        // v = n × u = +Z × +X = +Y.
        Assert.Equal(0, z.Vx, 12);
        Assert.Equal(1, z.Vy, 12);
        Assert.Equal(0, z.Vz, 12);

        // -Y normal (XZ convention) → u = +X; v = -Y × X = +Z.
        var ny = SelectedPlaneFrameMath.BuildFrame(0, -1, 0);
        Assert.Equal(1, ny.Ux, 12);
        Assert.Equal(0, ny.Uy, 12);
        Assert.Equal(0, ny.Uz, 12);
        Assert.Equal(0, ny.Vx, 12);
        Assert.Equal(0, ny.Vy, 12);
        Assert.Equal(1, ny.Vz, 12);

        // +X normal (YZ convention) → u = +Y (X excluded: |n·X|=1); v = X × Y = +Z.
        var px = SelectedPlaneFrameMath.BuildFrame(1, 0, 0);
        Assert.Equal(0, px.Ux, 12);
        Assert.Equal(1, px.Uy, 12);
        Assert.Equal(0, px.Uz, 12);
        Assert.Equal(0, px.Vx, 12);
        Assert.Equal(0, px.Vy, 12);
        Assert.Equal(1, px.Vz, 12);
    }

    [Fact]
    public void DegenerateNormals_AreRejected()
    {
        Assert.Throws<ArgumentException>(() => SelectedPlaneFrameMath.BuildFrame(0, 0, 0));
        Assert.Throws<ArgumentException>(() => SelectedPlaneFrameMath.BuildFrame(double.NaN, 0, 1));
        Assert.Throws<ArgumentException>(() => SelectedPlaneFrameMath.BuildFrame(1, double.PositiveInfinity, 0));
    }
}
