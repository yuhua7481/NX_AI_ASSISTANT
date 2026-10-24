using System.Globalization;
using System.Text.Json;
using NXAI.Assistant.Core;
using NXOpen;
using NXOpen.Features;
using NXOpen.UF;

namespace NXAI.Assistant.NxHost.NX2512;

/// <summary>
/// Phase 9.1: SIMPLE blind hole executor (create_hole). Follows the shared
/// geometry-tool safety contract: current work part only, visible UndoMark
/// with rollback on every failure, builder Destroy on all paths, stage-tagged
/// PhaseLog lines, error-log writes on every failure exit, postcondition
/// verification before reporting success.
///
/// APIs verified against NX2512 NXOpen.xml before use:
///   - FeatureCollection.CreateHoleFeatureBuilder(Feature) (NX3+; null
///     feature = create, same convention as the boolean/revolve builders);
///   - HoleFeatureBuilder.SetSimpleHole(Point3d, bool, ISurface, string) —
///     location, reverse flag (datum-plane placement only), placement face,
///     diameter (string expression);
///   - HoleFeatureBuilder.SetDepthAndTipAngle(string, string) — blind depth
///     + drill tip angle (setting depth ignores thru face per docs);
///   - Feature.CommitFeature / Session.UpdateManager.DoUpdate — the shared
///     commit flow;
///   - Face: the verified planar-face read path (AskFaceUvMinmax +
///     AskFaceProps — NEVER UFEvalsf on planar faces, Phase 5.1 crash
///     lesson) provides the frame for the hole center.
///
/// Live-verify items (cannot be confirmed from local metadata; covered by
/// the acceptance checklist): Face-as-ISurface acceptance, the committed
/// feature's FeatureType string, and GetBodies semantics for a subtractive
/// hole feature.
/// </summary>
public sealed partial class NxToolExecutor
{
    public CreateBlockExecutionResult ExecuteCreateHole(
        CreateHoleParameters parameters, string? expectedWorkPartFullPath)
    {
        PhaseLog.Write("execute.hole",
            $"begin: face={parameters.PlacementFaceRef} u={parameters.LocationU:0.###} " +
            $"v={parameters.LocationV:0.###} d={parameters.Diameter:0.###} depth={parameters.Depth:0.###} " +
            $"tip={parameters.TipAngleDeg:0.###} expectedPart={expectedWorkPartFullPath ?? "(null)"}");

        if (!TryResolveWorkPart(expectedWorkPartFullPath, "孔",
                out Session session, out Part workPart, out string workPartIdentity,
                out CreateBlockExecutionResult? failure))
        {
            PhaseLog.Write("execute.hole", "REFUSED: work part resolution failed");
            LogFailure(new InvalidOperationException("孔特征前置失败：工作部件解析失败。"),
                workPartIdentity: "(unknown)", operation: "CreateHole");
            return failure!;
        }

        // Resolve the placement face: the reference must match EXACTLY ONE
        // face in the work part (JournalIdentifier). No guessing.
        Face? placementFace;
        try
        {
            placementFace = FindFaceByRef(workPart, parameters.PlacementFaceRef);
        }
        catch (Exception ex)
        {
            PhaseLog.Write("execute.hole",
                $"ABORTED in face resolution: {ex.GetType().Name}: {ex.Message}");
            LogFailure(ex, workPartIdentity, "CreateHole");
            return CreateBlockExecutionResult.Failure(
                ToolExecutionStatus.ExecutionFailed,
                $"孔放置面解析发生异常（{ex.GetType().Name}：{ex.Message}），未执行任何建模操作。",
                rolledBack: false, workPartIdentity: workPartIdentity);
        }
        if (placementFace == null)
        {
            PhaseLog.Write("execute.hole",
                $"REFUSED: placement face {parameters.PlacementFaceRef} not resolvable");
            LogFailure(new InvalidOperationException(
                    $"孔特征前置失败：放置面引用 {parameters.PlacementFaceRef} 无法在当前工作部件解析。"),
                workPartIdentity, "CreateHole");
            return CreateBlockExecutionResult.Failure(
                ToolExecutionStatus.PreconditionsChanged,
                $"放置面引用 {parameters.PlacementFaceRef} 在当前工作部件中不存在。请让用户选中目标平面后重试。",
                workPartIdentity: workPartIdentity);
        }

        // Planar gate + frame (the verified UF_MODL read path).
        double[] uv = new double[4];
        double[] center = new double[3];
        double[] u1 = new double[3], v1 = new double[3], u2 = new double[3], v2 = new double[3];
        double[] unitNorm = new double[3];
        double[] radii = new double[2];
        try
        {
            string faceType = placementFace.SolidFaceType.ToString().ToLowerInvariant();
            if (faceType != "planar")
            {
                PhaseLog.Write("execute.hole", $"REFUSED: face is {faceType}, not planar");
                LogFailure(new InvalidOperationException($"孔特征前置失败：放置面类型为 {faceType}（需要平面）。"),
                    workPartIdentity, "CreateHole");
                return CreateBlockExecutionResult.Failure(
                    ToolExecutionStatus.PreconditionsChanged,
                    $"孔的放置面必须是平面（当前面类型：{faceType}）。请在平面上打孔。",
                    workPartIdentity: workPartIdentity);
            }

            var ufSession = UFSession.GetUFSession();
            ufSession.Modl.AskFaceUvMinmax(placementFace.Tag, uv);
            double uMid = (uv[0] + uv[1]) / 2.0;
            double vMid = (uv[2] + uv[3]) / 2.0;
            ufSession.Modl.AskFaceProps(placementFace.Tag,
                new double[] { uMid, vMid }, center, u1, v1, u2, v2, unitNorm, radii);
        }
        catch (Exception ex)
        {
            PhaseLog.Write("execute.hole",
                $"ABORTED in face frame read: {ex.GetType().Name}: {ex.Message}");
            LogFailure(ex, workPartIdentity, "CreateHole");
            return CreateBlockExecutionResult.Failure(
                ToolExecutionStatus.ExecutionFailed,
                $"读取放置面几何发生异常（{ex.GetType().Name}：{ex.Message}），未执行任何建模操作。",
                rolledBack: false, workPartIdentity: workPartIdentity);
        }

        // Hole center: face UV midpoint + local U/V offsets. The local axes
        // are the face's own tangent vectors (u1 = dP/du, v1 = dP/dv) —
        // deterministic for a given face and derived from the face itself.
        var uAxis = VecNormalize(new Vector3d(u1[0], u1[1], u1[2]));
        var vAxis = VecNormalize(new Vector3d(v1[0], v1[1], v1[2]));
        var holeCenter = new Point3d(
            center[0] + parameters.LocationU * uAxis.X + parameters.LocationV * vAxis.X,
            center[1] + parameters.LocationU * uAxis.Y + parameters.LocationV * vAxis.Y,
            center[2] + parameters.LocationU * uAxis.Z + parameters.LocationV * vAxis.Z);
        PhaseLog.Write("execute.hole",
            $"frame ok: faceCenter=({center[0]:F3},{center[1]:F3},{center[2]:F3}) " +
            $"u=({uAxis.X:F3},{uAxis.Y:F3},{uAxis.Z:F3}) v=({vAxis.X:F3},{vAxis.Y:F3},{vAxis.Z:F3}) " +
            $"holeCenter=({holeCenter.X:F3},{holeCenter.Y:F3},{holeCenter.Z:F3})");

        // Pre-attempt state for the postcondition (a hole CONSUMES material
        // from the host body: body count is unchanged; verification is via
        // the committed feature's evidence + body count stability).
        int bodiesBefore;
        HashSet<string> idsBefore;
        try
        {
            bodiesBefore = CountBodies(workPart);
            idsBefore = CollectBodyIds(workPart);
        }
        catch (Exception ex)
        {
            PhaseLog.Write("execute.hole",
                $"ABORTED in pre-state snapshot: {ex.GetType().Name}: {ex.Message}");
            LogFailure(ex, workPartIdentity, "CreateHole");
            return CreateBlockExecutionResult.Failure(
                ToolExecutionStatus.ExecutionFailed,
                $"孔前置快照发生异常（{ex.GetType().Name}：{ex.Message}），未执行任何建模操作。",
                rolledBack: false, workPartIdentity: workPartIdentity);
        }

        string FormatMm(double v) => v.ToString("R", CultureInfo.InvariantCulture);
        HoleFeatureBuilder? holeBuilder = null;
        Session.UndoMarkId markId = default;
        bool markCreated = false;
        try
        {
            markId = session.SetUndoMark(
                Session.MarkVisibility.Visible, "NXAI 创建孔");
            markCreated = true;
            PhaseLog.Write("execute.hole", "undo mark created: NXAI 创建孔");

            holeBuilder = workPart.Features.CreateHoleFeatureBuilder(null!);
            // Simple blind hole: location + placement face + diameter, then
            // depth + tip angle (per docs, depth set ⇒ thru face ignored —
            // no SetThruFace call, no SetTargetBody call: depth semantics
            // must stay in force).
            holeBuilder.SetSimpleHole(
                holeCenter, false, placementFace, FormatMm(parameters.Diameter));
            PhaseLog.Write("execute.hole",
                $"simple hole set: center=({holeCenter.X:F3},{holeCenter.Y:F3},{holeCenter.Z:F3}) d={parameters.Diameter:0.###}");
            holeBuilder.SetDepthAndTipAngle(
                FormatMm(parameters.Depth), FormatMm(parameters.TipAngleDeg));
            PhaseLog.Write("execute.hole",
                $"depth+tip set: depth={parameters.Depth:0.###} tip={parameters.TipAngleDeg:0.###}");

            var committedFeature = holeBuilder.CommitFeature() as Feature;
            PhaseLog.Write("execute.hole",
                $"commit returned: feature={(committedFeature != null ? SafeFeatureObjectId(committedFeature) : "null")} " +
                $"type={(committedFeature?.FeatureType ?? "?")} — running DoUpdate");
            session.UpdateManager.DoUpdate(markId);
            PhaseLog.Write("execute.hole",
                $"commit done: feature={(committedFeature != null ? SafeFeatureObjectId(committedFeature) : "null")} update=ok");

            // ---- postcondition (facts from NX; verdict below) ----
            string? featureType = null;
            Body[]? featureBodies = null;
            bool featureAlive = false;
            if (committedFeature != null)
            {
                try
                {
                    featureAlive = !IsDead(committedFeature);
                    featureType = committedFeature.FeatureType;
                    featureBodies = committedFeature.GetBodies().Where(b => !IsDead(b)).ToArray();
                }
                catch (Exception ex)
                {
                    PhaseLog.Write("execute.hole",
                        $"feature read FAILED: {ex.GetType().Name}: {ex.Message}");
                }
            }
            int bodiesAfter = CountBodies(workPart);
            PhaseLog.Write("execute.hole",
                $"feature evidence: alive={featureAlive} type={featureType} " +
                $"bodies={(featureBodies == null ? "unreadable" : featureBodies.Length.ToString())} " +
                $"bodyCount={bodiesBefore}→{bodiesAfter}");

            // Verdict: a committed hole MUST be (1) an alive feature, (2) the
            // body count unchanged (subtractive in-place edit — a body-count
            // change means something unexpected happened), and (3) the
            // committed feature's evidence must be readable. A hole that
            // cannot prove all three is rolled back, never reported as
            // success.
            if (committedFeature == null || !featureAlive || featureType == null ||
                featureBodies == null || bodiesAfter != bodiesBefore)
            {
                (bool undone, string undoError) = Rollback(session, markId, "NXAI 创建孔");
                LogFailure(new InvalidOperationException(
                        $"Hole postcondition failed: alive={featureAlive} type={featureType} " +
                        $"featureBodies={(featureBodies?.Length ?? -1)} bodies {bodiesBefore}→{bodiesAfter}"),
                    workPartIdentity, "CreateHole");
                return CreateBlockExecutionResult.Failure(
                    ToolExecutionStatus.VerificationFailed,
                    "孔特征创建后验证失败：无法证明孔已正确提交。" +
                    (undone ? "已回滚本次操作。" : $"回滚未完成：{undoError}。请在 NX 撤销列表中手动撤销。"),
                    rolledBack: undone, workPartIdentity: workPartIdentity);
            }

            // Geometric check: the hole must exist ON the placement face —
            // probe the center point's neighborhood: after a successful blind
            // hole, a distance query is overkill; the strongest cheap check
            // is that the feature's owning body still contains the center
            // region's face planarity (lighter: verify the feature identity
            // is resolvable by JournalIdentifier round-trip).
            string? roundTrip = FindHoleFeatureByRef(workPart, SafeFeatureObjectId(committedFeature)) != null
                ? SafeFeatureObjectId(committedFeature)
                : null;
            if (roundTrip == null)
            {
                (bool undone, string undoError) = Rollback(session, markId, "NXAI 创建孔");
                LogFailure(new InvalidOperationException(
                        "Hole feature not resolvable by JournalIdentifier after commit"),
                    workPartIdentity, "CreateHole");
                return CreateBlockExecutionResult.Failure(
                    ToolExecutionStatus.VerificationFailed,
                    "孔特征提交后无法在工作部件中重新定位，结果归属无法证明。" +
                    (undone ? "已回滚本次操作。" : $"回滚未完成：{undoError}。请在 NX 撤销列表中手动撤销。"),
                    rolledBack: undone, workPartIdentity: workPartIdentity);
            }

            string details = JsonSerializer.Serialize(new Dictionary<string, object?>
            {
                ["featureRef"] = roundTrip,
                ["featureType"] = featureType,
                ["placementFaceRef"] = placementFace.JournalIdentifier,
                ["holeCenter"] = new[] { holeCenter.X, holeCenter.Y, holeCenter.Z },
                ["diameter"] = parameters.Diameter,
                ["depth"] = parameters.Depth,
                ["tipAngleDeg"] = parameters.TipAngleDeg,
                ["bodyCountUnchanged"] = true,
            });
            return CreateBlockExecutionResult.Success(
                parameters.Diameter, parameters.Diameter, parameters.Depth,
                $"已在选定平面创建简单盲孔（本次特征 {roundTrip}，直径 {parameters.Diameter:0.###} × " +
                $"深 {parameters.Depth:0.###} 毫米，孔心 ({holeCenter.X:0.###}, {holeCenter.Y:0.###}, {holeCenter.Z:0.###})）。",
                workPartIdentity, details);
        }
        catch (Exception ex)
        {
            PhaseLog.Write("execute.hole",
                $"EXCEPTION: {ex.GetType().Name}: {ex.Message} (markCreated={markCreated})");
            (bool undone, string undoError) = markCreated
                ? Rollback(session, markId, "NXAI 创建孔")
                : (false, "撤销标记尚未创建，无需回滚");
            LogFailure(ex, workPartIdentity, markCreated ? "CreateHole" : "CreateHole-premark");
            return CreateBlockExecutionResult.Failure(
                ToolExecutionStatus.ExecutionFailed,
                $"{ex.GetType().Name}: {ex.Message}" +
                (undone ? "（本次操作已回滚，部件恢复原状。）" : $"（回滚未完成：{undoError}。请在 NX 撤销列表中手动撤销。）"),
                rolledBack: undone, workPartIdentity: workPartIdentity);
        }
        finally
        {
            if (holeBuilder != null)
            {
                try
                {
                    holeBuilder.Destroy();
                    PhaseLog.Write("execute.hole", "builder destroyed");
                }
                catch (Exception destroyEx)
                {
                    PhaseLog.Write("execute.hole",
                        $"builder destroy FAILED: {destroyEx.GetType().Name}: {destroyEx.Message}");
                }
            }
        }
    }

