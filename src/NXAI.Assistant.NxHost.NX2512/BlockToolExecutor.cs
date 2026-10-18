using System.Globalization;
using System.Text;
using NXAI.Assistant.Core;
using NXOpen;
using NXOpen.Features;
using NXOpen.UF;

namespace NXAI.Assistant.NxHost.NX2512;

/// <summary>
/// In-process executor for the create_block tool. Must be called on the NX
/// UI thread (the chat form's confirmation path runs there).
///
/// Safety contract:
///   - acts only on the current work part; never calls SaveAs, never saves,
///     never renames or re-paths the part;
///   - validates parameters before touching the session;
///   - refuses to execute when the work part changed after user confirmation
///     (expectedWorkPartFullPath mismatch);
///   - refuses non-millimeter parts instead of guessing unit conversion;
///   - wraps the mutation in a visible UndoMark and rolls back on failure,
///     verifying the rollback restored the body count;
///   - verifies postconditions by querying real geometry (body count delta +
///     JournalIdentifier diff + bounding box) before reporting success;
///   - destroys the builder on all paths and appends non-sensitive failure
///     context to %TEMP%\NXAI_Assistant_operation_error.log.
/// </summary>
public sealed partial class NxToolExecutor : INxToolExecutor
{
    private const string OperationLogFileName = "NXAI_Assistant_operation_error.log";

    /// <summary>
    /// Absolute tolerance (mm) between requested and verified dimensions. NX
    /// receives round-trippable "R"-formatted expressions, so the achieved
    /// geometry matches far better than this; the margin only absorbs query
    /// and rounding noise in the verification path.
    /// </summary>
    internal const double DimensionToleranceMm = 0.01;

    public CreateBlockExecutionResult ExecuteCreateBlock(
        CreateBlockParameters parameters, string? expectedWorkPartFullPath)
    {
        string? validationError = BlockRequestValidator.ValidateWorkingPartRequest(
            parameters.OriginX, parameters.OriginY, parameters.OriginZ,
            parameters.Length, parameters.Width, parameters.Height);
        if (validationError != null)
            return CreateBlockExecutionResult.Failure(ToolExecutionStatus.InvalidParameters, validationError);

        Session session;
        Part? workPart;
        try
        {
            session = Session.GetSession();
            workPart = session.Parts.Work;
        }
        catch (Exception ex)
        {
            return CreateBlockExecutionResult.Failure(
                ToolExecutionStatus.ExecutionFailed, $"无法访问 NX 会话或工作部件：{ex.Message}");
        }

        if (workPart == null)
            return CreateBlockExecutionResult.Failure(
                ToolExecutionStatus.PreconditionsChanged, "当前没有活动工作部件，无法创建长方体。");

        string workPartIdentity = DescribePart(workPart);

        if (!string.IsNullOrEmpty(expectedWorkPartFullPath) &&
            !string.Equals(workPart.FullPath ?? "", expectedWorkPartFullPath, StringComparison.OrdinalIgnoreCase))
        {
            return CreateBlockExecutionResult.Failure(
                ToolExecutionStatus.PreconditionsChanged,
                $"工作部件已变化（确认时：{expectedWorkPartFullPath}；现在：{workPart.FullPath ?? "（未保存部件）"}）。" +
                "本次执行已中止，请重新发起请求并再次确认。",
                workPartIdentity: workPartIdentity);
        }

        if (workPart.PartUnits != BasePart.Units.Millimeters)
        {
            return CreateBlockExecutionResult.Failure(
                ToolExecutionStatus.PreconditionsChanged,
                $"当前工作部件单位是 {workPart.PartUnits}，本工具目前只支持毫米（mm）部件。" +
                "请在毫米单位的工作部件中重试。",
                workPartIdentity: workPartIdentity);
        }

        int bodiesBefore = CountBodies(workPart);
        HashSet<string> idsBefore = CollectBodyIds(workPart);

        Session.UndoMarkId markId = session.SetUndoMark(
            Session.MarkVisibility.Visible, "NXAI 创建长方体");

        BlockFeatureBuilder? blockBuilder = null;
        try
        {
            blockBuilder = workPart.Features.CreateBlockFeatureBuilder(default(Feature)!);
            blockBuilder.Type = BlockFeatureBuilder.Types.OriginAndEdgeLengths;
            // NXOpen parses these strings as expressions: invariant culture
            // ("12.5", never "12,5") and round-trippable "R" formatting.
            blockBuilder.SetOriginAndLengths(
                new Point3d(parameters.OriginX, parameters.OriginY, parameters.OriginZ),
                FormatLength(parameters.Length), FormatLength(parameters.Width),
                FormatLength(parameters.Height));

            blockBuilder.CommitFeature();
            session.UpdateManager.DoUpdate(markId);
            PhaseLog.Write("execute.block", "commit done");

            var verification = VerifyBlockCreated(workPart, bodiesBefore, idsBefore,
                parameters.Length, parameters.Width, parameters.Height);
            PhaseLog.Write("execute.block",
                $"verify: {(verification.Success ? "ok" : "FAILED " + verification.Reason)}");
            if (verification.Success)
            {
                return CreateBlockExecutionResult.Success(
                    verification.LengthMm, verification.WidthMm, verification.HeightMm,
                    $"已在工作部件中创建长方体，实测尺寸 " +
                    $"{verification.LengthMm:F3} × {verification.WidthMm:F3} × {verification.HeightMm:F3} 毫米" +
                    $"（请求 {parameters.Length} × {parameters.Width} × {parameters.Height} 毫米）。",
                    workPartIdentity);
            }

            // The created geometry does not match the request: undo rather than
            // report a success we cannot prove.
            (bool undone, string undoError) = Rollback(session, markId);
            LogFailure(new InvalidOperationException($"Postcondition failed: {verification.Reason}"),
                workPartIdentity);
            return CreateBlockExecutionResult.Failure(
                ToolExecutionStatus.VerificationFailed,
                $"已创建几何与请求不符（{verification.Reason}）。" +
                (undone ? "已回滚本次操作，部件恢复原状。" : $"回滚未完成：{undoError}。请在 NX 撤销列表中手动撤销。"),
                rolledBack: undone,
                workPartIdentity: workPartIdentity);
        }
        catch (Exception ex)
        {
            (bool undone, string undoError) = Rollback(session, markId);
            LogFailure(ex, workPartIdentity);
            return CreateBlockExecutionResult.Failure(
                ToolExecutionStatus.ExecutionFailed,
                $"{ex.GetType().Name}: {ex.Message}" +
                (undone ? "（本次操作已回滚，部件恢复原状。）"
                        : $"（回滚未完成：{undoError}。请在 NX 撤销列表中手动撤销。）"),
                rolledBack: undone,
                workPartIdentity: workPartIdentity);
        }
        finally
        {
            // NXOpen requires Destroy to release native builder resources.
            blockBuilder?.Destroy();
        }
    }

