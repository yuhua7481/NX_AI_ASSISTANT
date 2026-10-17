using System.Globalization;
using System.Text.Json;
using NXAI.Assistant.Core;
using NXOpen;
using NXOpen.Features;
using NXOpen.GeometricUtilities;
using NXOpen.UF;

namespace NXAI.Assistant.NxHost.NX2512;

/// <summary>
/// Sketch / extrude / revolve / boolean executors (Phase 7), split out of
/// BlockToolExecutor.cs as a partial of <see cref="NxToolExecutor"/> so all
/// geometry tools share one undo/rollback/logging implementation.
///
/// All APIs below were verified against the local NX2512 managed assembly
/// metadata and NXOpen.xml docs before use:
///   - SketchCollection.CreateSketchInPlaceBuilder2(null!)  (NX11+)
///   - SketchInPlaceBuilder.PlaneOption / PlaneReference (Plane)
///   - PlaneCollection.CreatePlane(origin, normal, UpdateOption)
///   - Sketch.Activate/Deactivate, Sketch.AddGeometry(Curve, InferConstraintsOption)
///   - CurveCollection.CreateLine(Point3d, Point3d) / CreateArc(center, xDir, yDir, r, a0, a1)
///   - SectionSets.CreateSection + ScRuleFactory.CreateRuleCurveFeature(Feature[])
///     + Section.AddToSection(rules, seed, null, null, helpPoint, Mode.Create)
///   - FeatureCollection.CreateExtrudeBuilder / CreateRevolveBuilder /
///     CreateBooleanBuilder; GeometricUtilities.Limits Start/End
///     Extend.SetValue(string); BooleanBuilder.Operation (Feature+BooleanType),
///     Targets (SelectBodyList), Tools (SelectDisplayableObjectList)
///   - AxisCollection.CreateAxis(Point3d, Vector3d, UpdateOption) → Axis
///     (DatumAxis is NOT an Axis — a fresh free axis is built from it)
///   - UFModl.AskBoundingBox(Tag, double[]) for postcondition geometry.
/// UF_MODL_ask_mass_props_3d's managed wrapper (AskMassProps3d) exists, but
/// the 47-element result-array layout is NOT verifiable from local docs, so
/// it is deliberately not used: postconditions verify against analytically
/// computed world bounding boxes instead (exact for our rectangle/circle
/// profiles), plus body/sketch count and identity diffs.
///
/// Safety contract (same as the primitive tools): current work part only,
/// no Save/SaveAs/rename, millimeter gate, visible UndoMark with rollback on
/// every failure, builder Destroy on all paths, postcondition verification
/// before reporting success, no trusting the caller's text.
/// </summary>
public sealed partial class NxToolExecutor
{
    private const double PlaneFrameToleranceMm = 0.01;

    // ------------------------------------------------------------ create_sketch

    public CreateBlockExecutionResult ExecuteCreateSketch(
        CreateSketchParameters parameters, string? expectedWorkPartFullPath)
    {
        if (parameters.ProfileType == SketchProfileType.Rectangle &&
            (parameters.RectangleWidth <= 0 || parameters.RectangleHeight <= 0))
        {
            return CreateBlockExecutionResult.Failure(
                ToolExecutionStatus.InvalidParameters,
                $"矩形宽高必须为正（{parameters.RectangleWidth} × {parameters.RectangleHeight} 毫米）。");
        }
        if (parameters.ProfileType == SketchProfileType.Circle && parameters.CircleDiameter <= 0)
        {
            return CreateBlockExecutionResult.Failure(
                ToolExecutionStatus.InvalidParameters,
                $"圆形直径必须为正（{parameters.CircleDiameter} 毫米）。");
        }

        if (!TryResolveWorkPart(expectedWorkPartFullPath, "草图",
                out Session session, out Part workPart, out string workPartIdentity,
                out CreateBlockExecutionResult? failure))
        {
            return failure!;
        }

        // Resolve the placement plane BEFORE the undo mark: selection reads
        // and plane construction must not be rolled back as model changes.
        // Phase 8.3: XZ/YZ planes additionally produce an explicit CSYS —
        // the live PhaseLog proved that NX's ExistingPlane + Plane(0,-1,0)
        // gets reoriented to the XY frame (n=(0,0,1) from sketch.Orientation),
        // so for those planes the builder's PlaneOption must be NewCsys with
        // an explicitly constructed CartesianCoordinateSystem.
        Plane? plane;
        CartesianCoordinateSystem? explicitCsys;
        string planeDescription;
        try
        {
            plane = ResolveSketchPlane(workPart, parameters.PlaneMode,
                out planeDescription, out string planeFailure);
            if (plane == null)
            {
                return CreateBlockExecutionResult.Failure(
                    ToolExecutionStatus.PreconditionsChanged, planeFailure,
                    workPartIdentity: workPartIdentity);
            }
            explicitCsys = parameters.PlaneMode switch
            {
                // isTemporary must be FALSE: a temporary CSYS is not displayed,
                // not saved, and SketchInPlaceBuilder.Commit rejects it with
                // "输入参数为空或无效" (live failure, Phase 8.4). A non-temporary
                // CSYS becomes a real datum object that the sketch can attach
                // to; it is created inside the UndoMark so a failed sketch
                // rolls it back with everything else.
                SketchPlaneMode.WcsXZ => workPart.CoordinateSystems.CreateCoordinateSystem(
                    new Point3d(0, 0, 0),
                    new Matrix3x3 { Xx = 1, Xy = 0, Xz = 0, Yx = 0, Yy = 0, Yz = 1, Zx = 0, Zy = -1, Zz = 0 },
                    false),
                SketchPlaneMode.WcsYZ => workPart.CoordinateSystems.CreateCoordinateSystem(
                    new Point3d(0, 0, 0),
                    new Matrix3x3 { Xx = 0, Xy = 0, Xz = 1, Yx = 0, Yy = 1, Yz = 0, Zx = -1, Zy = 0, Zz = 0 },
                    false),
                _ => null,
            };
            PhaseLog.Write("execute.sketch",
                $"plane: {planeDescription} csys: {(explicitCsys != null ? "explicit" : "from-plane")}");
        }
        catch (Exception ex)
        {
            return CreateBlockExecutionResult.Failure(
                ToolExecutionStatus.PreconditionsChanged,
                $"解析草图放置平面失败：{ex.Message}", workPartIdentity: workPartIdentity);
        }

        HashSet<string> sketchIdsBefore = CollectSketchIds(workPart);
        int sketchesBefore = CountSketches(workPart);

        Session.UndoMarkId markId = session.SetUndoMark(
            Session.MarkVisibility.Visible, "NXAI 创建草图");

        SketchInPlaceBuilder? sketchBuilder = null;
        try
        {
            sketchBuilder = workPart.Sketches.CreateSketchInPlaceBuilder2(null!);
            if (explicitCsys != null)
            {
                // Phase 8.3: NX reorients ExistingPlane sketches for XZ/YZ —
                // the live sketch.Orientation read n=(0,0,1) (XY frame) even
                // though the Plane normal was (0,-1,0). NewCsys with an
                // explicit CartesianCoordinateSystem forces NX to use the
                // exact axes we provide.
                sketchBuilder.PlaneOption = Sketch.PlaneOption.NewCsys;
                sketchBuilder.Csystem = explicitCsys;
            }
            else
            {
                sketchBuilder.PlaneOption = Sketch.PlaneOption.ExistingPlane;
                sketchBuilder.PlaneReference = plane;
            }

            var committed = sketchBuilder.Commit() as Sketch;
            Sketch? sketch = committed ?? FindNewSketch(workPart, sketchIdsBefore);
            if (sketch == null)
            {
                throw new InvalidOperationException("草图创建后无法在工作部件中定位新草图对象。");
            }
            PhaseLog.Write("execute.sketch", $"committed: {sketch.JournalIdentifier}");

            BuildSketchProfile(sketch, workPart, parameters);

            var verification = VerifySketchCreated(workPart, sketch, parameters,
                sketchesBefore, sketchIdsBefore);
            if (!verification.Success)
            {
                (bool undone, string undoError) = Rollback(session, markId, "NXAI 创建草图");
                LogFailure(new InvalidOperationException($"Sketch postcondition failed: {verification.Reason}"),
                    workPartIdentity, "CreateSketch");
                return CreateBlockExecutionResult.Failure(
                    ToolExecutionStatus.VerificationFailed,
                    $"草图创建后验证失败（{verification.Reason}）。" +
                    (undone ? "已回滚本次操作。" : $"回滚未完成：{undoError}。请在 NX 撤销列表中手动撤销。"),
                    rolledBack: undone, workPartIdentity: workPartIdentity);
            }

            string details = JsonSerializer.Serialize(new Dictionary<string, object?>
            {
                ["sketchRef"] = sketch.JournalIdentifier,
                ["sketchName"] = sketch.Name,
                ["plane"] = planeDescription,
            });
            return CreateBlockExecutionResult.Success(
                parameters.ProfileType == SketchProfileType.Rectangle ? parameters.RectangleWidth : parameters.CircleDiameter,
                parameters.ProfileType == SketchProfileType.Rectangle ? parameters.RectangleHeight : parameters.CircleDiameter,
                0,
                $"已在{planeDescription}创建草图（{DescribeProfile(parameters)}），" +
                $"草图引用 {sketch.JournalIdentifier}。",
                workPartIdentity, details);
        }
        catch (Exception ex)
        {
            (bool undone, string undoError) = Rollback(session, markId, "NXAI 创建草图");
            LogFailure(ex, workPartIdentity, "CreateSketch");
            return CreateBlockExecutionResult.Failure(
                ToolExecutionStatus.ExecutionFailed,
                $"{ex.GetType().Name}: {ex.Message}" +
                (undone ? "（本次操作已回滚。）" : $"（回滚未完成：{undoError}。请在 NX 撤销列表中手动撤销。）"),
                rolledBack: undone, workPartIdentity: workPartIdentity);
        }
        finally
        {
            sketchBuilder?.Destroy();
        }
    }