    /// <summary>Resolution result for the selected-point path.</summary>
    private sealed record PointResolution(bool Success, NXOpen.Point? Point, string? Source, string? Error);

    /// <summary>Face-resolution result for the selected-point path.</summary>
    private sealed record PointFaceResolution(bool Success, Face? Face, string? Error);

    /// <summary>
    /// Resolves the CURRENT selection to exactly ONE hole-center point.
    /// Accepted sources (the live selection's concrete runtime types —
    /// never name strings):
    ///   - NXOpen.Point (standalone point object);
    ///   - Features.PointFeature → resolved via Feature.GetEntities to its
    ///     NXOpen.Point (the recorded journal's pointFeature path — must be
    ///     exactly one live point);
    ///   - Sketch: ONLY when the sketch contains exactly one point (its
    ///     point geometry, already in model coordinates); a sketch with
    ///     zero or several points is refused — the user must select the
    ///     target point itself, the tool never picks one.
    /// Any other mix (multiple points, points + faces, edges, no selection)
    /// is refused with a diagnosis. The point's Coordinates are returned
    /// AS-IS: no snapping, no projection, no substitution.
    /// </summary>
    private static PointResolution ResolveSelectedPointForHole(Part workPart, string phaseTag)
    {
        var selected = new List<NXObject>();
        try
        {
            var uiSelection = NXOpen.UI.GetUI().SelectionManager;
            int count = uiSelection.GetNumSelectedObjects();
            for (int i = 0; i < count; i++)
            {
                if (uiSelection.GetSelectedTaggedObject(i) is NXObject obj)
                    selected.Add(obj);
            }
        }
        catch (Exception ex)
        {
            return new PointResolution(false, null, null,
                $"读取当前选择失败（{ex.GetType().Name}：{ex.Message}）。请重新选择后重试。");
        }
        PhaseLog.Write(phaseTag, $"point selection: {selected.Count} object(s)");

        var candidates = new List<(NXOpen.Point Point, string Source)>();
        var diagnoses = new List<string>();
        foreach (var obj in selected)
        {
            switch (obj)
            {
                case NXOpen.Point p:
                    candidates.Add((p, "独立点"));
                    break;
                case NXOpen.Features.PointFeature pf:
                {
                    try
                    {
                        var pts = pf.GetEntities()
                            .OfType<NXOpen.Point>()
                            .Where(pt => !IsDead(pt))
                            .ToList();
                        if (pts.Count == 1)
                            candidates.Add((pts[0], "点特征(PointFeature)"));
                        else
                            diagnoses.Add($"点特征 {SafeFeatureObjectId(pf)} 解析出 {pts.Count} 个点（预期 1）");
                    }
                    catch (Exception ex)
                    {
                        diagnoses.Add($"点特征 {SafeFeatureObjectId(pf)} 无法读取：{ex.Message}");
                    }
                    break;
                }
                case Sketch sketch:
                {
                    try
                    {
                        var pts = sketch.GetAllGeometry()
                            .OfType<NXOpen.Point>()
                            .Where(pt => !IsDead(pt))
                            .ToList();
                        if (pts.Count == 1)
                            candidates.Add((pts[0], "草图点"));
                        else
                            diagnoses.Add($"草图 {SafeFeatureObjectId(sketch)} 含 {pts.Count} 个点——请明确选择目标点，不能自动挑选");
                    }
                    catch (Exception ex)
                    {
                        diagnoses.Add($"草图 {SafeFeatureObjectId(sketch)} 无法读取：{ex.Message}");
                    }
                    break;
                }
                case Face:
                    diagnoses.Add("选中的是面——点放置模式要求选中一个点；如要用面中心请改用默认模式");
                    break;
                default:
                    diagnoses.Add($"选中的 {obj.GetType().Name} 不是可用的点");
                    break;
            }
        }

        if (candidates.Count == 0)
        {
            string diag = diagnoses.Count > 0 ? "；" + string.Join("；", diagnoses) : "";
            return new PointResolution(false, null, null,
                $"当前选择中没有可解析的点{diag}。请选中一个点（独立点、点特征或含单个点的草图）后重试。");
        }
        if (candidates.Count > 1)
        {
            return new PointResolution(false, null, null,
                $"当前选中了 {candidates.Count} 个点，无法确定孔心。请只保留一个点选择后重试。");
        }

        var (point, source) = candidates[0];
        var c = point.Coordinates;
        PhaseLog.Write(phaseTag,
            $"point resolved: source={source} coord=({c.X:F3},{c.Y:F3},{c.Z:F3})");
        return new PointResolution(true, point, source, null);
    }