    private static string DescribePart(Part part) =>
        $"{part.Name}（{part.FullPath ?? "未保存部件"}，单位 {part.PartUnits}）";

    /// <summary>
    /// Shared precondition checks for all geometry tools: validation already
    /// ran in the caller; here the NX session, work part presence, the
    /// confirmed part identity and the millimeter-unit gate are enforced.
    /// Returns the work part and its display identity, or a failure result.
    /// </summary>
    private static bool TryResolveWorkPart(
        string? expectedWorkPartFullPath, string toolName,
        out Session session, out Part workPart, out string workPartIdentity,
        out CreateBlockExecutionResult? failure)
    {
        failure = null;
        session = null!;
        workPart = null!;
        workPartIdentity = "";
        try
        {
            session = Session.GetSession();
            workPart = session.Parts.Work;
        }
        catch (Exception ex)
        {
            failure = CreateBlockExecutionResult.Failure(
                ToolExecutionStatus.ExecutionFailed, $"无法访问 NX 会话或工作部件：{ex.Message}");
            return false;
        }

        if (workPart == null)
        {
            failure = CreateBlockExecutionResult.Failure(
                ToolExecutionStatus.PreconditionsChanged, $"当前没有活动工作部件，无法创建{toolName}。");
            return false;
        }

        workPartIdentity = DescribePart(workPart);

        if (!string.IsNullOrEmpty(expectedWorkPartFullPath) &&
            !string.Equals(workPart.FullPath ?? "", expectedWorkPartFullPath, StringComparison.OrdinalIgnoreCase))
        {
            failure = CreateBlockExecutionResult.Failure(
                ToolExecutionStatus.PreconditionsChanged,
                $"工作部件已变化（确认时：{expectedWorkPartFullPath}；现在：{workPart.FullPath ?? "（未保存部件）"}）。" +
                "本次执行已中止，请重新发起请求并再次确认。",
                workPartIdentity: workPartIdentity);
            return false;
        }

        if (workPart.PartUnits != BasePart.Units.Millimeters)
        {
            failure = CreateBlockExecutionResult.Failure(
                ToolExecutionStatus.PreconditionsChanged,
                $"当前工作部件单位是 {workPart.PartUnits}，本工具目前只支持毫米（mm）部件。" +
                "请在毫米单位的工作部件中重试。",
                workPartIdentity: workPartIdentity);
            return false;
        }
        return true;
    }