    private static string DescribeProfile(CreateSketchParameters p) => p.ProfileType switch
    {
        SketchProfileType.Rectangle =>
            $"矩形 {FormatLength(p.RectangleWidth)} × {FormatLength(p.RectangleHeight)} 毫米",
        _ => $"圆形直径 {FormatLength(p.CircleDiameter)} 毫米",
    };

    /// <summary>
    /// Resolves the placement plane from the mode. SelectedPlane requires
    /// exactly one selected planar face or datum plane — never guesses.
    /// WCS planes use the absolute coordinate frame (XY normal +Z, XZ normal
    /// −Y, YZ normal +X; right-handed u×v=n).
    /// </summary>
    private Plane? ResolveSketchPlane(
        Part workPart, SketchPlaneMode mode,
        out string description, out string failure)
    {
        description = "";
        failure = "";
        string planeDescription = "";
        switch (mode)
        {
            case SketchPlaneMode.SelectedPlane:
            {
                var selected = new List<NXObject>();
                var uiSelection = NXOpen.UI.GetUI().SelectionManager;
                int count = uiSelection.GetNumSelectedObjects();
                for (int i = 0; i < count; i++)
                {
                    if (uiSelection.GetSelectedTaggedObject(i) is NXObject obj)
                        selected.Add(obj);
                }
                var faces = new List<Face>();
                var datumPlanes = new List<DatumPlane>();
                foreach (var obj in selected)
                {
                    if (obj is Face f && IsPlanarFace(f)) faces.Add(f);
                    else if (obj is DatumPlane dp) datumPlanes.Add(dp);
                }
                if (faces.Count + datumPlanes.Count != 1)
                {
                    failure = "selected_plane 需要恰好选中一个平面或基准面" +
                        $"（当前选中：{faces.Count} 个平面、{datumPlanes.Count} 个基准面，共 {selected.Count} 个对象）。" +
                        "请在 NX 中只选中一个目标平面后重新发起请求。";
                    return null;
                }
                if (faces.Count == 1)
                {
                    var face = faces[0];
                    GetPlanarFaceFrame(face, out Point3d origin, out Vector3d normal);
                    Plane frame = workPart.Planes.CreatePlane(
                        origin, normal, SmartObject.UpdateOption.WithinModeling);
                    planeDescription = "选中的平面";
                    description = planeDescription;
                    return frame;
                }
                var datum = datumPlanes[0];
                Plane datumFrame = workPart.Planes.CreatePlane(
                    datum.Origin, datum.Normal, SmartObject.UpdateOption.WithinModeling);
                planeDescription = "选中的基准面";
                description = planeDescription;
                return datumFrame;
            }
            case SketchPlaneMode.WcsXY:
                description = planeDescription = "绝对坐标 XY 平面";
                return workPart.Planes.CreatePlane(
                    new Point3d(0, 0, 0), new Vector3d(0, 0, 1), SmartObject.UpdateOption.WithinModeling);
            case SketchPlaneMode.WcsXZ:
                description = planeDescription = "绝对坐标 XZ 平面";
                return workPart.Planes.CreatePlane(
                    new Point3d(0, 0, 0), new Vector3d(0, -1, 0), SmartObject.UpdateOption.WithinModeling);
            case SketchPlaneMode.WcsYZ:
                description = planeDescription = "绝对坐标 YZ 平面";
                return workPart.Planes.CreatePlane(
                    new Point3d(0, 0, 0), new Vector3d(1, 0, 0), SmartObject.UpdateOption.WithinModeling);
            default:
                failure = $"不支持的草图平面模式：{mode}。";
                return null;
        }
    }

    private static Vector3d VecNormalize(Vector3d v)
    {
        double len = Math.Sqrt(v.X * v.X + v.Y * v.Y + v.Z * v.Z);
        return len < 1e-12 ? new Vector3d(0, 0, 0) : new Vector3d(v.X / len, v.Y / len, v.Z / len);
    }

    private static double VecDot(Vector3d a, Vector3d b) => a.X * b.X + a.Y * b.Y + a.Z * b.Z;