    /// <summary>
    /// Finds the UNIQUE solid planar face of the work part's bodies that
    /// contains the given point — inside-face verification, not just
    /// distance-zero:
    ///   1. the face belongs to a live solid Body (sheet bodies excluded);
    ///   2. the face is planar;
    ///   3. the point lies ON the face's plane within the verified
    ///      distance tolerance (|point-to-plane| ≤ 0.01 mm, matching the
    ///      section/chaining tolerance used by every committed tool);
    ///   4. the point lies WITHIN the face's finite UV range (projected UV
    ///      inside [umin,umax]×[vmin,vmax] with a boundary slack of the same
    ///      tolerance) — a point on the infinite plane but off the actual
    ///      face surface is rejected;
    ///   5. a point near edges/vertices of MULTIPLE qualifying faces is
    ///      refused (ambiguous placement face) — never silently picked.
    /// Zero matches → refused; one match → returned; several → refused.
    /// </summary>
    private static PointFaceResolution FindUniqueSolidPlanarFaceContaining(
        Part workPart, Point3d point, string phaseTag)
    {
        const double Tolerance = 0.01;
        var matches = new List<Face>();
        var planarFaces = new List<(Face Face, Point3d Center, Vector3d Normal, double UMin, double UMax, double VMin, double VMax)>();
        try
        {
            var ufSession = UFSession.GetUFSession();
            foreach (object item in workPart.Bodies)
            {
                if (item is not Body body || body.IsSheetBody || IsDead(body))
                    continue;
                foreach (Face face in body.GetFaces())
                {
                    try
                    {
                        if (face.SolidFaceType.ToString().ToLowerInvariant() != "planar")
                            continue;
                        double[] uv = new double[4];
                        ufSession.Modl.AskFaceUvMinmax(face.Tag, uv);
                        double[] fc = new double[3];
                        double[] du = new double[3], dv = new double[3], duu = new double[3], dvv = new double[3];
                        double[] n = new double[3], rr = new double[2];
                        ufSession.Modl.AskFaceProps(face.Tag,
                            new double[] { (uv[0] + uv[1]) / 2.0, (uv[2] + uv[3]) / 2.0 },
                            fc, du, dv, duu, dvv, n, rr);
                        planarFaces.Add((face,
                            new Point3d(fc[0], fc[1], fc[2]),
                            new Vector3d(n[0], n[1], n[2]),
                            uv[0], uv[1], uv[2], uv[3]));
                    }
                    catch
                    {
                        // unreadable face: skipped, not fatal
                    }
                }
            }
        }
        catch (Exception ex)
        {
            return new PointFaceResolution(false, null,
                $"扫描实体平面时发生异常（{ex.GetType().Name}：{ex.Message}）。未执行任何建模操作。");
        }
        PhaseLog.Write(phaseTag, $"point-face scan: {planarFaces.Count} planar solid faces");

        // Distance-to-plane + in-plane UV containment per face. For a PLANE
        // the derivative magnitudes |du|,|dv| are constant across the face,
        // so physical size = extent × |d| and the in-plane offset converts
        // to a parameter delta by dividing by |d| (AskFaceProps midpoint
        // evaluation gives both the plane point and the constant derivatives).
        matches.Clear();
        foreach (var (face, fc, n, uMin, uMax, vMin, vMax) in planarFaces)
        {
            var normal = VecNormalize(n);
            var toPoint = new Vector3d(point.X - fc.X, point.Y - fc.Y, point.Z - fc.Z);
            double dist = Math.Abs(toPoint.X * normal.X + toPoint.Y * normal.Y + toPoint.Z * normal.Z);
            if (dist > Tolerance)
                continue;
            try
            {
                var ufSession = UFSession.GetUFSession();
                double[] fc2 = new double[3];
                double[] du = new double[3], dv = new double[3], duu = new double[3], dvv = new double[3];
                double[] nn = new double[3], rr = new double[2];
                // Re-evaluate at the UV midpoint for the constant derivative
                // magnitudes (plane: constant everywhere).
                ufSession.Modl.AskFaceProps(face.Tag,
                    new double[] { (uMin + uMax) / 2.0, (vMin + vMax) / 2.0 },
                    fc2, du, dv, duu, dvv, nn, rr);
                double duMag = Math.Sqrt(du[0] * du[0] + du[1] * du[1] + du[2] * du[2]);
                double dvMag = Math.Sqrt(dv[0] * dv[0] + dv[1] * dv[1] + dv[2] * dv[2]);
                if (duMag < 1e-12 || dvMag < 1e-12)
                    continue;
                var uDir = new Vector3d(du[0] / duMag, du[1] / duMag, du[2] / duMag);
                var vDir = new Vector3d(dv[0] / dvMag, dv[1] / dvMag, dv[2] / dvMag);
                // In-plane offset components (the normal component is ~0 by
                // the distance gate): physical lengths along each axis.
                double offU = toPoint.X * uDir.X + toPoint.Y * uDir.Y + toPoint.Z * uDir.Z;
                double offV = toPoint.X * vDir.X + toPoint.Y * vDir.Y + toPoint.Z * vDir.Z;
                // Face physical size along each axis = extent × derivative
                // magnitude (plane: constant derivative). Convert the offset
                // to a PARAMETER fraction of the extents.
                double sizeU = (uMax - uMin) * duMag;
                double sizeV = (vMax - vMin) * dvMag;
                // Parameter delta from the midpoint, in parameter units:
                // paramU = offU / duMag (offset length ÷ parameter speed).
                double paramU = (uMin + uMax) / 2.0 + offU / duMag;
                double paramV = (vMin + vMax) / 2.0 + offV / dvMag;
                bool insideU = paramU >= uMin - Tolerance / Math.Max(duMag, 1e-12) &&
                               paramU <= uMax + Tolerance / Math.Max(duMag, 1e-12);
                bool insideV = paramV >= vMin - Tolerance / Math.Max(dvMag, 1e-12) &&
                               paramV <= vMax + Tolerance / Math.Max(dvMag, 1e-12);
                PhaseLog.Write(phaseTag,
                    $"point-face check: face={face.JournalIdentifier} dist={dist:G4} " +
                    $"paramU=({paramU:G4} in [{uMin:G4},{uMax:G4}]) paramV=({paramV:G4} in [{vMin:G4},{vMax:G4}]) " +
                    $"inside={insideU && insideV} (size {sizeU:G4}×{sizeV:G4})");
                if (insideU && insideV)
                    matches.Add(face);
            }
            catch
            {
                // unreadable frame: skip this face
            }
        }

        if (matches.Count == 0)
        {
            return new PointFaceResolution(false, null,
                "选定点不在任何实体平面的有效范围内（点可能超出实体表面边界，或不在合格的平面上）。" +
                "未创建任何几何；请检查点的位置或改用选面中心模式。");
        }
        if (matches.Count > 1)
        {
            return new PointFaceResolution(false, null,
                $"选定点位于 {matches.Count} 个实体平面的交界处（如边线/顶点附近），放置面无法唯一确定。" +
                "请把点移到某个面的内部，或改用选面中心模式后重试。");
        }
        var matched = matches[0];
        var ownerBody = matched.GetBody();
        PhaseLog.Write(phaseTag,
            $"point-face MATCH: face={matched.JournalIdentifier} body={ownerBody.JournalIdentifier}");
        return new PointFaceResolution(true, matched, null);
    }