    /// <summary>
    /// Creates a cylinder (axis = work part +Z, origin = bottom-face center)
    /// in the current work part. Same safety contract as the block: no
    /// SaveAs/auto-save/re-path, precondition checks before NXOpen, UndoMark
    /// rollback, builder Destroy, and postcondition verification against the
    /// real bounding box (XY extents = diameter, Z extent = height).
    /// </summary>
    public CreateBlockExecutionResult ExecuteCreateCylinder(
        CreateCylinderParameters parameters, string? expectedWorkPartFullPath)
    {
        string? validationError = BlockRequestValidator.ValidateWorkingPartRequest(
            parameters.OriginX, parameters.OriginY, parameters.OriginZ,
            parameters.Diameter, parameters.Diameter, parameters.Height);
        if (validationError != null)
            return CreateBlockExecutionResult.Failure(ToolExecutionStatus.InvalidParameters, validationError);

        // Face-relative placement resolves the target face fresh at execution
        // time; the model's origin values are ignored (the face decides), and
        // the request is refused rather than silently falling back to the
        // global origin.
        if (parameters.Placement == CylinderPlacement.SelectedFaceCenter)
            return ExecuteCreateCylinderOnSelectedFace(parameters, expectedWorkPartFullPath);

        Session session;
        Part? workPart;
        try
        {
            session = Session.GetSession();
            workPart = session.Parts.Work;
        }
        catch (Exception ex)
        {
            return CreateBlockExecutionResult.Failure(
                ToolExecutionStatus.ExecutionFailed, $"无法访问 NX 会话或工作部件：{ex.Message}");
        }

        if (workPart == null)
            return CreateBlockExecutionResult.Failure(
                ToolExecutionStatus.PreconditionsChanged, "当前没有活动工作部件，无法创建圆柱体。");

        string workPartIdentity = DescribePart(workPart);

        if (!string.IsNullOrEmpty(expectedWorkPartFullPath) &&
            !string.Equals(workPart.FullPath ?? "", expectedWorkPartFullPath, StringComparison.OrdinalIgnoreCase))
        {
            return CreateBlockExecutionResult.Failure(
                ToolExecutionStatus.PreconditionsChanged,
                $"工作部件已变化（确认时：{expectedWorkPartFullPath}；现在：{workPart.FullPath ?? "（未保存部件）"}）。" +
                "本次执行已中止，请重新发起请求并再次确认。",
                workPartIdentity: workPartIdentity);
        }

        if (workPart.PartUnits != BasePart.Units.Millimeters)
        {
            return CreateBlockExecutionResult.Failure(
                ToolExecutionStatus.PreconditionsChanged,
                $"当前工作部件单位是 {workPart.PartUnits}，本工具目前只支持毫米（mm）部件。" +
                "请在毫米单位的工作部件中重试。",
                workPartIdentity: workPartIdentity);
        }

        int bodiesBefore = CountBodies(workPart);
        HashSet<string> idsBefore = CollectBodyIds(workPart);

        Session.UndoMarkId markId = session.SetUndoMark(
            Session.MarkVisibility.Visible, "NXAI 创建圆柱体");

        CylinderBuilder? cylinderBuilder = null;
        try
        {
            cylinderBuilder = workPart.Features.CreateCylinderBuilder(default(Feature)!);
            cylinderBuilder.Type = CylinderBuilder.Types.AxisDiameterAndHeight;
            // Diameter/Height are NXOpen Expression objects: set the value with
            // invariant round-trippable formatting (same convention as the block).
            cylinderBuilder.Diameter.SetFormula(FormatLength(parameters.Diameter));
            cylinderBuilder.Height.SetFormula(FormatLength(parameters.Height));
            cylinderBuilder.Origin = new Point3d(
                parameters.OriginX, parameters.OriginY, parameters.OriginZ);
            // Axis direction: leave the builder default (+Z); ReverseDirection
            // stays false. The model cannot choose an arbitrary axis in Phase 3.

            cylinderBuilder.CommitFeature();
            session.UpdateManager.DoUpdate(markId);

            var verification = VerifyCylinderCreated(workPart, bodiesBefore, idsBefore,
                parameters.Diameter, parameters.Height);
            if (verification.Success)
            {
                return CreateBlockExecutionResult.Success(
                    verification.LengthMm, verification.WidthMm, verification.HeightMm,
                    $"已在工作部件中创建圆柱体，实测包围盒 " +
                    $"{verification.LengthMm:F3} × {verification.WidthMm:F3} × {verification.HeightMm:F3} 毫米" +
                    $"（请求直径 {parameters.Diameter} × 高 {parameters.Height} 毫米，轴向 +Z）。",
                    workPartIdentity);
            }

            (bool undone, string undoError) = Rollback(session, markId, "NXAI 创建圆柱体");
            LogFailure(new InvalidOperationException($"Postcondition failed: {verification.Reason}"),
                workPartIdentity, "CreateCylinder");
            return CreateBlockExecutionResult.Failure(
                ToolExecutionStatus.VerificationFailed,
                $"已创建几何与请求不符（{verification.Reason}）。" +
                (undone ? "已回滚本次操作，部件恢复原状。" : $"回滚未完成：{undoError}。请在 NX 撤销列表中手动撤销。"),
                rolledBack: undone,
                workPartIdentity: workPartIdentity);
        }
        catch (Exception ex)
        {
            (bool undone, string undoError) = Rollback(session, markId, "NXAI 创建圆柱体");
            LogFailure(ex, workPartIdentity, "CreateCylinder");
            return CreateBlockExecutionResult.Failure(
                ToolExecutionStatus.ExecutionFailed,
                $"{ex.GetType().Name}: {ex.Message}" +
                (undone ? "（本次操作已回滚，部件恢复原状。）"
                        : $"（回滚未完成：{undoError}。请在 NX 撤销列表中手动撤销。）"),
                rolledBack: undone,
                workPartIdentity: workPartIdentity);
        }
        finally
        {
            cylinderBuilder?.Destroy();
        }
    }

    private static int CountBodies(Part part)
    {
        int count = 0;
        foreach (object _ in part.Bodies)
            count++;
        return count;
    }

    private static HashSet<string> CollectBodyIds(Part part)
    {
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (object item in part.Bodies)
        {
            if (item is Body body)
                ids.Add(body.JournalIdentifier);
        }
        return ids;
    }

    private static (bool Undone, string UndoError) Rollback(
        Session session, Session.UndoMarkId markId, string undoName)
    {
        try
        {
            session.UndoToMark(markId, undoName);
            PhaseLog.Write("execute.rollback", $"ok: {undoName}");
            return (true, "");
        }
        catch (Exception ex)
        {
            PhaseLog.Write("execute.rollback", $"FAILED: {undoName}: {ex.Message}");
            return (false, ex.Message);
        }
    }

    private static (bool Undone, string UndoError) Rollback(Session session, Session.UndoMarkId markId) =>
        Rollback(session, markId, "NXAI 创建长方体");

    private readonly record struct VerificationResult(
        bool Success, double LengthMm, double WidthMm, double HeightMm, string Reason);

    /// <summary>
    /// Postcondition check against real geometry: exactly one body must have
    /// been added, and its bounding box must match the requested dimensions
    /// within <see cref="DimensionToleranceMm"/>. Never trusts the absence of
    /// exceptions as success.
    /// </summary>
    private static VerificationResult VerifyBlockCreated(
        Part workPart, int bodiesBefore, HashSet<string> idsBefore,
        double expectedLength, double expectedWidth, double expectedHeight)
    {
        int bodiesAfter = CountBodies(workPart);
        if (bodiesAfter != bodiesBefore + 1)
        {
            return new VerificationResult(false, 0, 0, 0,
                $"实体数量为 {bodiesAfter}，预期 {bodiesBefore + 1}");
        }

        Body? newBody = null;
        foreach (object item in workPart.Bodies)
        {
            if (item is Body body && !idsBefore.Contains(body.JournalIdentifier))
            {
                if (newBody != null)
                {
                    return new VerificationResult(false, 0, 0, 0, "发现多个新实体，无法确定本次创建结果");
                }
                newBody = body;
            }
        }
        if (newBody == null)
        {
            return new VerificationResult(false, 0, 0, 0, "未找到本次创建的新实体");
        }

        if (!TryGetBoundingBoxMm(workPart, newBody, out double lx, out double wy, out double lz))
        {
            return new VerificationResult(false, 0, 0, 0, "无法读取新实体的包围盒");
        }

        if (!WithinTolerance(lx, expectedLength) || !WithinTolerance(wy, expectedWidth) ||
            !WithinTolerance(lz, expectedHeight))
        {
            return new VerificationResult(false, lx, wy, lz,
                $"包围盒 {lx:F3} × {wy:F3} × {lz:F3} 毫米与请求 " +
                $"{expectedLength} × {expectedWidth} × {expectedHeight} 毫米不符");
        }

        return new VerificationResult(true, lx, wy, lz, "");
    }

