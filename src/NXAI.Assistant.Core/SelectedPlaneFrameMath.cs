namespace NXAI.Assistant.Core;

/// <summary>
/// Deterministic sketch-plane frame for a selected plane (planar face or
/// datum plane) with unit normal n and a chosen origin point:
///   u = the world axis (X, Y, Z — ties broken X before Y before Z) whose
///       in-plane projection is longest, i.e. the axis with the smallest
///       |n·axis|, oriented positively along that axis;
///   v = n × u.
/// The same plane normal therefore always yields the same frame: repeating
/// an execution on the same plane cannot flip axes (the Phase 8.8
/// "stable, repeatable plane coordinates" requirement). Negating the normal
/// (top vs bottom face of the same slab) leaves u unchanged by construction
/// — the chosen axis and its projection direction are identical. The frame
/// is right-handed: u × v = n. A zero or non-finite normal is rejected.
/// </summary>
public static class SelectedPlaneFrameMath
{
    public static (double Ux, double Uy, double Uz, double Vx, double Vy, double Vz)
        BuildFrame(double nx, double ny, double nz)
    {
        if (!double.IsFinite(nx) || !double.IsFinite(ny) || !double.IsFinite(nz))
            throw new ArgumentException("平面法向包含非有限值，无法确定草图坐标系。");
        double len = Math.Sqrt(nx * nx + ny * ny + nz * nz);
        if (len < 1e-12)
            throw new ArgumentException("平面法向为零向量，无法确定草图坐标系。");
        nx /= len; ny /= len; nz /= len;

        // Axis most in-plane: smallest |n·axis|; X before Y before Z on ties
        // (deterministic tie-break, no selection-order dependence).
        double absX = Math.Abs(nx), absY = Math.Abs(ny), absZ = Math.Abs(nz);
        double ax, ay, az, dot;
        if (absX <= absY && absX <= absZ) { ax = 1; ay = 0; az = 0; dot = nx; }
        else if (absY <= absZ) { ax = 0; ay = 1; az = 0; dot = ny; }
        else { ax = 0; ay = 0; az = 1; dot = nz; }

        // In-plane projection of that axis, normalized; u·axis ≥ 0 by
        // construction, so the sign is stable for a given plane.
        double ux = ax - dot * nx, uy = ay - dot * ny, uz = az - dot * nz;
        double ulen = Math.Sqrt(ux * ux + uy * uy + uz * uz);
        if (ulen < 1e-9)
            throw new InvalidOperationException("平面法向与所有坐标轴平行，无法确定面内 U 方向。");
        ux /= ulen; uy /= ulen; uz /= ulen;

        // v = n × u so that u × v = n (right-handed frame).
        double vx = ny * uz - nz * uy;
        double vy = nz * ux - nx * uz;
        double vz = nx * uy - ny * ux;
        return (ux, uy, uz, vx, vy, vz);
    }
}