    /// <summary>
    /// Resolves a face reference (exact JournalIdentifier) to a live face in
    /// the work part. Logs the miss with the part's face inventory (bounded).
    /// </summary>
    private static Face? FindFaceByRef(Part workPart, string faceRef)
    {
        var faces = new List<Face>();
        foreach (object item in workPart.Bodies)
        {
            if (item is not Body body)
                continue;
            foreach (Face face in body.GetFaces())
                faces.Add(face);
        }

        foreach (var face in faces)
        {
            if (string.Equals(face.JournalIdentifier, faceRef, StringComparison.Ordinal))
                return face;
        }

        string inventory = string.Join("、", faces.Take(6).Select(f => SafeFeatureObjectId(f)));
        PhaseLog.Write("execute.hole",
            $"face ref MISS: requested={faceRef} faces=[{inventory}{(faces.Count > 6 ? " …" : "")}]");
        return null;
    }

    private static Feature? FindHoleFeatureByRef(Part workPart, string featureRef)
    {
        foreach (object item in workPart.Features.ToArray())
        {
            if (item is Feature feature &&
                string.Equals(feature.JournalIdentifier, featureRef, StringComparison.Ordinal))
            {
                return feature;
            }
        }
        return null;
    }

    // ------------------------------------------------------------------
    // Standard holes (Phase 9.2): threaded / simple screw clearance /
    // counterbored / countersunk — parameters from the NX modeling-standards
    // XML tables, integrated from the live-proven probes:
    //   - threaded: E:\Templates\ThreadedHoleTableMultiProbe.cs (9/9 specs
    //     verified in NX2512) — SizeOfHole=Custom + TypeOfHole=ThreadedHole,
    //     ThreadStandard/ThreadSize/RadialEngageOption raw strings from the
    //     record, 11 expression syncs (SetFormula + readback ±0.001 gate),
    //     depths as user values gated thread < hole.
    //   - clearance families: %TEMP%\threaded_hole_probe\*.cs (M6 verified,
    //     full-field sync = the "螺纹表未找到" warning fix) —
    //     SizeOfHole=ScrewClearance + TypeOfHole per family, ScrewType/
    //     ScrewSize/ScrewStandard/ScrewFitOption strings, ALL record
    //     expressions synced (never only name+size+diameter).
    // Table lookup (Core, unit-tested offline) resolves the UNIQUE record or
    // refuses; the executor reads the REAL installed XML (UGII_ROOT_DIR,
    // then the baseline install path) and applies the same gates.
    // ------------------------------------------------------------------

    private static string? _threadTableXml;
    private static string? _clearanceTableXml;

    /// <summary>Reads a standards XML once per NX session. UGII_ROOT_DIR is
    /// authoritative; the baseline install path is the fallback. A missing
    /// file yields null → the tool refuses with a structured message.</summary>
    private static string? LoadStandardTableXml(string fileName)
    {
        string? root = Environment.GetEnvironmentVariable("UGII_ROOT_DIR");
        if (!string.IsNullOrWhiteSpace(root))
        {
            string candidate = Path.Combine(
                root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                "ugii", "modeling_standards", fileName);
            if (File.Exists(candidate))
                return File.ReadAllText(candidate);
        }
        string fallback = Path.Combine(
            @"D:\Program Files\Siemens\DesigncenterNX2512",
            "UGII", "modeling_standards", fileName);
        if (File.Exists(fallback))
            return File.ReadAllText(fallback);
        return null;
    }