    private static bool WithinTolerance(double actual, double expected) =>
        Math.Abs(actual - expected) <= DimensionToleranceMm;

    /// <summary>
    /// Creates a right circular cone (TopDiameter = 0, apex on the axis) or a
    /// truncated cone (0 < TopDiameter < BaseDiameter) along the work part's
    /// +Z axis, origin = bottom-face center. Uses ConeBuilder
    /// DiametersAndHeight — the documented type where a zero top diameter is
    /// the supported representation of a true cone. Placement: explicit Axis
    /// at the requested base center, direction +Z (the model cannot choose an
    /// arbitrary axis).
    /// </summary>
    public CreateBlockExecutionResult ExecuteCreateCone(
        CreateConeParameters parameters, string? expectedWorkPartFullPath)
    {
        string? validationError = BlockRequestValidator.ValidateWorkingPartRequest(
            parameters.OriginX, parameters.OriginY, parameters.OriginZ,
            parameters.BaseDiameter, parameters.BaseDiameter, parameters.Height);
        if (validationError != null)
            return CreateBlockExecutionResult.Failure(ToolExecutionStatus.InvalidParameters, validationError);

        if (parameters.TopDiameter < 0 || parameters.TopDiameter >= parameters.BaseDiameter)
        {
            return CreateBlockExecutionResult.Failure(ToolExecutionStatus.InvalidParameters,
                $"top_diameter（{parameters.TopDiameter} 毫米）非法：必须为 0（标准圆锥）或小于 base_diameter" +
                $"（{parameters.BaseDiameter} 毫米）。");
        }

        string shapeName = parameters.TopDiameter > 0 ? "圆台" : "圆锥体";

        if (!TryResolveWorkPart(expectedWorkPartFullPath, shapeName,
                out Session session, out Part workPart, out string workPartIdentity,
                out CreateBlockExecutionResult? failure))
        {
            return failure!;
        }

        int bodiesBefore = CountBodies(workPart);
        HashSet<string> idsBefore = CollectBodyIds(workPart);

        Session.UndoMarkId markId = session.SetUndoMark(
            Session.MarkVisibility.Visible, $"NXAI 创建{shapeName}");

        ConeBuilder? coneBuilder = null;
        try
        {
            coneBuilder = workPart.Features.CreateConeBuilder(default(Cone)!);
            coneBuilder.Type = ConeBuilder.Types.DiametersAndHeight;
            coneBuilder.BaseDiameter.SetFormula(FormatLength(parameters.BaseDiameter));
            coneBuilder.TopDiameter.SetFormula(FormatLength(parameters.TopDiameter));
            coneBuilder.Height.SetFormula(FormatLength(parameters.Height));
            coneBuilder.Axis = workPart.Axes.CreateAxis(
                workPart.Points.CreatePoint(new Point3d(
                    parameters.OriginX, parameters.OriginY, parameters.OriginZ)),
                workPart.Directions.CreateDirection(
                    new Point3d(parameters.OriginX, parameters.OriginY, parameters.OriginZ),
                    new Vector3d(0, 0, 1), SmartObject.UpdateOption.WithinModeling),
                SmartObject.UpdateOption.WithinModeling);

            coneBuilder.CommitFeature();
            session.UpdateManager.DoUpdate(markId);

            var verification = VerifyConeCreated(workPart, bodiesBefore, idsBefore,
                parameters.BaseDiameter, parameters.Height);
            if (verification.Success)
            {
                string shapeDesc = parameters.TopDiameter > 0
                    ? $"圆台（底面直径 {parameters.BaseDiameter} × 顶部直径 {parameters.TopDiameter} × 高 {parameters.Height} 毫米）"
                    : $"圆锥体（底面直径 {parameters.BaseDiameter} × 高 {parameters.Height} 毫米）";
                return CreateBlockExecutionResult.Success(
                    verification.LengthMm, verification.WidthMm, verification.HeightMm,
                    $"已在工作部件中创建{shapeDesc}，实测包围盒 " +
                    $"{verification.LengthMm:F3} × {verification.WidthMm:F3} × {verification.HeightMm:F3} 毫米（轴向 +Z）。",
                    workPartIdentity);
            }

            (bool undone, string undoError) = Rollback(session, markId, $"NXAI 创建{shapeName}");
            LogFailure(new InvalidOperationException($"Postcondition failed: {verification.Reason}"),
                workPartIdentity, "CreateCone");
            return CreateBlockExecutionResult.Failure(
                ToolExecutionStatus.VerificationFailed,
                $"已创建几何与请求不符（{verification.Reason}）。" +
                (undone ? "已回滚本次操作，部件恢复原状。" : $"回滚未完成：{undoError}。请在 NX 撤销列表中手动撤销。"),
                rolledBack: undone,
                workPartIdentity: workPartIdentity);
        }
        catch (Exception ex)
        {
            (bool undone, string undoError) = Rollback(session, markId, $"NXAI 创建{shapeName}");
            LogFailure(ex, workPartIdentity, "CreateCone");
            return CreateBlockExecutionResult.Failure(
                ToolExecutionStatus.ExecutionFailed,
                $"{ex.GetType().Name}: {ex.Message}" +
                (undone ? "（本次操作已回滚，部件恢复原状。）"
                        : $"（回滚未完成：{undoError}。请在 NX 撤销列表中手动撤销。）"),
                rolledBack: undone,
                workPartIdentity: workPartIdentity);
        }
        finally
        {
            coneBuilder?.Destroy();
        }
    }

