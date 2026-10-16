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
public sealed class NxToolExecutor : INxToolExecutor
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

            var verification = VerifyBlockCreated(workPart, bodiesBefore, idsBefore,
                parameters.Length, parameters.Width, parameters.Height);
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

    private static (bool Undone, string UndoError) Rollback(Session session, Session.UndoMarkId markId)
    {
        try
        {
            session.UndoToMark(markId, "NXAI 创建长方体");
            return (true, "");
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }

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
    private static void LogFailure(Exception ex, string workPartIdentity)
    {
        try
        {
            string logPath = Path.Combine(Path.GetTempPath(), OperationLogFileName);
            string record =
                $"{new string('=', 60)}{Environment.NewLine}" +
                $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] CreateBlockInWorkPart failed " +
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