    public CreateBlockExecutionResult ExecuteCreateStandardHole(
        CreateStandardHoleParameters parameters, string? expectedWorkPartFullPath)
    {
        string familyText = parameters.Family switch
        {
            StandardHoleFamily.Threaded => "螺纹孔",
            StandardHoleFamily.SimpleClearance => "螺纹间隙孔",
            StandardHoleFamily.Counterbored => "沉头孔",
            _ => "埋头孔",
        };
        string phaseTag = "execute.std_hole";
        PhaseLog.Write(phaseTag,
            $"begin: family={parameters.Family} size={parameters.Size} " +
            $"face={parameters.PlacementFaceRef} u={parameters.LocationU:0.###} v={parameters.LocationV:0.###} " +
            $"holeDepth={(parameters.HoleDepthMm?.ToString("0.###") ?? "(standard)")} " +
            $"threadDepth={(parameters.ThreadDepthMm?.ToString("0.###") ?? "(standard)")} " +
            $"expectedPart={expectedWorkPartFullPath ?? "(null)"}");

        if (!TryResolveWorkPart(expectedWorkPartFullPath, familyText,
                out Session session, out Part workPart, out string workPartIdentity,
                out CreateBlockExecutionResult? failure))
        {
            PhaseLog.Write(phaseTag, "REFUSED: work part resolution failed");
            LogFailure(new InvalidOperationException("标准孔前置失败：工作部件解析失败。"),
                workPartIdentity: "(unknown)", operation: "CreateStandardHole");
            return failure!;
        }

        // ---- Standard table resolution (BEFORE any NX mutation) ----
        StandardHoleRecord record;
        try
        {
            string? xml = parameters.Family == StandardHoleFamily.Threaded
                ? _threadTableXml ??= LoadStandardTableXml(StandardHoleTable.ThreadStandardFile)
                : _clearanceTableXml ??= LoadStandardTableXml(StandardHoleTable.ClearanceStandardFile);
            if (xml == null)
            {
                string fileName = parameters.Family == StandardHoleFamily.Threaded
                    ? StandardHoleTable.ThreadStandardFile
                    : StandardHoleTable.ClearanceStandardFile;
                PhaseLog.Write(phaseTag, $"REFUSED: standards XML not found: {fileName}");
                return CreateBlockExecutionResult.Failure(
                    ToolExecutionStatus.PreconditionsChanged,
                    $"找不到 NX 标准表文件（{fileName}）。本工具必须使用 NX 安装自带的建模标准数据，" +
                    "请确认 NX 安装完整（UGII\\modeling_standards 目录）。",
                    workPartIdentity: workPartIdentity);
            }
            record = StandardHoleTable.Resolve(
                parameters.Family, parameters.Size, _ => xml);
        }
        catch (StandardHoleLookupException ex)
        {
            PhaseLog.Write(phaseTag, $"REFUSED: {ex.Message}");
            return CreateBlockExecutionResult.Failure(
                ToolExecutionStatus.InvalidParameters, ex.Message,
                workPartIdentity: workPartIdentity);
        }
        catch (Exception ex)
        {
            PhaseLog.Write(phaseTag,
                $"ABORTED in table resolution: {ex.GetType().Name}: {ex.Message}");
            LogFailure(ex, workPartIdentity, "CreateStandardHole");
            return CreateBlockExecutionResult.Failure(
                ToolExecutionStatus.ExecutionFailed,
                $"标准表解析发生异常（{ex.GetType().Name}：{ex.Message}），未执行任何建模操作。",
                workPartIdentity: workPartIdentity);
        }
        PhaseLog.Write(phaseTag,
            $"record ok: size={record.Field("Size")} " +
            $"standard={record.Field("Standard") ?? record.Field("ThreadStd")} " +
            $"fields={record.Fields.Count}");

        // ---- Placement resolution: two modes ----
        // Face-center (original): placement ref resolves the face, the hole
        // center is the face's UV midpoint + UV offsets. Point (new): the
        // LIVE selection must resolve to exactly one point; the hole center
        // IS that point (never snapped, never projected) and the placement
        // face is the UNIQUE solid planar face containing it (inside-face
        // verification, not just distance-zero).
        var holeCenter = new Point3d(0, 0, 0);
        Face? placementFace;
        NXOpen.Point? userPoint = null;
        string pointSource = "";
        string placementDescription = "";
        if (parameters.PlacementMode == StandardHolePlacementMode.SelectedPoint)
        {
            var pointResolution = ResolveSelectedPointForHole(workPart, phaseTag);
            if (!pointResolution.Success)
            {
                PhaseLog.Write(phaseTag, $"REFUSED(point): {pointResolution.Error}");
                return CreateBlockExecutionResult.Failure(
                    ToolExecutionStatus.PreconditionsChanged,
                    pointResolution.Error!,
                    workPartIdentity: workPartIdentity);
            }
            userPoint = pointResolution.Point!;
            pointSource = pointResolution.Source!;
            holeCenter = pointResolution.Point!.Coordinates;
            placementDescription = $"选定点（{pointSource}）({holeCenter.X:0.###}, {holeCenter.Y:0.###}, {holeCenter.Z:0.###})";

            var faceResolution = FindUniqueSolidPlanarFaceContaining(workPart, holeCenter, phaseTag);
            if (!faceResolution.Success)
            {
                PhaseLog.Write(phaseTag, $"REFUSED(point-face): {faceResolution.Error}");
                return CreateBlockExecutionResult.Failure(
                    ToolExecutionStatus.PreconditionsChanged,
                    faceResolution.Error!,
                    workPartIdentity: workPartIdentity);
            }
            placementFace = faceResolution.Face!;
        }
        else
        {
            try
            {
                placementFace = FindFaceByRef(workPart, parameters.PlacementFaceRef);
            }
            catch (Exception ex)
            {
                PhaseLog.Write(phaseTag,
                    $"ABORTED in face resolution: {ex.GetType().Name}: {ex.Message}");
                LogFailure(ex, workPartIdentity, "CreateStandardHole");
                return CreateBlockExecutionResult.Failure(
                    ToolExecutionStatus.ExecutionFailed,
                    $"放置面解析发生异常（{ex.GetType().Name}：{ex.Message}），未执行任何建模操作。",
                    workPartIdentity: workPartIdentity);
            }
            if (placementFace == null)
            {
                PhaseLog.Write(phaseTag,
                    $"REFUSED: placement face {parameters.PlacementFaceRef} not resolvable");
                LogFailure(new InvalidOperationException(
                        $"标准孔前置失败：放置面引用 {parameters.PlacementFaceRef} 无法在当前工作部件解析。"),
                    workPartIdentity, "CreateStandardHole");
                return CreateBlockExecutionResult.Failure(
                    ToolExecutionStatus.PreconditionsChanged,
                    $"放置面引用 {parameters.PlacementFaceRef} 在当前工作部件中不存在。请让用户选中目标平面后重试。",
                    workPartIdentity: workPartIdentity);
            }
        }

        // Planar gate + frame (the verified UF_MODL read path) — both modes:
        // face-center mode additionally derives the hole center at the face
        // UV midpoint + UV offsets; point mode keeps the resolved point.
        double[] uv = new double[4];
        double[] center = new double[3];
        double[] u1 = new double[3], v1 = new double[3], u2 = new double[3], v2 = new double[3];
        double[] unitNorm = new double[3];
        double[] radii = new double[2];
        try
        {
            string faceType = placementFace.SolidFaceType.ToString().ToLowerInvariant();
            if (faceType != "planar")
            {
                PhaseLog.Write(phaseTag, $"REFUSED: face is {faceType}, not planar");
                return CreateBlockExecutionResult.Failure(
                    ToolExecutionStatus.PreconditionsChanged,
                    $"孔的放置面必须是平面（当前面类型：{faceType}）。请在平面上打孔。",
                    workPartIdentity: workPartIdentity);
            }
            var ufSession = UFSession.GetUFSession();
            ufSession.Modl.AskFaceUvMinmax(placementFace.Tag, uv);
            ufSession.Modl.AskFaceProps(placementFace.Tag,
                new double[] { (uv[0] + uv[1]) / 2.0, (uv[2] + uv[3]) / 2.0 },
                center, u1, v1, u2, v2, unitNorm, radii);
        }
        catch (Exception ex)
        {
            PhaseLog.Write(phaseTag,
                $"ABORTED in face frame read: {ex.GetType().Name}: {ex.Message}");
            LogFailure(ex, workPartIdentity, "CreateStandardHole");
            return CreateBlockExecutionResult.Failure(
                ToolExecutionStatus.ExecutionFailed,
                $"读取放置面几何发生异常（{ex.GetType().Name}：{ex.Message}），未执行任何建模操作。",
                workPartIdentity: workPartIdentity);
        }

        if (parameters.PlacementMode == StandardHolePlacementMode.SelectedFaceCenter)
        {
            var uAxis = VecNormalize(new Vector3d(u1[0], u1[1], u1[2]));
            var vAxis = VecNormalize(new Vector3d(v1[0], v1[1], v1[2]));
            holeCenter = new Point3d(
                center[0] + parameters.LocationU * uAxis.X + parameters.LocationV * vAxis.X,
                center[1] + parameters.LocationU * uAxis.Y + parameters.LocationV * vAxis.Y,
                center[2] + parameters.LocationU * uAxis.Z + parameters.LocationV * vAxis.Z);
            placementDescription = $"选中面 {parameters.PlacementFaceRef} 的中心";
        }
        PhaseLog.Write(phaseTag,
            $"placement ok: mode={parameters.PlacementMode} face={placementFace.JournalIdentifier} " +
            $"center=({holeCenter.X:F3},{holeCenter.Y:F3},{holeCenter.Z:F3}) " +
            (userPoint != null ? $"pointSource={pointSource} " : "") +
            $"desc={placementDescription}");

        int bodiesBefore;
        try { bodiesBefore = CountBodies(workPart); }
        catch (Exception ex)
        {
            PhaseLog.Write(phaseTag,
                $"ABORTED in pre-state snapshot: {ex.GetType().Name}: {ex.Message}");
            LogFailure(ex, workPartIdentity, "CreateStandardHole");
            return CreateBlockExecutionResult.Failure(
                ToolExecutionStatus.ExecutionFailed,
                $"标准孔前置快照发生异常（{ex.GetType().Name}：{ex.Message}），未执行任何建模操作。",
                workPartIdentity: workPartIdentity);
        }

        NXOpen.Body targetBody = placementFace.GetBody();
        HolePackageBuilder? builder = null;
        Session.UndoMarkId markId = default;
        bool markCreated = false;
        // Every expression sync must verify against its record value; every
        // readback mismatch aborts BEFORE commit — the exact probe discipline
        // that eliminated the "螺纹表未找到" warning class.
        bool valuesOk = true;
        var synced = new List<string>();
        try
        {
            markId = session.SetUndoMark(
                Session.MarkVisibility.Visible, $"NXAI 创建{familyText} {parameters.Size}");
            markCreated = true;
            PhaseLog.Write(phaseTag, $"undo mark created: NXAI 创建{familyText} {parameters.Size}");

            builder = workPart.Features.CreateHolePackageBuilder(null!);
            if (parameters.Family == StandardHoleFamily.Threaded)
            {
                // Threaded path (live-proven 9/9): Custom + ThreadedHole +
                // string triple + expression syncs; depths user-or-standard.
                builder.SizeOfHole = HolePackageBuilder.SizesOfHole.Custom;
                builder.TypeOfHole = HolePackageBuilder.TypesOfHole.ThreadedHole;
                builder.ThreadStandard = record.Field("Standard")!;
                builder.ThreadSize = record.Field("Size")!;
                builder.RadialEngageOption = record.Field("RadialEngage")!;
                PhaseLog.Write(phaseTag,
                    $"threaded strings: std='{builder.ThreadStandard}' size='{builder.ThreadSize}' engage='{builder.RadialEngageOption}'");

                // Reference depths: the standard record's own values; the
                // parser has already gated user thread < user hole.
                double holeDepth = parameters.HoleDepthMm
                    ?? ParseStandardNumber(record.Field("HoleDepth"), "HoleDepth", phaseTag, ref valuesOk);
                double threadDepth = parameters.ThreadDepthMm
                    ?? ParseStandardNumber(record.Field("ThreadDepth"), "ThreadDepth", phaseTag, ref valuesOk);
                if (valuesOk && threadDepth >= holeDepth)
                {
                    PhaseLog.Write(phaseTag, $"REFUSED: threadDepth {threadDepth} >= holeDepth {holeDepth}");
                    return CreateBlockExecutionResult.Failure(
                        ToolExecutionStatus.InvalidParameters,
                        $"螺纹深度（{threadDepth:0.###} 毫米）必须小于孔深（{holeDepth:0.###} 毫米），本次操作已拒绝。",
                        workPartIdentity: workPartIdentity);
                }

                // ALL direct counterpart fields from the record (the probe's
                // full sync set — depths handled above, NOT re-synced here).
                SyncExpr(builder, b => b.TapDrillDiameter, "TapDrillDia", record, phaseTag, synced, ref valuesOk);
                SyncExpr(builder, b => b.ThreadedStartChamferDiameter, "StartChamferDiameter", record, phaseTag, synced, ref valuesOk);
                SyncExpr(builder, b => b.ThreadedStartChamferAngle, "StartChamferAngle", record, phaseTag, synced, ref valuesOk);
                SyncExpr(builder, b => b.ThreadedEndChamferDiameter, "EndChamferDiameter", record, phaseTag, synced, ref valuesOk);
                SyncExpr(builder, b => b.ThreadedEndChamferAngle, "EndChamferAngle", record, phaseTag, synced, ref valuesOk);
                SyncExpr(builder, b => b.ThreadedReliefDiameter, "ReliefDiameter", record, phaseTag, synced, ref valuesOk);
                SyncExpr(builder, b => b.ThreadedReliefDepth, "ReliefDepth", record, phaseTag, synced, ref valuesOk);
                SyncExpr(builder, b => b.ThreadedReliefAngle, "ReliefTipAngle", record, phaseTag, synced, ref valuesOk);
                SyncExpr(builder, b => b.ThreadedReliefChamferAngle, "ReliefChamferAngle", record, phaseTag, synced, ref valuesOk);
                SyncExpr(builder, b => b.ThreadedReliefChamferOffset, "ReliefChamferOffset", record, phaseTag, synced, ref valuesOk);
                SyncExpr(builder, b => b.ThreadedTipAngle, "HoleTipAngle", record, phaseTag, synced, ref valuesOk);
                SetExprValue(builder.ThreadedHoleDepth, "ThreadedHoleDepth", holeDepth, phaseTag, ref valuesOk);
                SetExprValue(builder.ThreadDepth, "ThreadDepth", threadDepth, phaseTag, ref valuesOk);
            }
            else
            {
                // Clearance-family path (live-proven M6): ScrewClearance +
                // per-family TypeOfHole + 4 strings + full expression sync.
                builder.SizeOfHole = HolePackageBuilder.SizesOfHole.ScrewClearance;
                builder.TypeOfHole = parameters.Family switch
                {
                    StandardHoleFamily.Counterbored => HolePackageBuilder.TypesOfHole.Counterbored,
                    StandardHoleFamily.Countersunk => HolePackageBuilder.TypesOfHole.Countersink,
                    _ => HolePackageBuilder.TypesOfHole.Simple,
                };
                PhaseLog.Write(phaseTag, $"type of hole: {builder.TypeOfHole}");

                SetStr(builder, b => b.ScrewType, "ScrewType", "ScrewType", record, phaseTag, ref valuesOk);
                SetStr(builder, b => b.ScrewSize, "ScrewSize", "Size", record, phaseTag, ref valuesOk);
                // ScrewStandard ← the table's Standard field ('ISO') — the
                // live-proven requirement for CommitFeature's lookup.
                SetStr(builder, b => b.ScrewStandard, "ScrewStandard", "Standard", record, phaseTag, ref valuesOk);
                SetStr(builder, b => b.ScrewFitOption, "ScrewFitOption", "Fit", record, phaseTag, ref valuesOk);

                SyncExpr(builder, b => b.ScrewClearanceHoleDiameter, "HoleDiameter", record, phaseTag, synced, ref valuesOk);
                SyncExpr(builder, b => b.ScrewClearanceTipAngle, "HoleTipAngle", record, phaseTag, synced, ref valuesOk);
                SyncExpr(builder, b => b.ScrewClearanceStartChamferAngle, "StartChamferAngle", record, phaseTag, synced, ref valuesOk);
                SyncExpr(builder, b => b.ScrewClearanceStartChamferOffset, "StartChamferOffset", record, phaseTag, synced, ref valuesOk);
                SyncExpr(builder, b => b.ScrewClearanceEndChamferAngle, "EndChamferAngle", record, phaseTag, synced, ref valuesOk);
                SyncExpr(builder, b => b.ScrewClearanceEndChamferOffset, "EndChamferOffset", record, phaseTag, synced, ref valuesOk);
                if (parameters.Family == StandardHoleFamily.Counterbored)
                {
                    SyncExpr(builder, b => b.ScrewClearanceCounterboreDiameter, "CBoreDia", record, phaseTag, synced, ref valuesOk);
                    SyncExpr(builder, b => b.ScrewClearanceCounterboreDepth, "CBoreDepth", record, phaseTag, synced, ref valuesOk);
                    SyncExpr(builder, b => b.ScrewClearanceNeckChamferOffset, "NeckChamferOffset", record, phaseTag, synced, ref valuesOk);
                    SyncExpr(builder, b => b.ScrewClearanceNeckChamferAngle, "NeckChamferAngle", record, phaseTag, synced, ref valuesOk);
                    // Optional hole depth: the probe's counterbore used the
                    // family default; an explicit user depth (parser-allowed
                    // only for threaded/simple) is NOT applied here.
                }
                else if (parameters.Family == StandardHoleFamily.Countersunk)
                {
                    SyncExpr(builder, b => b.ScrewClearanceCountersinkDiameter, "CSinkDia", record, phaseTag, synced, ref valuesOk);
                    SyncExpr(builder, b => b.ScrewClearanceCountersinkAngle, "CSinkAngle", record, phaseTag, synced, ref valuesOk);
                    SyncExpr(builder, b => b.ScrewClearanceReliefDepth, "CSinkReliefDepth", record, phaseTag, synced, ref valuesOk);
                }
                else // SimpleClearance
                {
                    if (parameters.HoleDepthMm is { } simpleDepth)
                        SetExprValue(builder.ScrewClearanceHoleDepth, "ScrewClearanceHoleDepth", simpleDepth, phaseTag, ref valuesOk);
                }
            }

            // ---- position section (baseline-proven point rule) ----
            Section posSection = builder.HolePosition;
            if (posSection == null)
            {
                PhaseLog.Write(phaseTag, "REFUSED: null position section");
                return CreateBlockExecutionResult.Failure(
                    ToolExecutionStatus.ExecutionFailed,
                    "孔定位截面不可用（builder.HolePosition 为空），未执行任何建模操作。",
                    workPartIdentity: workPartIdentity);
            }
            posSection.SetAllowedEntityTypes(Section.AllowTypes.OnlyPoints);
            posSection.DistanceTolerance = 0.01;
            posSection.ChainingTolerance = 0.0095;
            NXOpen.Point holePoint = workPart.Points.CreatePoint(holeCenter);
            var pointRule = workPart.ScRuleFactory.CreateRuleCurveDumbFromPoints(new NXOpen.Point[] { holePoint });
            posSection.AddToSection(
                new SelectionIntentRule[] { pointRule },
                holePoint, null!, null!,
                new Point3d(holeCenter.X, holeCenter.Y, holeCenter.Z),
                Section.Mode.Create, false);
            posSection.EvaluateAndAskOutputEntities(out var outputEntities);
            PhaseLog.Write(phaseTag, $"position outputs={outputEntities.Length}");
            if (outputEntities.Length != 1)
            {
                PhaseLog.Write(phaseTag, "REFUSED: expected 1 position output");
                return CreateBlockExecutionResult.Failure(
                    ToolExecutionStatus.ExecutionFailed,
                    $"孔定位解析得到 {outputEntities.Length} 个输出（预期 1 个），未提交任何几何。",
                    workPartIdentity: workPartIdentity);
            }

            // Pre-commit hole-FORM readback gate: the builder must still carry
            // the requested NX hole type — a silent TypeOfHole flip (e.g. a
            // string assignment knocking the builder back to its default)
            // would commit the WRONG geometry shape while everything else
            // reads back clean. This is the 2026-10-23 "埋头孔 came out
            // cylindrical" class of failure, guarded structurally.
            var expectedType = parameters.Family switch
            {
                StandardHoleFamily.Threaded => HolePackageBuilder.TypesOfHole.ThreadedHole,
                StandardHoleFamily.SimpleClearance => HolePackageBuilder.TypesOfHole.Simple,
                StandardHoleFamily.Counterbored => HolePackageBuilder.TypesOfHole.Counterbored,
                _ => HolePackageBuilder.TypesOfHole.Countersink,
            };
            if (builder.TypeOfHole != expectedType)
            {
                PhaseLog.Write(phaseTag,
                    $"REFUSED: TypeOfHole readback={builder.TypeOfHole}, expected={expectedType}");
                return CreateBlockExecutionResult.Failure(
                    ToolExecutionStatus.VerificationFailed,
                    $"孔型读回不一致（builder 实际为 {builder.TypeOfHole}，请求为 {expectedType}），未提交任何几何。",
                    workPartIdentity: workPartIdentity);
            }
            PhaseLog.Write(phaseTag, $"type readback OK: {builder.TypeOfHole}");

            builder.Tolerance = 0.01;
            var boolOp = builder.BooleanOperation;
            boolOp.Type = NXOpen.GeometricUtilities.BooleanOperation.BooleanType.Subtract;
            boolOp.SetTargetBodies(new NXOpen.Body[] { targetBody });
            PhaseLog.Write(phaseTag,
                $"boolean=Subtract target={targetBody.JournalIdentifier}");

            if (!valuesOk)
            {
                PhaseLog.Write(phaseTag, "REFUSED: readback gates failed — NOT committing");
                return CreateBlockExecutionResult.Failure(
                    ToolExecutionStatus.VerificationFailed,
                    "标准孔参数与标准表读回不一致（读回门失败），未提交任何几何。" +
                    "已同步字段：" + string.Join("、", synced) + "。",
                    workPartIdentity: workPartIdentity);
            }

            bool validateOk = builder.Validate();
            PhaseLog.Write(phaseTag, $"validate={validateOk}");
            if (!validateOk)
            {
                return CreateBlockExecutionResult.Failure(
                    ToolExecutionStatus.VerificationFailed,
                    "标准孔 builder 校验未通过（Validate=false），未提交任何几何。请检查放置面与规格。",
                    workPartIdentity: workPartIdentity);
            }

            var committedObj = builder.CommitFeature();
            var committedFeature = committedObj as Feature;
            session.UpdateManager.DoUpdate(markId);
            PhaseLog.Write(phaseTag,
                $"commit returned: {(committedObj != null ? committedObj.GetType().Name + " " + SafeFeatureObjectId(committedObj) : "null")} " +
                $"type={committedFeature?.FeatureType ?? "?"} update=ok");

            // ---- postcondition (facts from NX; verdict below) ----
            string? featureType = null;
            bool featureAlive = false;
            if (committedFeature != null)
            {
                try
                {
                    featureAlive = !IsDead(committedFeature);
                    featureType = committedFeature.FeatureType;
                }
                catch (Exception ex)
                {
                    PhaseLog.Write(phaseTag, $"feature read FAILED: {ex.GetType().Name}: {ex.Message}");
                }
            }
            int bodiesAfter = CountBodies(workPart);
            PhaseLog.Write(phaseTag,
                $"feature evidence: alive={featureAlive} type={featureType} bodyCount={bodiesBefore}→{bodiesAfter}");

            // Verdict: alive feature + unchanged body count (subtractive
            // in-place edit) + readable identity round-trip. Same three-part
            // proof as create_hole; unverifiable results roll back.
            string? roundTrip = committedFeature != null
                ? FindHoleFeatureByRef(workPart, SafeFeatureObjectId(committedFeature)) != null
                    ? SafeFeatureObjectId(committedFeature)
                    : null
                : null;
            if (committedFeature == null || !featureAlive || featureType == null ||
                bodiesAfter != bodiesBefore || roundTrip == null)
            {
                (bool undone, string undoError) = Rollback(session, markId, $"NXAI 创建{familyText}");
                LogFailure(new InvalidOperationException(
                        $"Standard hole postcondition failed: alive={featureAlive} type={featureType} " +
                        $"bodies {bodiesBefore}→{bodiesAfter} roundTrip={(roundTrip != null)}"),
                    workPartIdentity, "CreateStandardHole");
                return CreateBlockExecutionResult.Failure(
                    ToolExecutionStatus.VerificationFailed,
                    $"{familyText}创建后验证失败：无法证明特征已正确提交。" +
                    (undone ? "已回滚本次操作。" : $"回滚未完成：{undoError}。请在 NX 撤销列表中手动撤销。"),
                    rolledBack: undone, workPartIdentity: workPartIdentity);
            }

            string details = JsonSerializer.Serialize(new Dictionary<string, object?>
            {
                ["featureRef"] = roundTrip,
                ["featureType"] = featureType,
                ["family"] = parameters.Family.ToString(),
                ["nxHoleType"] = expectedType.ToString(),
                ["size"] = parameters.Size,
                ["standardFields"] = synced,
                ["placementFaceRef"] = placementFace.JournalIdentifier,
                ["holeCenter"] = new[] { holeCenter.X, holeCenter.Y, holeCenter.Z },
                ["bodyCountUnchanged"] = true,
            });
            // The success message names the REAL NX hole form (not just the
            // user's word) so the record can't conflate 沉头孔/埋头孔 with
            // either builder family.
            string formText = expectedType switch
            {
                HolePackageBuilder.TypesOfHole.Countersink => "锥形埋头孔（Countersink，90° 锥面）",
                HolePackageBuilder.TypesOfHole.Counterbored => "圆柱沉头孔（Counterbored，平底台阶）",
                HolePackageBuilder.TypesOfHole.ThreadedHole => "螺纹孔（ThreadedHole）",
                _ => "简单孔（Simple）",
            };
            return CreateBlockExecutionResult.Success(
                parameters.LocationU, parameters.LocationV, bodiesAfter,
                $"已创建{familyText}——NX 孔型 {formText}（规格 {parameters.Size}，特征 {roundTrip}，" +
                $"标准字段 {synced.Count} 项已按标准表同步，孔心 " +
                $"({holeCenter.X:0.###}, {holeCenter.Y:0.###}, {holeCenter.Z:0.###})）。",
                workPartIdentity, details);
        }
        catch (Exception ex)
        {
            PhaseLog.Write(phaseTag,
                $"EXCEPTION: {ex.GetType().Name}: {ex.Message} (markCreated={markCreated})");
            (bool undone, string undoError) = markCreated
                ? Rollback(session, markId, $"NXAI 创建{familyText}")
                : (false, "撤销标记尚未创建，无需回滚");
            LogFailure(ex, workPartIdentity, markCreated ? "CreateStandardHole" : "CreateStandardHole-premark");
            return CreateBlockExecutionResult.Failure(
                ToolExecutionStatus.ExecutionFailed,
                $"{ex.GetType().Name}: {ex.Message}" +
                (undone ? "（本次操作已回滚，部件恢复原状。）" : $"（回滚未完成：{undoError}。请在 NX 撤销列表中手动撤销。）"),
                rolledBack: undone, workPartIdentity: workPartIdentity);
        }
        finally
        {
            if (builder != null)
            {
                try
                {
                    builder.Destroy();
                    PhaseLog.Write(phaseTag, "builder destroyed");
                }
                catch (Exception destroyEx)
                {
                    PhaseLog.Write(phaseTag,
                        $"builder destroy FAILED: {destroyEx.GetType().Name}: {destroyEx.Message}");
                }
            }
        }
    }