    /// <summary>
    /// Creates a real spherical solid (CenterPointAndDiameter) in the current
    /// work part. Origin = sphere center. Same safety contract as the other
    /// tools; verification checks the bounding box is [d, d, d].
    /// </summary>
    public CreateBlockExecutionResult ExecuteCreateSphere(
        CreateSphereParameters parameters, string? expectedWorkPartFullPath)
    {
        string? validationError = BlockRequestValidator.ValidateWorkingPartRequest(
            parameters.CenterX, parameters.CenterY, parameters.CenterZ,
            parameters.Diameter, parameters.Diameter, parameters.Diameter);
        if (validationError != null)
            return CreateBlockExecutionResult.Failure(ToolExecutionStatus.InvalidParameters, validationError);

        if (!TryResolveWorkPart(expectedWorkPartFullPath, "球体",
                out Session session, out Part workPart, out string workPartIdentity,
                out CreateBlockExecutionResult? failure))
        {
            return failure!;
        }

        int bodiesBefore = CountBodies(workPart);
        HashSet<string> idsBefore = CollectBodyIds(workPart);

        Session.UndoMarkId markId = session.SetUndoMark(
            Session.MarkVisibility.Visible, "NXAI 创建球体");

        SphereBuilder? sphereBuilder = null;
        try
        {
            sphereBuilder = workPart.Features.CreateSphereBuilder(default(Sphere)!);
            sphereBuilder.Type = SphereBuilder.Types.CenterPointAndDiameter;
            sphereBuilder.CenterPoint = workPart.Points.CreatePoint(new Point3d(
                parameters.CenterX, parameters.CenterY, parameters.CenterZ));
            sphereBuilder.Diameter.SetFormula(FormatLength(parameters.Diameter));

            sphereBuilder.CommitFeature();
            session.UpdateManager.DoUpdate(markId);

            var verification = VerifySphereCreated(workPart, bodiesBefore, idsBefore,
                parameters.Diameter);
            if (verification.Success)
            {
                return CreateBlockExecutionResult.Success(
                    verification.LengthMm, verification.WidthMm, verification.HeightMm,
                    $"已在工作部件中创建球体，实测包围盒 " +
                    $"{verification.LengthMm:F3} × {verification.WidthMm:F3} × {verification.HeightMm:F3} 毫米" +
                    $"（请求直径 {parameters.Diameter} 毫米）。",
                    workPartIdentity);
            }

            (bool undone, string undoError) = Rollback(session, markId, "NXAI 创建球体");
            LogFailure(new InvalidOperationException($"Postcondition failed: {verification.Reason}"),
                workPartIdentity, "CreateSphere");
            return CreateBlockExecutionResult.Failure(
                ToolExecutionStatus.VerificationFailed,
                $"已创建几何与请求不符（{verification.Reason}）。" +
                (undone ? "已回滚本次操作，部件恢复原状。" : $"回滚未完成：{undoError}。请在 NX 撤销列表中手动撤销。"),
                rolledBack: undone,
                workPartIdentity: workPartIdentity);
        }
        catch (Exception ex)
        {
            (bool undone, string undoError) = Rollback(session, markId, "NXAI 创建球体");
            LogFailure(ex, workPartIdentity, "CreateSphere");
            return CreateBlockExecutionResult.Failure(
                ToolExecutionStatus.ExecutionFailed,
                $"{ex.GetType().Name}: {ex.Message}" +
                (undone ? "（本次操作已回滚，部件恢复原状。）"
                        : $"（回滚未完成：{undoError}。请在 NX 撤销列表中手动撤销。）"),
                rolledBack: undone,
                workPartIdentity: workPartIdentity);
        }
        finally
        {
            sphereBuilder?.Destroy();
        }
    }

    /// <summary>
    /// Cone postcondition check: exactly one new body (by identity diff),
    /// bounding box XY extents = base diameter, Z extent = height. The box
    /// alone cannot distinguish a cone from a cylinder — the shape check
    /// (solid type / topology) is a real-NX acceptance item; see the phase
    /// report for what the user must confirm live.
    /// </summary>
    private static VerificationResult VerifyConeCreated(
        Part workPart, int bodiesBefore, HashSet<string> idsBefore,
        double expectedBaseDiameter, double expectedHeight)
    {
        int bodiesAfter = CountBodies(workPart);
        if (bodiesAfter != bodiesBefore + 1)
        {
            return new VerificationResult(false, 0, 0, 0,
                $"实体数量为 {bodiesAfter}，预期 {bodiesBefore + 1}");
        }

        Body? newBody = null;
        foreach (object item in workPart.Bodies)
        {
            if (item is Body body && !idsBefore.Contains(body.JournalIdentifier))
            {
                if (newBody != null)
                {
                    return new VerificationResult(false, 0, 0, 0, "发现多个新实体，无法确定本次创建结果");
                }
                newBody = body;
            }
        }
        if (newBody == null)
        {
            return new VerificationResult(false, 0, 0, 0, "未找到本次创建的新实体");
        }

        if (!TryGetBoundingBoxMm(workPart, newBody, out double lx, out double wy, out double lz))
        {
            return new VerificationResult(false, 0, 0, 0, "无法读取新实体的包围盒");
        }

        if (!WithinTolerance(lx, expectedBaseDiameter) || !WithinTolerance(wy, expectedBaseDiameter) ||
            !WithinTolerance(lz, expectedHeight))
        {
            return new VerificationResult(false, lx, wy, lz,
                $"包围盒 {lx:F3} × {wy:F3} × {lz:F3} 毫米与请求 底面直径{expectedBaseDiameter} × 高{expectedHeight} 毫米不符");
        }

        return new VerificationResult(true, lx, wy, lz, "");
    }