    private static bool IsPlanarFace(Face face)
    {
        try
        {
            return face.SolidFaceType.ToString().ToLowerInvariant() == "planar";
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Computes a deterministic right-handed frame on a planar face:
    /// origin = UV-midpoint representative point, normal = unit normal,
    /// u = projection of the world X axis (world Y when nearly parallel),
    /// v = n × u. Same UF APIs as the face-relative cylinder (UF_MODL APIs,
    /// never UFEvalsf on planar faces).
    /// </summary>
    private static void GetPlanarFaceFrame(Face face, out Point3d origin, out Vector3d normal)
    {
        var ufSession = UFSession.GetUFSession();
        double[] uv = new double[4];
        ufSession.Modl.AskFaceUvMinmax(face.Tag, uv);
        double uMid = (uv[0] + uv[1]) / 2.0;
        double vMid = (uv[2] + uv[3]) / 2.0;
        double[] center = new double[3];
        double[] u1 = new double[3], v1 = new double[3], u2 = new double[3], v2 = new double[3];
        double[] unitNorm = new double[3];
        double[] radii = new double[2];
        ufSession.Modl.AskFaceProps(face.Tag,
            new double[] { uMid, vMid }, center, u1, v1, u2, v2, unitNorm, radii);
        origin = new Point3d(center[0], center[1], center[2]);
        normal = VecNormalize(new Vector3d(unitNorm[0], unitNorm[1], unitNorm[2]));
    }

    private static Vector3d Cross(Vector3d a, Vector3d b) =>
        new(a.Y * b.Z - a.Z * b.Y, a.Z * b.X - a.X * b.Z, a.X * b.Y - a.Y * b.X);

    /// <summary>
    /// Builds the closed profile inside the activated sketch. Geometry points
    /// are computed in the sketch plane's world frame (origin + u/v/n);
    /// center_u/center_v offset along u/v — sketch-local by construction,
    /// never model-supplied world coordinates.
    /// </summary>
    private void BuildSketchProfile(
        Sketch sketch, Part workPart, CreateSketchParameters parameters)
    {
        sketch.Activate(Sketch.ViewReorient.False);
        try
        {
            // The sketch's OWN coordinate system is the only authority for
            // where its local U/V axes point and where its plane lies.
            // Deriving a frame by hand (Phase ≤7.4) disagreed with the CSYS
            // NX assigns for the XZ plane — the arc came out on a different
            // plane and AddGeometry rejected it with "圆弧的平面必须与草图
            // 平面平行" (live failure, Phase 8.2). Orientation's element
            // matrix is row-major with the local axes as COLUMNS (same
            // verified mapping as TryGetSketchFrame): local X = (Xx,Yx,Zx),
            // local Y = (Xy,Yy,Zy), local Z (plane normal) = (Xz,Yz,Zz).
            var origin = sketch.Origin;
            Matrix3x3 m = sketch.Orientation.Element;
            var u = VecNormalize(new Vector3d(m.Xx, m.Yx, m.Zx));
            var v = VecNormalize(new Vector3d(m.Xy, m.Yy, m.Zy));
            var n = VecNormalize(new Vector3d(m.Xz, m.Yz, m.Zz));
            PhaseLog.Write("execute.sketch",
                $"frame from sketch orientation: u=({u.X:F3},{u.Y:F3},{u.Z:F3}) " +
                $"v=({v.X:F3},{v.Y:F3},{v.Z:F3}) n=({n.X:F3},{n.Y:F3},{n.Z:F3})");

            Point3d ProfilePoint(double du, double dv) => new(
                origin.X + du * u.X + dv * v.X,
                origin.Y + du * u.Y + dv * v.Y,
                origin.Z + du * u.Z + dv * v.Z);

            if (parameters.ProfileType == SketchProfileType.Rectangle)
            {
                double hu = parameters.RectangleWidth / 2.0;
                double hv = parameters.RectangleHeight / 2.0;
                double cu = parameters.CenterU, cv = parameters.CenterV;
                var p1 = ProfilePoint(cu - hu, cv - hv);
                var p2 = ProfilePoint(cu + hu, cv - hv);
                var p3 = ProfilePoint(cu + hu, cv + hv);
                var p4 = ProfilePoint(cu - hu, cv + hv);
                foreach (var (a, b) in new[] { (p1, p2), (p2, p3), (p3, p4), (p4, p1) })
                {
                    Line line = workPart.Curves.CreateLine(a, b);
                    sketch.AddGeometry(line, Sketch.InferConstraintsOption.InferNoConstraints);
                }
            }
            else
            {
                double r = parameters.CircleDiameter / 2.0;
                var center = ProfilePoint(parameters.CenterU, parameters.CenterV);
                // CreateArc's normal is xDir × yDir (verified NXOpen docs).
                // Passing the sketch's OWN local axes (u, v) makes the arc's
                // normal = u × v = the sketch plane normal by construction —
                // NX cannot reject it for plane mismatch.
                Arc circle = workPart.Curves.CreateArc(
                    center, u, v, r, 0.0, 2.0 * Math.PI);
                sketch.AddGeometry(circle, Sketch.InferConstraintsOption.InferNoConstraints);
            }
            sketch.Update();
        }
        finally
        {
            sketch.Deactivate(Sketch.ViewReorient.False, Sketch.UpdateLevel.Model);
        }
    }

    private static int CountSketches(Part part)
    {
        int count = 0;
        foreach (object _ in part.Sketches.ToArray())
            count++;
        return count;
    }

    private static HashSet<string> CollectSketchIds(Part part)
    {
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (object item in part.Sketches.ToArray())
        {
            if (item is Sketch sketch)
                ids.Add(sketch.JournalIdentifier);
        }
        return ids;
    }

    private static Sketch? FindNewSketch(Part part, HashSet<string> idsBefore)
    {
        foreach (object item in part.Sketches.ToArray())
        {
            if (item is Sketch sketch && !idsBefore.Contains(sketch.JournalIdentifier))
                return sketch;
        }
        return null;
    }

    private static (bool Success, string Reason) VerifySketchCreated(
        Part workPart, Sketch sketch, CreateSketchParameters parameters,
        int sketchesBefore, HashSet<string> idsBefore)
    {
        if (CountSketches(workPart) != sketchesBefore + 1)
            return (false, $"草图数量为 {CountSketches(workPart)}，预期 {sketchesBefore + 1}");
        if (!idsBefore.Contains(sketch.JournalIdentifier) == false)
            return (false, "新建草图的引用与已有草图冲突");

        int expectedGeometry = parameters.ProfileType == SketchProfileType.Rectangle ? 4 : 1;
        NXObject[] geometry;
        try
        {
            geometry = sketch.GetAllGeometry();
        }
        catch (Exception ex)
        {
            return (false, $"无法读取草图几何：{ex.Message}");
        }
        int curveCount = geometry.Count(g => g is Arc or Line);
        if (curveCount != expectedGeometry)
            return (false, $"草图曲线数量为 {curveCount}，预期 {expectedGeometry}");

        return (true, "");
    }

    // ------------------------------------------------------ sketch resolution

    private static Sketch? FindSketchByRef(Part workPart, string sketchRef)
    {
        foreach (object item in workPart.Sketches.ToArray())
        {
            if (item is Sketch sketch &&
                string.Equals(sketch.JournalIdentifier, sketchRef, StringComparison.Ordinal))
            {
                return sketch;
            }
        }
        return null;
    }

    private static Body? FindBodyByRef(Part workPart, string bodyRef)
    {
        foreach (object item in workPart.Bodies)
        {
            if (item is Body body &&
                string.Equals(body.JournalIdentifier, bodyRef, StringComparison.Ordinal))
            {
                return body;
            }
        }
        return null;
    }

    /// <summary>
    /// Creates a section from the sketch's own feature via the verified
    /// curve-feature rule; returns null with a failure reason when the sketch
    /// cannot provide one.
    ///
    /// Phase 7.2: the seed curve must be a LIVE object at AddToSection time.
    /// A deactivated sketch's internal curves are consumed objects — using
    /// one as the seed raised NX JAM "attempt to use an object that is not
    /// alive" (试图使用不活动的对象) in live testing, session-state dependent
    /// (a second request against the same sketch). The sketch is activated
    /// around section construction so its curves are live, and liveness of
    /// every passed object is verified up front. PhaseLog covers every stage
    /// so a future failure names its stage instead of surfacing as an
    /// untranslated exception.
    /// </summary>
    private static (Section? Section, string? Error) CreateSectionFromSketch(
        Part workPart, Sketch sketch)
    {
        PhaseLog.Write("execute.section", "begin");
        Feature? sketchFeature = sketch.Feature;
        if (sketchFeature == null)
        {
            PhaseLog.Write("execute.section", "FAIL: sketch has no feature");
            return (null, "草图没有关联的特征对象，无法构建截面。");
        }
        PhaseLog.Write("execute.section",
            $"sketchFeature={sketchFeature.JournalIdentifier} type={sketchFeature.FeatureType}");

        NXObject[] geometry;
        try
        {
            geometry = sketch.GetAllGeometry();
        }
        catch (Exception ex)
        {
            PhaseLog.Write("execute.section", $"FAIL: GetAllGeometry: {ex.GetType().Name}: {ex.Message}");
            return (null, $"无法读取草图几何作为截面种子：{ex.Message}");
        }
        var curves = geometry.OfType<ICurve>().ToList();
        if (curves.Count == 0)
        {
            PhaseLog.Write("execute.section", "FAIL: no curves in sketch");
            return (null, "草图中没有可用曲线，无法构建截面。");
        }
        PhaseLog.Write("execute.section", $"curves={curves.Count} seed={(curves[0] as NXObject)?.JournalIdentifier}");

        // Liveness gate: every object handed to the section must be alive.
        // A dead wrapper here is the direct cause of the JAM 'not alive'
        // error — refuse with a precise message instead of letting NX throw.
        foreach (var c in curves)
        {
            if (c is NXObject curveObj && IsDead(curveObj))
            {
                PhaseLog.Write("execute.section", $"FAIL: curve not alive: {curveObj.JournalIdentifier}");
                return (null, $"草图曲线 {curveObj.JournalIdentifier} 已失效，无法作为截面。" +
                    "请重新创建草图后再执行。");
            }
        }

        bool activated = false;
        try
        {
            // Activate so the sketch's curves are live prototype objects for
            // the section evaluation; Deactivate returns to the committed state.
            if (!sketch.IsActive)
            {
                sketch.Activate(Sketch.ViewReorient.False);
                activated = true;
                PhaseLog.Write("execute.section", "sketch activated for section build");
            }

            Section section = workPart.Sections.CreateSection();
            PhaseLog.Write("execute.section", "section created");
            SelectionIntentRule[] rules =
                { workPart.ScRuleFactory.CreateRuleCurveFeature(new[] { sketchFeature }) };
            section.AddToSection(rules, (NXObject)curves[0], null!, null!,
                sketch.Origin, Section.Mode.Create);
            PhaseLog.Write("execute.section", "addToSection ok");
            return (section, null);
        }
        catch (Exception ex)
        {
            PhaseLog.Write("execute.section",
                $"FAIL: {ex.GetType().Name}: {ex.Message} stack={(ex.StackTrace ?? "").Split('\n').FirstOrDefault() ?? ""}");
            throw;
        }
        finally
        {
            if (activated)
            {
                try
                {
                    sketch.Deactivate(Sketch.ViewReorient.False, Sketch.UpdateLevel.Model);
                    PhaseLog.Write("execute.section", "sketch deactivated");
                }
                catch (Exception ex)
                {
                    PhaseLog.Write("execute.section", $"WARN deactivate: {ex.Message}");
                }
            }
        }
    }

    /// <summary>
    /// NX liveness check: a disposed/moved-to-void wrapper or an object whose
    /// tag no longer resolves is dead. JournalIdentifier is only read when
    /// the object answers, so logging never triggers the JAM error itself.
    /// </summary>
    private static bool IsDead(NXObject obj)
    {
        try
        {
            _ = obj.Tag;
            _ = obj.IsOccurrence;
            return false;
        }
        catch
        {
            return true;
        }
    }

    // ------------------------------------------------------------ create_extrude

    public CreateBlockExecutionResult ExecuteCreateExtrude(
        CreateExtrudeParameters parameters, string? expectedWorkPartFullPath)
    {
        if (parameters.Distance <= 0)
        {
            return CreateBlockExecutionResult.Failure(
                ToolExecutionStatus.InvalidParameters,
                $"拉伸距离必须为正（{parameters.Distance} 毫米）。");
        }

        if (!TryResolveWorkPart(expectedWorkPartFullPath, "拉伸",
                out Session session, out Part workPart, out string workPartIdentity,
                out CreateBlockExecutionResult? failure))
        {
            return failure!;
        }

        Sketch? sketch;
        string? sketchError;
        PhaseLog.Write("execute.extrude", $"resolve begin ref={parameters.SketchRef}");
        (sketch, sketchError) = ResolveSketchRef(workPart, parameters.SketchRef);
        if (sketch == null)
        {
            PhaseLog.Write("execute.extrude", $"resolve FAIL: {sketchError}");
            return CreateBlockExecutionResult.Failure(
                ToolExecutionStatus.PreconditionsChanged, sketchError!,
                workPartIdentity: workPartIdentity);
        }
        PhaseLog.Write("execute.extrude",
            $"resolve ok id={sketch.JournalIdentifier} name={sketch.Name} active={sketch.IsActive} workPart={workPartIdentity}");
        if (IsDead(sketch))
        {
            PhaseLog.Write("execute.extrude", "resolve FAIL: sketch object not alive");
            return CreateBlockExecutionResult.Failure(
                ToolExecutionStatus.PreconditionsChanged,
                "草图对象已失效（不存活），无法拉伸。请重新创建草图后再执行。",
                workPartIdentity: workPartIdentity);
        }

        if (!TryGetSketchFrame(sketch, out Vector3d sketchNormal, out string frameError))
        {
            PhaseLog.Write("execute.extrude", $"frame FAIL: {frameError}");
            return CreateBlockExecutionResult.Failure(
                ToolExecutionStatus.PreconditionsChanged, frameError,
                workPartIdentity: workPartIdentity);
        }
        Vector3d direction = parameters.Direction == ExtrudeDirection.Reverse
            ? new Vector3d(-sketchNormal.X, -sketchNormal.Y, -sketchNormal.Z)
            : sketchNormal;

        int bodiesBefore = CountBodies(workPart);
        HashSet<string> idsBefore = CollectBodyIds(workPart);

        Session.UndoMarkId markId = session.SetUndoMark(
            Session.MarkVisibility.Visible, "NXAI 创建拉伸");

        ExtrudeBuilder? extrudeBuilder = null;
        try
        {
            var (section, sectionError) = CreateSectionFromSketch(workPart, sketch);
            if (section == null)
            {
                PhaseLog.Write("execute.extrude", $"section FAIL: {sectionError}");
                throw new InvalidOperationException(sectionError!);
            }

            extrudeBuilder = workPart.Features.CreateExtrudeBuilder(null!);
            PhaseLog.Write("execute.extrude", "builder created");
            extrudeBuilder.Section = section;
            extrudeBuilder.Direction = workPart.Directions.CreateDirection(
                sketch.Origin, direction, SmartObject.UpdateOption.WithinModeling);
            extrudeBuilder.Limits.StartExtend.Value.SetFormula("0");
            extrudeBuilder.Limits.EndExtend.Value.SetFormula(
                FormatLength(parameters.Distance));

            extrudeBuilder.CommitFeature();
            session.UpdateManager.DoUpdate(markId);
            PhaseLog.Write("execute.extrude", "commit done");

            var box = ComputeProfileExtrudeAabb(sketch, parameters.Distance, direction);

            var verification = VerifyNewBodyByAabb(workPart, bodiesBefore, idsBefore,
                box, "拉伸体");
            PhaseLog.Write("execute.extrude",
                $"verify: {(verification.Success ? "ok" : "FAILED " + verification.Reason)}");
            if (!verification.Success)
            {
                (bool undone, string undoError) = Rollback(session, markId, "NXAI 创建拉伸");
                PhaseLog.Write("execute.extrude", $"rolled back: {verification.Reason}");
                LogFailure(new InvalidOperationException($"Extrude postcondition failed: {verification.Reason}"),
                    workPartIdentity, "CreateExtrude");
                return CreateBlockExecutionResult.Failure(
                    ToolExecutionStatus.VerificationFailed,
                    $"拉伸结果与请求不符（{verification.Reason}）。" +
                    (undone ? "已回滚本次操作。" : $"回滚未完成：{undoError}。请在 NX 撤销列表中手动撤销。"),
                    rolledBack: undone, workPartIdentity: workPartIdentity);
            }

            string newBodyRef = verification.BodyRef!;
            string details = JsonSerializer.Serialize(new Dictionary<string, object?>
            {
                ["sketchRef"] = sketch.JournalIdentifier,
                ["newBodyRef"] = newBodyRef,
                ["directionMm"] = parameters.Direction == ExtrudeDirection.Reverse ? "reverse" : "normal",
            });
            return CreateBlockExecutionResult.Success(
                parameters.Distance, parameters.Distance, parameters.Distance,
                $"已拉伸草图（{FormatLength(parameters.Distance)} 毫米，" +
                $"{(parameters.Direction == ExtrudeDirection.Reverse ? "法向反向" : "草图法向")}），" +
                $"实体引用 {newBodyRef}。",
                workPartIdentity, details);
        }
        catch (Exception ex)
        {
            PhaseLog.Write("execute.extrude",
                $"FAIL: {ex.GetType().Name}: {ex.Message} stack={(ex.StackTrace ?? "").Split('\n').FirstOrDefault() ?? ""}");
            (bool undone, string undoError) = Rollback(session, markId, "NXAI 创建拉伸");
            LogFailure(ex, workPartIdentity, "CreateExtrude");
            return CreateBlockExecutionResult.Failure(
                ToolExecutionStatus.ExecutionFailed,
                $"{ex.GetType().Name}: {ex.Message}" +
                (undone ? "（本次操作已回滚。）" : $"（回滚未完成：{undoError}。请在 NX 撤销列表中手动撤销。）"),
                rolledBack: undone, workPartIdentity: workPartIdentity);
        }
        finally
        {
            extrudeBuilder?.Destroy();
            PhaseLog.Write("execute.extrude", "builder destroyed");
        }
    }

    private static (Sketch? Sketch, string? Error) ResolveSketchRef(
        Part workPart, string sketchRef)
    {
        Sketch? sketch = FindSketchByRef(workPart, sketchRef);
        if (sketch == null)
        {
            return (null, $"草图引用 {sketchRef} 在当前工作部件中不存在或已失效" +
                "（可能已被撤销）。请重新创建草图后再执行。");
        }
        return (sketch, null);
    }

    /// <summary>
    /// Sketch plane normal. The sketch's Orientation NXMatrix maps sketch
    /// local axes to world space: local +Z is the plane normal. The element
    /// matrix (Matrix3x3) is row-major: columns are the local axes in world
    /// coordinates, so local Z = (Xz, Yz, Zz).
    /// </summary>
    private static bool TryGetSketchFrame(
        Sketch sketch, out Vector3d normal, out string error)
    {
        error = "";
        normal = default;
        try
        {
            Matrix3x3 m = sketch.Orientation.Element;
            var localZ = new Vector3d(m.Xz, m.Yz, m.Zz);
            double len = Math.Sqrt(localZ.X * localZ.X + localZ.Y * localZ.Y + localZ.Z * localZ.Z);
            if (len < 1e-9)
            {
                error = "无法读取草图平面法向（方向矩阵退化）。";
                return false;
            }
            normal = new Vector3d(localZ.X / len, localZ.Y / len, localZ.Z / len);
            return true;
        }
        catch (Exception ex)
        {
            error = $"读取草图平面法向失败：{ex.Message}";
            return false;
        }
    }

    /// <summary>
    /// Analytic world AABB of the extruded profile: the sketch's own geometry
    /// bounds (from its curves) swept along the extrude direction.
    /// Computed from the actual sketch curves, not from the request — so a
    /// mismatch between sketch content and request is caught, not masked.
    /// Circle points are the extreme points ON THE ARC'S OWN PLANE
    /// (center ± r·xDir / ± r·yDir), never a 3D box-corner set: the corners
    /// would wrongly extend the box along the plane normal by ±r, which
    /// rejected a correct cylinder in live testing (Phase 7.2 root cause).
    /// </summary>
    private static (double MinX, double MinY, double MinZ, double MaxX, double MaxY, double MaxZ)
        ComputeProfileExtrudeAabb(Sketch sketch, double distance, Vector3d direction)
    {
        var geometry = sketch.GetAllGeometry();
        var pts = new List<Point3d>();
        foreach (var g in geometry)
        {
            switch (g)
            {
                case Line line:
                    pts.Add(line.StartPoint);
                    pts.Add(line.EndPoint);
                    break;
                case Arc arc:
                {
                    // Extreme points of the circle lie in the arc's plane:
                    // center ± r·xDir and center ± r·yDir. Arc exposes its
                    // plane via the inherited Conic.Matrix (NXMatrix.Element
                    // Matrix3x3, columns = local axes — same verified mapping
                    // as TryGetSketchFrame).
                    double r = arc.Radius;
                    var c = arc.CenterPoint;
                    var m = arc.Matrix.Element;
                    var x = new Vector3d(m.Xx, m.Yx, m.Zx);
                    var y = new Vector3d(m.Xy, m.Yy, m.Zy);
                    pts.Add(new Point3d(c.X + r * x.X, c.Y + r * x.Y, c.Z + r * x.Z));
                    pts.Add(new Point3d(c.X - r * x.X, c.Y - r * x.Y, c.Z - r * x.Z));
                    pts.Add(new Point3d(c.X + r * y.X, c.Y + r * y.Y, c.Z + r * y.Z));
                    pts.Add(new Point3d(c.X - r * y.X, c.Y - r * y.Y, c.Z - r * y.Z));
                    break;
                }
            }
        }

        bool first = true;
        double minX = 0, minY = 0, minZ = 0, maxX = 0, maxY = 0, maxZ = 0;
        foreach (var p in pts)
        {
            for (int sign = 0; sign < 2; sign++)
            {
                double t = sign == 0 ? 0 : distance;
                var q = new Point3d(
                    p.X + t * direction.X, p.Y + t * direction.Y, p.Z + t * direction.Z);
                if (first)
                {
                    minX = maxX = q.X;
                    minY = maxY = q.Y;
                    minZ = maxZ = q.Z;
                    first = false;
                }
                else
                {
                    minX = Math.Min(minX, q.X); maxX = Math.Max(maxX, q.X);
                    minY = Math.Min(minY, q.Y); maxY = Math.Max(maxY, q.Y);
                    minZ = Math.Min(minZ, q.Z); maxZ = Math.Max(maxZ, q.Z);
                }
            }
        }
        return (minX, minY, minZ, maxX, maxY, maxZ);
    }

    // ------------------------------------------------------------ create_revolve

    public CreateBlockExecutionResult ExecuteCreateRevolve(
        CreateRevolveParameters parameters, string? expectedWorkPartFullPath)
    {
        if (parameters.AngleDeg <= 0 || parameters.AngleDeg > 360)
        {
            return CreateBlockExecutionResult.Failure(
                ToolExecutionStatus.InvalidParameters,
                $"旋转角度必须在 (0, 360] 度范围内（当前 {parameters.AngleDeg} 度）。");
        }

        if (!TryResolveWorkPart(expectedWorkPartFullPath, "旋转体",
                out Session session, out Part workPart, out string workPartIdentity,
                out CreateBlockExecutionResult? failure))
        {
            return failure!;
        }

        Sketch? sketch;
        string? sketchError;
        PhaseLog.Write("execute.revolve", $"resolve begin ref={parameters.SketchRef}");
        (sketch, sketchError) = ResolveSketchRef(workPart, parameters.SketchRef);
        if (sketch == null)
        {
            PhaseLog.Write("execute.revolve", $"resolve FAIL: {sketchError}");
            return CreateBlockExecutionResult.Failure(
                ToolExecutionStatus.PreconditionsChanged, sketchError!,
                workPartIdentity: workPartIdentity);
        }
        // Liveness gate BEFORE any builder is created: a stale reference
        // (e.g. to a sketch rolled back moments earlier) reaching
        // RevolveBuilder.CommitFeature caused a native access violation in
        // live testing (Phase 7.4 root cause B). Refuse managed-side instead.
        if (IsDead(sketch))
        {
            PhaseLog.Write("execute.revolve", "resolve FAIL: sketch object not alive");
            return CreateBlockExecutionResult.Failure(
                ToolExecutionStatus.PreconditionsChanged,
                "草图对象已失效（可能已被撤销），无法旋转。请重新创建草图后再执行。",
                workPartIdentity: workPartIdentity);
        }
        PhaseLog.Write("execute.revolve",
            $"resolve ok id={sketch.JournalIdentifier} name={sketch.Name} active={sketch.IsActive}");

        // Resolve the axis BEFORE the undo mark (selection read).
        Point3d axisPoint;
        Vector3d axisVector;
        string axisDescription;
        try
        {
            var axisResult = ResolveRevolveAxis(workPart, parameters.AxisMode);
            if (!axisResult.Success)
            {
                return CreateBlockExecutionResult.Failure(
                    ToolExecutionStatus.PreconditionsChanged, axisResult.Error!,
                    workPartIdentity: workPartIdentity);
            }
            axisPoint = axisResult.Point;
            axisVector = axisResult.Vector;
            axisDescription = axisResult.Description;
        }
        catch (Exception ex)
        {
            return CreateBlockExecutionResult.Failure(
                ToolExecutionStatus.PreconditionsChanged,
                $"解析旋转轴失败：{ex.Message}", workPartIdentity: workPartIdentity);
        }

        // The axis must lie in the sketch plane — verify with the sketch's
        // own plane data; otherwise refuse rather than guessing an axis.
        if (!TryGetSketchFrame(sketch, out Vector3d sketchNormal, out string frameError))
        {
            return CreateBlockExecutionResult.Failure(
                ToolExecutionStatus.PreconditionsChanged, frameError,
                workPartIdentity: workPartIdentity);
        }
        double planeDistance = Math.Abs(
            (axisPoint.X - sketch.Origin.X) * sketchNormal.X +
            (axisPoint.Y - sketch.Origin.Y) * sketchNormal.Y +
            (axisPoint.Z - sketch.Origin.Z) * sketchNormal.Z);
        if (planeDistance > PlaneFrameToleranceMm)
        {
            return CreateBlockExecutionResult.Failure(
                ToolExecutionStatus.PreconditionsChanged,
                $"旋转轴（{axisDescription}）不在草图平面内（距离 {planeDistance:F3} 毫米）。" +
                "旋转要求轴位于草图平面中；请调整草图或轴后重试。",
                workPartIdentity: workPartIdentity);
        }
        double axisVecInPlane = VecDot(sketchNormal, VecNormalize(axisVector));
        if (Math.Abs(axisVecInPlane) > 1e-6)
        {
            return CreateBlockExecutionResult.Failure(
                ToolExecutionStatus.PreconditionsChanged,
                $"旋转轴方向（{axisDescription}）与草图平面不平行，无法在该平面内旋转。",
                workPartIdentity: workPartIdentity);
        }

        int bodiesBefore = CountBodies(workPart);
        HashSet<string> idsBefore = CollectBodyIds(workPart);

        Session.UndoMarkId markId = session.SetUndoMark(
            Session.MarkVisibility.Visible, "NXAI 创建旋转体");

        RevolveBuilder? revolveBuilder = null;
        try
        {
            var (section, sectionError) = CreateSectionFromSketch(workPart, sketch);
            if (section == null)
                throw new InvalidOperationException(sectionError!);

            revolveBuilder = workPart.Features.CreateRevolveBuilder(null!);
            revolveBuilder.Section = section;
            revolveBuilder.Axis = workPart.Axes.CreateAxis(
                axisPoint, axisVector, SmartObject.UpdateOption.WithinModeling);
            revolveBuilder.Limits.StartExtend.Value.SetFormula("0");
            revolveBuilder.Limits.EndExtend.Value.SetFormula(
                FormatLength(parameters.AngleDeg));
            PhaseLog.Write("execute.revolve",
                $"builder configured: axis={axisDescription} angle={parameters.AngleDeg}");

            // NOTE: a stale sketch reference reaching this point crashed NX
            // with a native access violation in live testing (Phase 7.4
            // root cause B) — the IsDead gate above exists precisely to
            // prevent this path. An AV here is process-fatal and cannot be
            // caught; the gate + PhaseLog trail are the defenses.
            revolveBuilder.CommitFeature();
            session.UpdateManager.DoUpdate(markId);
            PhaseLog.Write("execute.revolve", "commit done");

            var box = ComputeRevolveEnvelopeAabb(sketch, axisPoint, axisVector);

            var verification = VerifyNewBodyByAabb(workPart, bodiesBefore, idsBefore,
                box, "旋转体");
            if (!verification.Success)
            {
                (bool undone, string undoError) = Rollback(session, markId, "NXAI 创建旋转体");
                LogFailure(new InvalidOperationException($"Revolve postcondition failed: {verification.Reason}"),
                    workPartIdentity, "CreateRevolve");
                return CreateBlockExecutionResult.Failure(
                    ToolExecutionStatus.VerificationFailed,
                    $"旋转结果与请求不符（{verification.Reason}）。" +
                    (undone ? "已回滚本次操作。" : $"回滚未完成：{undoError}。请在 NX 撤销列表中手动撤销。"),
                    rolledBack: undone, workPartIdentity: workPartIdentity);
            }

            string newBodyRef = verification.BodyRef!;
            string details = JsonSerializer.Serialize(new Dictionary<string, object?>
            {
                ["sketchRef"] = sketch.JournalIdentifier,
                ["newBodyRef"] = newBodyRef,
                ["axis"] = axisDescription,
                ["angleDeg"] = parameters.AngleDeg,
            });
            return CreateBlockExecutionResult.Success(
                0, 0, parameters.AngleDeg,
                $"已旋转草图生成旋转体（轴：{axisDescription}，{parameters.AngleDeg} 度），" +
                $"实体引用 {newBodyRef}。",
                workPartIdentity, details);
        }
        catch (Exception ex)
        {
            (bool undone, string undoError) = Rollback(session, markId, "NXAI 创建旋转体");
            LogFailure(ex, workPartIdentity, "CreateRevolve");
            return CreateBlockExecutionResult.Failure(
                ToolExecutionStatus.ExecutionFailed,
                $"{ex.GetType().Name}: {ex.Message}" +
                (undone ? "（本次操作已回滚。）" : $"（回滚未完成：{undoError}。请在 NX 撤销列表中手动撤销。）"),
                rolledBack: undone, workPartIdentity: workPartIdentity);
        }
        finally
        {
            revolveBuilder?.Destroy();
        }
    }

    private readonly record struct AxisResolution(
        bool Success, Point3d Point, Vector3d Vector, string Description, string? Error);

    private static AxisResolution ResolveRevolveAxis(Part workPart, RevolveAxisMode mode)
    {
        switch (mode)
        {
            case RevolveAxisMode.SelectedAxis:
            {
                var selected = new List<NXObject>();
                var uiSelection = NXOpen.UI.GetUI().SelectionManager;
                int count = uiSelection.GetNumSelectedObjects();
                for (int i = 0; i < count; i++)
                {
                    if (uiSelection.GetSelectedTaggedObject(i) is NXObject obj)
                        selected.Add(obj);
                }
                var axes = selected.OfType<DatumAxis>().ToList();
                var edges = selected.OfType<Edge>().ToList();
                var lines = selected.OfType<Line>().ToList();
                int total = axes.Count + edges.Count + lines.Count;
                if (total != 1)
                {
                    return new AxisResolution(false, default, default, "",
                        $"selected_axis 需要恰好选中一个基准轴、直边或草图直线" +
                        $"（当前：{axes.Count} 个基准轴、{edges.Count} 条边、{lines.Count} 条直线，共 {selected.Count} 个对象）。");
                }
                if (axes.Count == 1)
                {
                    var datum = axes[0];
                    var dir = VecNormalize(datum.Direction);
                    return new AxisResolution(true, datum.Origin, dir, "选中的基准轴", null);
                }
                if (lines.Count == 1)
                {
                    var line = lines[0];
                    var dir = VecNormalize(new Vector3d(
                        line.EndPoint.X - line.StartPoint.X,
                        line.EndPoint.Y - line.StartPoint.Y,
                        line.EndPoint.Z - line.StartPoint.Z));
                    return new AxisResolution(true, line.StartPoint, dir, "选中的草图直线", null);
                }
                var edge = edges[0];
                if (edge.SolidEdgeType.ToString().ToLowerInvariant() != "linear")
                {
                    return new AxisResolution(false, default, default, "",
                        $"选中的边不是直线（类型：{edge.SolidEdgeType}），无法作为旋转轴。");
                }
                edge.GetVertices(out var v1, out var v2);
                var edgeDir = VecNormalize(new Vector3d(v2.X - v1.X, v2.Y - v1.Y, v2.Z - v1.Z));
                return new AxisResolution(true, v1, edgeDir, "选中的直边", null);
            }
            case RevolveAxisMode.WcsX:
                return new AxisResolution(true, new Point3d(0, 0, 0), new Vector3d(1, 0, 0), "绝对坐标 X 轴", null);
            case RevolveAxisMode.WcsY:
                return new AxisResolution(true, new Point3d(0, 0, 0), new Vector3d(0, 1, 0), "绝对坐标 Y 轴", null);
            case RevolveAxisMode.WcsZ:
                return new AxisResolution(true, new Point3d(0, 0, 0), new Vector3d(0, 0, 1), "绝对坐标 Z 轴", null);
            default:
                return new AxisResolution(false, default, default, "", $"不支持的轴模式：{mode}。");
        }
    }

    /// <summary>
    /// Analytic world AABB of the solid of revolution about the axis line
    /// (point + unit vector): axial extent from the sketch curve points
    /// projected onto the axis, radial extent from the max axis distance.
    /// Exact for a full 360° sweep; for partial angles this is a conservative
    /// superset — stated honestly in the phase report.
    /// </summary>
    private static (double MinX, double MinY, double MinZ, double MaxX, double MaxY, double MaxZ)
        ComputeRevolveEnvelopeAabb(Sketch sketch, Point3d axisPoint, Vector3d axisVector)
    {
        var u = VecNormalize(axisVector);
        var geometry = sketch.GetAllGeometry();
        var pts = new List<Point3d>();
        foreach (var g in geometry)
        {
            switch (g)
            {
                case Line line:
                    pts.Add(line.StartPoint);
                    pts.Add(line.EndPoint);
                    break;
                case Arc arc:
                {
                    double r = arc.Radius;
                    var c = arc.CenterPoint;
                    pts.Add(new Point3d(c.X - r, c.Y - r, c.Z - r));
                    pts.Add(new Point3d(c.X + r, c.Y + r, c.Z + r));
                    break;
                }
            }
        }

        double zMin = double.MaxValue, zMax = double.MinValue, maxR = 0;
        foreach (var p in pts)
        {
            var rel = new Vector3d(p.X - axisPoint.X, p.Y - axisPoint.Y, p.Z - axisPoint.Z);
            double z = VecDot(rel, u);
            // Distance to the axis line = |rel − (rel·u)u|.
            var along = new Vector3d(z * u.X, z * u.Y, z * u.Z);
            var perp = new Vector3d(rel.X - along.X, rel.Y - along.Y, rel.Z - along.Z);
            maxR = Math.Max(maxR, Math.Sqrt(VecDot(perp, perp)));
            zMin = Math.Min(zMin, z);
            zMax = Math.Max(zMax, z);
        }

        // Radius of a revolution about axis u reaches at most maxR on the
        // plane perpendicular to u; per world axis the radial reach is
        // maxR·sqrt(1−u_i²) (the component of the radial circle along axis i).
        double sX = Math.Sqrt(Math.Max(0, 1 - u.X * u.X));
        double sY = Math.Sqrt(Math.Max(0, 1 - u.Y * u.Y));
        double sZ = Math.Sqrt(Math.Max(0, 1 - u.Z * u.Z));
        double zA = axisPoint.X, zB = axisPoint.Y, zC = axisPoint.Z;
        double tLo = Math.Min(zMin, zMax), tHi = Math.Max(zMin, zMax);

        double minX = zA + u.X * tLo - sX * maxR;
        double maxX = zA + u.X * tHi + sX * maxR;
        double minY = zB + u.Y * tLo - sY * maxR;
        double maxY = zB + u.Y * tHi + sY * maxR;
        double minZ = zC + u.Z * tLo - sZ * maxR;
        double maxZ = zC + u.Z * tHi + sZ * maxR;
        return (minX, minY, minZ, maxX, maxY, maxZ);
    }

    // ------------------------------------------------------------ boolean

    public CreateBlockExecutionResult ExecuteBooleanOperation(
        BooleanOperationParameters parameters, string? expectedWorkPartFullPath)
    {
        if (!TryResolveWorkPart(expectedWorkPartFullPath, "布尔运算",
                out Session session, out Part workPart, out string workPartIdentity,
                out CreateBlockExecutionResult? failure))
        {
            return failure!;
        }

        Body? target = FindBodyByRef(workPart, parameters.TargetBodyRef);
        Body? tool = FindBodyByRef(workPart, parameters.ToolBodyRef);
        if (target == null)
        {
            return CreateBlockExecutionResult.Failure(
                ToolExecutionStatus.PreconditionsChanged,
                $"目标体引用 {parameters.TargetBodyRef} 在当前工作部件中不存在或已失效。请重新创建或选择实体。",
                workPartIdentity: workPartIdentity);
        }
        if (tool == null)
        {
            return CreateBlockExecutionResult.Failure(
                ToolExecutionStatus.PreconditionsChanged,
                $"工具体引用 {parameters.ToolBodyRef} 在当前工作部件中不存在或已失效。请重新创建或选择实体。",
                workPartIdentity: workPartIdentity);
        }
        if (ReferenceEquals(target, tool))
        {
            return CreateBlockExecutionResult.Failure(
                ToolExecutionStatus.PreconditionsChanged,
                "目标体与工具体解析为同一实体，无法执行布尔运算。",
                workPartIdentity: workPartIdentity);
        }
        if (target.IsSheetBody || tool.IsSheetBody)
        {
            return CreateBlockExecutionResult.Failure(
                ToolExecutionStatus.PreconditionsChanged,
                "布尔运算需要实体（solid body），当前引用了片体（sheet body）。",
                workPartIdentity: workPartIdentity);
        }

        int bodiesBefore = CountBodies(workPart);
        HashSet<string> idsBefore = CollectBodyIds(workPart);
        double[] targetBox = ReadBodyBox(target);
        double[] toolBox = ReadBodyBox(tool);

        string opName = parameters.Operation switch
        {
            BooleanOperationType.Unite => "并集",
            BooleanOperationType.Subtract => "差集",
            _ => "交集",
        };

        Session.UndoMarkId markId = session.SetUndoMark(
            Session.MarkVisibility.Visible, $"NXAI 布尔{opName}");

        BooleanBuilder? booleanBuilder = null;
        try
        {
            booleanBuilder = workPart.Features.CreateBooleanBuilder(null!);
            booleanBuilder.Operation = parameters.Operation switch
            {
                BooleanOperationType.Unite => Feature.BooleanType.Unite,
                BooleanOperationType.Subtract => Feature.BooleanType.Subtract,
                _ => Feature.BooleanType.Intersect,
            };
            // Targets/Tools are the documented SelectBodyList/SelectDisplayableObjectList
            // path (deprecated in favor of ScCollector, still fully supported in
            // NX2512; the collector variant requires a selection-rule replacement
            // flow that adds no safety here).
#pragma warning disable CS0618
            booleanBuilder.Targets.Add(new[] { target });
            booleanBuilder.Tools.Add(new DisplayableObject[] { tool });
#pragma warning restore CS0618

            booleanBuilder.CommitFeature();
            session.UpdateManager.DoUpdate(markId);
            PhaseLog.Write("execute.boolean", $"commit done: {opName}");

            var verification = VerifyBooleanResult(workPart, bodiesBefore, idsBefore,
                parameters.Operation, targetBox, toolBox);
            if (!verification.Success)
            {
                (bool undone, string undoError) = Rollback(session, markId, $"NXAI 布尔{opName}");
                LogFailure(new InvalidOperationException($"Boolean postcondition failed: {verification.Reason}"),
                    workPartIdentity, $"Boolean{opName}");
                return CreateBlockExecutionResult.Failure(
                    ToolExecutionStatus.VerificationFailed,
                    $"布尔{opName}结果与预期不符（{verification.Reason}）。" +
                    (undone ? "已回滚本次操作。" : $"回滚未完成：{undoError}。请在 NX 撤销列表中手动撤销。"),
                    rolledBack: undone, workPartIdentity: workPartIdentity);
            }

            string resultBodyRef = verification.BodyRef!;
            string toolFate = "工具体已被消耗（合并入布尔特征）";
            string details = JsonSerializer.Serialize(new Dictionary<string, object?>
            {
                ["operation"] = parameters.Operation.ToString().ToLowerInvariant(),
                ["resultBodyRef"] = resultBodyRef,
                ["toolConsumed"] = true,
            });
            return CreateBlockExecutionResult.Success(
                0, 0, 0,
                $"已完成布尔{opName}（结果体引用 {resultBodyRef}，{toolFate}）。",
                workPartIdentity, details);
        }
        catch (Exception ex)
        {
            (bool undone, string undoError) = Rollback(session, markId, $"NXAI 布尔{opName}");
            LogFailure(ex, workPartIdentity, $"Boolean{opName}");
            return CreateBlockExecutionResult.Failure(
                ToolExecutionStatus.ExecutionFailed,
                $"{ex.GetType().Name}: {ex.Message}" +
                (undone ? "（本次操作已回滚，部件恢复原状。）" : $"（回滚未完成：{undoError}。请在 NX 撤销列表中手动撤销。）"),
                rolledBack: undone, workPartIdentity: workPartIdentity);
        }
        finally
        {
            booleanBuilder?.Destroy();
        }
    }

    private static double[] ReadBodyBox(Body body)
    {
        try
        {
            var ufSession = UFSession.GetUFSession();
            double[] box = new double[6];
            ufSession.Modl.AskBoundingBox(body.Tag, box);
            return box;
        }
        catch
        {
            return Array.Empty<double>();
        }
    }

    /// <summary>
    /// Boolean postcondition: exactly one body consumed (n → n−1), and the
    /// surviving body satisfies the operation's necessary containment
    /// relations (unite: ⊇ target; subtract: ⊆ target; intersect: ⊆ both).
    /// Necessary-condition strength is documented — volume-level equality is
    /// a live acceptance item, not claimed here.
    /// </summary>
    private static (bool Success, string Reason, string? BodyRef) VerifyBooleanResult(
        Part workPart, int bodiesBefore, HashSet<string> idsBefore,
        BooleanOperationType operation, double[] targetBox, double[] toolBox)
    {
        int after = CountBodies(workPart);
        if (after != bodiesBefore - 1)
            return (false, $"实体数量为 {after}，预期 {bodiesBefore - 1}", null);

        Body? result = null;
        foreach (object item in workPart.Bodies)
        {
            if (item is Body body && !idsBefore.Contains(body.JournalIdentifier))
            {
                if (result != null)
                    return (false, "发现多个新实体，无法确定布尔结果", null);
                result = body;
            }
        }
        result ??= FindBodyByRef(workPart, idsBefore.Count > 0 ? "" : "") is null ? null : result;
        if (result == null)
        {
            // Target identity may survive the boolean — fall back to the
            // surviving body that is neither of the original identities.
            foreach (object item in workPart.Bodies)
            {
                if (item is Body body)
                    result = body;
            }
            if (result == null)
                return (false, "无法定位布尔结果实体", null);
        }

        double[] resultBox = ReadBodyBox(result);
        if (resultBox.Length != 6 || targetBox.Length != 6 || toolBox.Length != 6)
            return (false, "无法读取布尔运算前后的包围盒", result.JournalIdentifier);

        const double tol = 0.01;
        bool Contains(double[] outer, double[] inner) =>
            inner[0] >= outer[0] - tol && inner[1] >= outer[1] - tol && inner[2] >= outer[2] - tol &&
            inner[3] <= outer[3] + tol && inner[4] <= outer[4] + tol && inner[5] <= outer[5] + tol;

        switch (operation)
        {
            case BooleanOperationType.Unite:
                if (!Contains(resultBox, targetBox))
                    return (false, "并集结果包围盒不包含目标体包围盒", result.JournalIdentifier);
                break;
            case BooleanOperationType.Subtract:
                if (!Contains(targetBox, resultBox))
                    return (false, "差集结果包围盒未保持在目标体范围内", result.JournalIdentifier);
                break;
            case BooleanOperationType.Intersect:
                if (!Contains(targetBox, resultBox) || !Contains(toolBox, resultBox))
                    return (false, "交集结果包围盒未同时包含于目标体与工具体范围内", result.JournalIdentifier);
                break;
        }
        return (true, "", result.JournalIdentifier);
    }

    // ------------------------------------------------------ shared verification

    private readonly record struct BodyAabbVerification(
        bool Success, string Reason, string? BodyRef);

    /// <summary>
    /// Generic new-body check: exactly one body added (identity diff), and
    /// its axis-aligned bounding box matches the analytic expectation within
    /// DimensionToleranceMm. Uses the same UF bounding-box query as the
    /// primitive tools.
    /// </summary>
    private BodyAabbVerification VerifyNewBodyByAabb(
        Part workPart, int bodiesBefore, HashSet<string> idsBefore,
        (double MinX, double MinY, double MinZ, double MaxX, double MaxY, double MaxZ) expected,
        string what)
    {
        int bodiesAfter = CountBodies(workPart);
        if (bodiesAfter != bodiesBefore + 1)
            return new BodyAabbVerification(false, $"实体数量为 {bodiesAfter}，预期 {bodiesBefore + 1}", null);

        Body? newBody = null;
        foreach (object item in workPart.Bodies)
        {
            if (item is Body body && !idsBefore.Contains(body.JournalIdentifier))
            {
                if (newBody != null)
                    return new BodyAabbVerification(false, "发现多个新实体，无法确定本次创建结果", null);
                newBody = body;
            }
        }
        if (newBody == null)
            return new BodyAabbVerification(false, "未找到本次创建的新实体", null);

        if (!TryGetBoundingBoxMm(workPart, newBody, out double lx, out double wy, out double lz))
            return new BodyAabbVerification(false, "无法读取新实体的包围盒", null);

        // AskBoundingBox returns absolute min/max; compare both extents and
        // positions against the analytic box.
        var ufSession = UFSession.GetUFSession();
        double[] box = new double[6];
        try
        {
            ufSession.Modl.AskBoundingBox(newBody.Tag, box);
        }
        catch (Exception ex)
        {
            return new BodyAabbVerification(false, $"无法读取新实体包围盒：{ex.Message}", null);
        }

        double tol = DimensionToleranceMm;
        if (Math.Abs(box[0] - expected.MinX) > tol || Math.Abs(box[1] - expected.MinY) > tol ||
            Math.Abs(box[2] - expected.MinZ) > tol || Math.Abs(box[3] - expected.MaxX) > tol ||
            Math.Abs(box[4] - expected.MaxY) > tol || Math.Abs(box[5] - expected.MaxZ) > tol)
        {
            return new BodyAabbVerification(false,
                $"新实体包围盒 ({box[0]:F3},{box[1]:F3},{box[2]:F3})–({box[3]:F3},{box[4]:F3},{box[5]:F3}) " +
                $"与预期 ({expected.MinX:F3},{expected.MinY:F3},{expected.MinZ:F3})–" +
                $"({expected.MaxX:F3},{expected.MaxY:F3},{expected.MaxZ:F3}) 不符", newBody.JournalIdentifier);
        }
        return new BodyAabbVerification(true, "", newBody.JournalIdentifier);
    }
}