    private static double ParseStandardNumber(string? raw, string field, string phaseTag, ref bool ok)
    {
        if (raw != null &&
            double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out double v))
        {
            return v;
        }
        PhaseLog.Write(phaseTag, $"GATE: standard field {field} missing/unparseable (raw='{raw}')");
        ok = false;
        return double.NaN;
    }

    /// <summary>Syncs ONE builder expression from its XML counterpart field
    /// with the probe's four-step gate: exists → non-null → SetFormula →
    /// readback ±0.001. Field missing from the record = skipped (builder
    /// default kept, logged); parse/throw/readback mismatch = gate FAIL.</summary>
    private static void SyncExpr(
        HolePackageBuilder builder, Func<HolePackageBuilder, NXOpen.Expression> get,
        string xmlField, StandardHoleRecord record, string phaseTag,
        List<string> synced, ref bool ok)
    {
        string? raw = record.Field(xmlField);
        if (raw == null)
        {
            PhaseLog.Write(phaseTag, $"sync {xmlField}: NOT PRESENT IN RECORD — skipped (builder default kept)");
            return;
        }
        if (!double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out double xmlVal))
        {
            PhaseLog.Write(phaseTag, $"sync {xmlField}: raw='{raw}' PARSE FAILED");
            ok = false;
            return;
        }
        try
        {
            var expr = get(builder);
            if (expr == null)
            {
                PhaseLog.Write(phaseTag, $"sync {xmlField}: builder expression is null — GATE FAIL");
                ok = false;
                return;
            }
            expr.SetFormula(xmlVal.ToString("R", CultureInfo.InvariantCulture));
            double delta = expr.Value - xmlVal;
            PhaseLog.Write(phaseTag,
                $"sync-OK {xmlField}: raw='{raw}' expr={expr.Name} readback={expr.Value.ToString("G17", CultureInfo.InvariantCulture)} delta={delta.ToString("G17", CultureInfo.InvariantCulture)}");
            if (Math.Abs(delta) > 0.001)
            {
                PhaseLog.Write(phaseTag, $"sync-GATE {xmlField}: OUT OF TOLERANCE");
                ok = false;
                return;
            }
            synced.Add(xmlField);
        }
        catch (Exception ex)
        {
            PhaseLog.Write(phaseTag, $"sync {xmlField}: FAILED ({ex.GetType().Name}: {ex.Message})");
            ok = false;
        }
    }

    /// <summary>Sets ONE explicit value (user depth) with the same readback gate.</summary>
    private static void SetExprValue(
        NXOpen.Expression expr, string what, double value, string phaseTag, ref bool ok)
    {
        if (expr == null)
        {
            PhaseLog.Write(phaseTag, $"set {what}: expression is null — GATE FAIL");
            ok = false;
            return;
        }
        try
        {
            expr.SetFormula(value.ToString("R", CultureInfo.InvariantCulture));
            double delta = expr.Value - value;
            PhaseLog.Write(phaseTag,
                $"set-OK {what}: value={value.ToString("R", CultureInfo.InvariantCulture)} readback={expr.Value.ToString("G17", CultureInfo.InvariantCulture)}");
            if (Math.Abs(delta) > 0.001)
            {
                PhaseLog.Write(phaseTag, $"set-GATE {what}: OUT OF TOLERANCE");
                ok = false;
            }
        }
        catch (Exception ex)
        {
            PhaseLog.Write(phaseTag, $"set {what}: FAILED ({ex.GetType().Name}: {ex.Message})");
            ok = false;
        }
    }

    /// <summary>Sets ONE string parameter with an exact readback gate.</summary>
    private static void SetStr(
        HolePackageBuilder builder, Func<HolePackageBuilder, string> get,
        string what, string xmlField, StandardHoleRecord record, string phaseTag, ref bool ok)
    {
        string? raw = record.Field(xmlField);
        if (raw == null)
        {
            PhaseLog.Write(phaseTag, $"str {what} ← {xmlField}: NOT PRESENT IN RECORD");
            ok = false;
            return;
        }
        try
        {
            // Read the CURRENT value to detect a no-op assignment: some
            // builders keep the old value when the assigned token is not a
            // valid option — indistinguishable from a silent failure
            // without a pre/post compare.
            string before = get(builder);
            string after = before == raw ? raw : raw; // assign below
            switch (what)
            {
                case "ScrewType": builder.ScrewType = raw; break;
                case "ScrewSize": builder.ScrewSize = raw; break;
                case "ScrewFitOption": builder.ScrewFitOption = raw; break;
                case "ScrewStandard": builder.ScrewStandard = raw; break;
            }
            string readback = get(builder);
            bool match = string.Equals(readback, raw, StringComparison.Ordinal);
            PhaseLog.Write(phaseTag,
                $"str-OK {what}: set='{raw}' readback='{readback}' match={match}");
            if (!match)
            {
                ok = false;
            }
        }
        catch (Exception ex)
        {
            PhaseLog.Write(phaseTag, $"str {what}: FAILED ({ex.GetType().Name}: {ex.Message})");
            ok = false;
        }
    }
}