    /// <summary>
    /// Sphere postcondition check: exactly one new body (by identity diff),
    /// bounding box [d, d, d]. Real sphericity is confirmed by the user in
    /// the live acceptance step (the box alone cannot distinguish a sphere
    /// from a cube, but the CenterPointAndDiameter sphere builder is the
    /// verified real-sphere API, not a mesh approximation).
    /// </summary>
    private static VerificationResult VerifySphereCreated(
        Part workPart, int bodiesBefore, HashSet<string> idsBefore,
        double expectedDiameter)
    {
        int bodiesAfter = CountBodies(workPart);
        if (bodiesAfter != bodiesBefore + 1)
        {
            return new VerificationResult(false, 0, 0, 0,
                $"实体数量为 {bodiesAfter}，预期 {bodiesBefore + 1}");
        }

        Body? newBody = null;
        foreach (object item in workPart.Bodies)
        {
            if (item is Body body && !idsBefore.Contains(body.JournalIdentifier))
            {
                if (newBody != null)
                {
                    return new VerificationResult(false, 0, 0, 0, "发现多个新实体，无法确定本次创建结果");
                }
                newBody = body;
            }
        }
        if (newBody == null)
        {
            return new VerificationResult(false, 0, 0, 0, "未找到本次创建的新实体");
        }

        if (!TryGetBoundingBoxMm(workPart, newBody, out double lx, out double wy, out double lz))
        {
            return new VerificationResult(false, 0, 0, 0, "无法读取新实体的包围盒");
        }

        if (!WithinTolerance(lx, expectedDiameter) || !WithinTolerance(wy, expectedDiameter) ||
            !WithinTolerance(lz, expectedDiameter))
        {
            return new VerificationResult(false, lx, wy, lz,
                $"包围盒 {lx:F3} × {wy:F3} × {lz:F3} 毫米与请求 直径{expectedDiameter} 毫米不符");
        }

        return new VerificationResult(true, lx, wy, lz, "");
    }

    /// <summary>
    /// Cylinder postcondition check: exactly one new body (by identity diff),
    /// bounding box XY extents = diameter, Z extent = height. A cylinder's
    /// axis-aligned bounding box is exactly [d, d, h] for axis +Z.
    /// </summary>
    /// <summary>
    /// Face-relative cylinder: resolves the single selected planar face from
    /// the current selection, computes its representative center and unit
    /// normal via UF_MODL_ask_face_props at the UV midpoint, verifies the
    /// point actually lies on the face (rejecting holed/concave faces), and
    /// creates the cylinder with bottom circle at the face center and axis
    /// along the outward normal. Refuses (never falls back to global origin)
    /// when the selection is empty, ambiguous, non-face, or non-planar.
    /// </summary>
    private CreateBlockExecutionResult ExecuteCreateCylinderOnSelectedFace(
        CreateCylinderParameters parameters, string? expectedWorkPartFullPath)
    {
        if (!TryResolveWorkPart(expectedWorkPartFullPath, "面相对圆柱",
                out Session session, out Part workPart, out string workPartIdentity,
                out CreateBlockExecutionResult? failure))
        {
            return failure!;
        }

        var faces = new List<Face>();
        try
        {
            var uiSelection = NXOpen.UI.GetUI().SelectionManager;
            int count = uiSelection.GetNumSelectedObjects();
            for (int i = 0; i < count; i++)
            {
                if (uiSelection.GetSelectedTaggedObject(i) is Face f)
                    faces.Add(f);
            }
        }
        catch (Exception ex)
        {
            return CreateBlockExecutionResult.Failure(
                ToolExecutionStatus.PreconditionsChanged,
                $"读取当前选择失败：{ex.Message}", workPartIdentity: workPartIdentity);
        }

        if (faces.Count == 0)
        {
            return CreateBlockExecutionResult.Failure(
                ToolExecutionStatus.PreconditionsChanged,
                "面相对放置需要恰好选中一个平面，但当前没有选中任何面。请在 NX 中选择目标面后" +
                "重新发起请求，或改用默认的 global_origin 模式。",
                workPartIdentity: workPartIdentity);
        }
        if (faces.Count > 1)
        {
            return CreateBlockExecutionResult.Failure(
                ToolExecutionStatus.PreconditionsChanged,
                $"面相对放置需要恰好选中一个平面，但当前选中了 {faces.Count} 个面（存在歧义）。" +
                "请只保留一个目标面后重新发起请求。",
                workPartIdentity: workPartIdentity);
        }
        var targetFace = faces[0];
        PhaseLog.Write("execute.cylinder_face",
            $"target resolved: 1 face, workPart={workPartIdentity}");
        string faceType;
        try
        {
            faceType = targetFace.SolidFaceType.ToString().ToLowerInvariant();
        }
        catch (Exception ex)
        {
            return CreateBlockExecutionResult.Failure(
                ToolExecutionStatus.PreconditionsChanged,
                $"无法读取目标面类型：{ex.Message}", workPartIdentity: workPartIdentity);
        }
        if (faceType != "planar")
        {
            return CreateBlockExecutionResult.Failure(
                ToolExecutionStatus.PreconditionsChanged,
                $"选中的目标是 {faceType} 面；面相对圆柱目前只支持平面。请选择一个平面，" +
                "或改用默认的 global_origin 模式。",
                workPartIdentity: workPartIdentity);
        }

        double[] center, normal;
        try
        {
            var ufSession = UFSession.GetUFSession();
            double[] uv = new double[4];
            // General-face UV bounds (UF_MODL_ask_face_uv_minmax). Never
            // UFEvalsf — the B-surface-only evaluator access-violates on
            // planar faces (crash root cause, see NxContextReader doc).
            ufSession.Modl.AskFaceUvMinmax(targetFace.Tag, uv);
            double uMid = (uv[0] + uv[1]) / 2.0;
            double vMid = (uv[2] + uv[3]) / 2.0;
            center = new double[3];
            normal = new double[3];
            var u1 = new double[3];
            var v1 = new double[3];
            var u2 = new double[3];
            var v2 = new double[3];
            var unitNorm = new double[3];
            var radii = new double[2];
            // Managed signature (reflection-verified):
            // AskFaceProps(face_id, param[2], point[3], u1[3], v1[3],
            //              u2[3], v2[3], unit_norm[3], radii[2])
            ufSession.Modl.AskFaceProps(targetFace.Tag,
                new double[] { uMid, vMid }, center, u1, v1, u2, v2, unitNorm, radii);
            normal = unitNorm;
            PhaseLog.Write("execute.cylinder_face_geometry",
                $"ok center=({center[0]:0.###},{center[1]:0.###},{center[2]:0.###}) " +
                $"normal=({normal[0]:0.###},{normal[1]:0.###},{normal[2]:0.###})");
        }
        catch (Exception ex)
        {
            PhaseLog.Write("execute.cylinder_face_geometry", $"FAILED: {ex.GetType().Name}: {ex.Message}");
            return CreateBlockExecutionResult.Failure(
                ToolExecutionStatus.PreconditionsChanged,
                $"无法可靠计算目标面中心与法向：{ex.Message}。请选择更简单的平面，" +
                "或改用 global_origin 模式。",
                workPartIdentity: workPartIdentity);
        }

        // KNOWN LIMITATION (honest scope): for a simple rectangular planar
        // face the UV midpoint is the geometric center. For faces with holes
        // or concave boundaries the UV midpoint may fall outside the actual
        // face. NX2512's managed wrapper exposes no reliable point-on-face
        // containment query, so this version does NOT claim on-face
        // verification: the confirmation dialog shows the computed center and
        // the user visually confirms placement; the live acceptance step
        // covers holed/concave faces explicitly. No fabricated geometry.

        int bodiesBefore = CountBodies(workPart);
        HashSet<string> idsBefore = CollectBodyIds(workPart);

        Session.UndoMarkId markId = session.SetUndoMark(
            Session.MarkVisibility.Visible, "NXAI 创建圆柱体（选中面）");

        CylinderBuilder? cylinderBuilder = null;
        try
        {
            PhaseLog.Write("execute.cylinder_face", "builder create begin");
            cylinderBuilder = workPart.Features.CreateCylinderBuilder(default(Feature)!);
            cylinderBuilder.Type = CylinderBuilder.Types.AxisDiameterAndHeight;
            cylinderBuilder.Diameter.SetFormula(FormatLength(parameters.Diameter));
            cylinderBuilder.Height.SetFormula(FormatLength(parameters.Height));
            // Bottom circle centered at the face center; axis along the face
            // outward normal.
            cylinderBuilder.Origin = new Point3d(center[0], center[1], center[2]);
            cylinderBuilder.Direction = new Vector3d(normal[0], normal[1], normal[2]);

            cylinderBuilder.CommitFeature();
            session.UpdateManager.DoUpdate(markId);
            PhaseLog.Write("execute.cylinder_face", "commit done");

            var verification = VerifyFaceCylinderCreated(
                workPart, bodiesBefore, idsBefore, center, parameters.Diameter, parameters.Height);
            PhaseLog.Write("execute.cylinder_face",
                $"verify: {(verification.Success ? "ok" : "FAILED " + verification.Reason)}");
            if (verification.Success)
            {
                return CreateBlockExecutionResult.Success(
                    parameters.Diameter, parameters.Diameter, parameters.Height,
                    $"已在选中面的中心创建圆柱体（面中心 " +
                    $"({center[0]:F3}, {center[1]:F3}, {center[2]:F3})，轴向沿面法向 " +
                    $"({normal[0]:F3}, {normal[1]:F3}, {normal[2]:F3})，" +
                    $"直径 {parameters.Diameter} × 高 {parameters.Height} 毫米）。",
                    workPartIdentity);
            }

            (bool undone, string undoError) = Rollback(session, markId, "NXAI 创建圆柱体（选中面）");
            LogFailure(new InvalidOperationException($"Postcondition failed: {verification.Reason}"),
                workPartIdentity, "CreateCylinderOnFace");
            return CreateBlockExecutionResult.Failure(
                ToolExecutionStatus.VerificationFailed,
                $"已创建几何与请求不符（{verification.Reason}）。" +
                (undone ? "已回滚本次操作，部件恢复原状。" : $"回滚未完成：{undoError}。请在 NX 撤销列表中手动撤销。"),
                rolledBack: undone,
                workPartIdentity: workPartIdentity);
        }
        catch (Exception ex)
        {
            (bool undone, string undoError) = Rollback(session, markId, "NXAI 创建圆柱体（选中面）");
            LogFailure(ex, workPartIdentity, "CreateCylinderOnFace");
            return CreateBlockExecutionResult.Failure(
                ToolExecutionStatus.ExecutionFailed,
                $"{ex.GetType().Name}: {ex.Message}" +
                (undone ? "（本次操作已回滚，部件恢复原状。）"
                        : $"（回滚未完成：{undoError}。请在 NX 撤销列表中手动撤销。）"),
                rolledBack: undone,
                workPartIdentity: workPartIdentity);
        }
        finally
        {
            cylinderBuilder?.Destroy();
            PhaseLog.Write("execute.cylinder_face", "builder destroyed");
        }
    }

    /// <summary>
    /// Face-cylinder postcondition: exactly one new body, its bounding box
    /// containing the face center, and every extent within the diagonal bound
    /// of an arbitrarily oriented cylinder (sqrt(d²+h²)). Exact axis-normal
    /// orientation is confirmed in the live acceptance step — the box alone
    /// cannot prove orientation for a tilted cylinder.
    /// </summary>
    private static VerificationResult VerifyFaceCylinderCreated(
        Part workPart, int bodiesBefore, HashSet<string> idsBefore,
        double[] faceCenter, double diameter, double height)
    {
        int bodiesAfter = CountBodies(workPart);
        if (bodiesAfter != bodiesBefore + 1)
        {
            return new VerificationResult(false, 0, 0, 0,
                $"实体数量为 {bodiesAfter}，预期 {bodiesBefore + 1}");
        }

        Body? newBody = null;
        foreach (object item in workPart.Bodies)
        {
            if (item is Body body && !idsBefore.Contains(body.JournalIdentifier))
            {
                if (newBody != null)
                    return new VerificationResult(false, 0, 0, 0, "发现多个新实体，无法确定本次创建结果");
                newBody = body;
            }
        }
        if (newBody == null)
            return new VerificationResult(false, 0, 0, 0, "未找到本次创建的新实体");

        try
        {
            var ufSession = UFSession.GetUFSession();
            double[] box = new double[6];
            ufSession.Modl.AskBoundingBox(newBody.Tag, box);
            double cx = faceCenter[0], cy = faceCenter[1], cz = faceCenter[2];
            if (cx < box[0] - 1e-3 || cx > box[3] + 1e-3 ||
                cy < box[1] - 1e-3 || cy > box[4] + 1e-3 ||
                cz < box[2] - 1e-3 || cz > box[5] + 1e-3)
            {
                return new VerificationResult(false, 0, 0, 0,
                    $"新实体包围盒不包含面中心 ({cx:F3}, {cy:F3}, {cz:F3})");
            }
            double maxDiag = Math.Sqrt(diameter * diameter + height * height);
            double lx = box[3] - box[0], wy = box[4] - box[1], lz = box[5] - box[2];
            if (lx > maxDiag + 1e-3 || wy > maxDiag + 1e-3 || lz > maxDiag + 1e-3)
            {
                return new VerificationResult(false, lx, wy, lz,
                    $"新实体包围盒 {lx:F3} × {wy:F3} × {lz:F3} 超出面相对圆柱的尺寸上限");
            }
            return new VerificationResult(true, lx, wy, lz, "");
        }
        catch (Exception ex)
        {
            return new VerificationResult(false, 0, 0, 0, $"无法读取新实体包围盒：{ex.Message}");
        }
    }

    private static VerificationResult VerifyCylinderCreated(
        Part workPart, int bodiesBefore, HashSet<string> idsBefore,
        double expectedDiameter, double expectedHeight)
    {
        int bodiesAfter = CountBodies(workPart);
        if (bodiesAfter != bodiesBefore + 1)
        {
            return new VerificationResult(false, 0, 0, 0,
                $"实体数量为 {bodiesAfter}，预期 {bodiesBefore + 1}");
        }

        Body? newBody = null;
        foreach (object item in workPart.Bodies)
        {
            if (item is Body body && !idsBefore.Contains(body.JournalIdentifier))
            {
                if (newBody != null)
                {
                    return new VerificationResult(false, 0, 0, 0, "发现多个新实体，无法确定本次创建结果");
                }
                newBody = body;
            }
        }
        if (newBody == null)
        {
            return new VerificationResult(false, 0, 0, 0, "未找到本次创建的新实体");
        }

        if (!TryGetBoundingBoxMm(workPart, newBody, out double lx, out double wy, out double lz))
        {
            return new VerificationResult(false, 0, 0, 0, "无法读取新实体的包围盒");
        }

        if (!WithinTolerance(lx, expectedDiameter) || !WithinTolerance(wy, expectedDiameter) ||
            !WithinTolerance(lz, expectedHeight))
        {
            return new VerificationResult(false, lx, wy, lz,
                $"包围盒 {lx:F3} × {wy:F3} × {lz:F3} 毫米与请求 直径{expectedDiameter} × 高{expectedHeight} 毫米不符");
        }

        return new VerificationResult(true, lx, wy, lz, "");
    }

    /// <summary>
    /// Reads the body's axis-aligned bounding box via UF_MODL_ask_bounding_box.
    /// Returns the dimension deltas in millimeters (the caller has already
    /// gated the part to millimeter units).
    /// </summary>
    private static bool TryGetBoundingBoxMm(Part workPart, Body body,
        out double lengthX, out double widthY, out double heightZ)
    {
        lengthX = widthY = heightZ = 0;
        try
        {
            var ufSession = UFSession.GetUFSession();
            double[] box = new double[6];
            ufSession.Modl.AskBoundingBox(body.Tag, box);
            // box = [xmin, ymin, zmin, xmax, ymax, zmax] in part units (mm here).
            lengthX = box[3] - box[0];
            widthY = box[4] - box[1];
            heightZ = box[5] - box[2];
            return double.IsFinite(lengthX) && double.IsFinite(widthY) && double.IsFinite(heightZ);
        }
        catch
        {
            return false;
        }
    }

    private static string FormatLength(double value) =>
        value.ToString("R", CultureInfo.InvariantCulture);

    /// <summary>
    /// Appends failure context (exception + work part identity) to
    /// %TEMP%\NXAI_Assistant_operation_error.log. Never throws and never logs
    /// credentials (this tool handles none).
    /// </summary>
    private static void LogFailure(Exception ex, string workPartIdentity) =>
        LogFailure(ex, workPartIdentity, "CreateBlock");

    private static void LogFailure(Exception ex, string workPartIdentity, string operation)
    {
        try
        {
            string logPath = Path.Combine(Path.GetTempPath(), OperationLogFileName);
            string record =
                $"{new string('=', 60)}{Environment.NewLine}" +
                $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] {operation} failed " +
                $"(work part: {workPartIdentity}){Environment.NewLine}" +
                $"{ex}{Environment.NewLine}{Environment.NewLine}";
            File.AppendAllText(logPath, record, Encoding.UTF8);
        }
        catch
        {
            // Diagnostics must never mask the failure being reported.
        }
    }
}
