using System.Globalization;
using System.Text;
using NXAI.Assistant.Core;
using NXOpen;
using NXOpen.Features;

namespace NXAI.Assistant.NxHost.NX2512;

/// <summary>
/// Creates a block feature in the active work part and saves it to the target
/// path. Runs inside the NX process on the NX execution thread.
/// Failure handling: the block is rolled back via UndoToMark, the builder is
/// destroyed (required by NXOpen to free native resources), the full exception
/// is appended to %TEMP%\NXAI_Assistant_operation_error.log, and the original
/// exception message is returned to the caller — never swallowed.
/// </summary>
public static class BlockFeatureCreator
{
    private const string OperationLogFileName = "NXAI_Assistant_operation_error.log";

    /// <summary>
    /// Creates a block at the specified origin with the given dimensions and
    /// saves the part to <paramref name="targetPath"/>.
    /// </summary>
    /// <param name="originX">X coordinate of origin (mm).</param>
    /// <param name="originY">Y coordinate of origin (mm).</param>
    /// <param name="originZ">Z coordinate of origin (mm).</param>
    /// <param name="length">X dimension (mm).</param>
    /// <param name="width">Y dimension (mm).</param>
    /// <param name="height">Z dimension (mm).</param>
    /// <param name="targetPath">Full path to save the part file.</param>
    /// <returns>Result indicating success or a user-facing error message.</returns>
    public static NXOperationResult CreateBlock(double originX, double originY, double originZ,
        double length, double width, double height,
        string targetPath)
    {
        // Validate everything that does not need NX before touching the session.
        string? validationError = BlockRequestValidator.Validate(
            originX, originY, originZ, length, width, height, targetPath);
        if (validationError != null)
            return new NXOperationResult(false, validationError);

        Session session = Session.GetSession();
        Part workPart = session.Parts.Work;
        if (workPart == null)
            return new NXOperationResult(false, "No active work part.");

        Session.UndoMarkId markId = session.SetUndoMark(
            Session.MarkVisibility.Visible, "Create Block Feature");

        BlockFeatureBuilder? blockBuilder = null;
        try
        {
            blockBuilder = workPart.Features.CreateBlockFeatureBuilder(default(Feature)!);
            blockBuilder.Type = BlockFeatureBuilder.Types.OriginAndEdgeLengths;
            // NXOpen parses these strings as expressions: use invariant culture
            // ("12.5", never "12,5") and round-trippable "R" formatting.
            blockBuilder.SetOriginAndLengths(
                new Point3d(originX, originY, originZ),
                FormatLength(length), FormatLength(width), FormatLength(height));

            blockBuilder.CommitFeature();
            session.UpdateManager.DoUpdate(markId);
            workPart.SaveAs(targetPath);

            return new NXOperationResult(true, "Operation succeeded.");
        }
        catch (Exception ex)
        {
            // Roll back so a failure (e.g. SaveAs) does not leave an
            // unrequested block feature in the user's part.
            try
            {
                session.UndoToMark(markId, "Create Block Feature");
            }
            catch
            {
                // Rollback is best-effort; the original failure is what we report.
            }
            LogFailure(ex);
            return new NXOperationResult(false, $"{ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            // NXOpen requires Destroy to release native builder resources.
            blockBuilder?.Destroy();
        }
    }

    private static string FormatLength(double value) =>
        value.ToString("R", CultureInfo.InvariantCulture);

    /// <summary>
    /// Appends the full failure context to %TEMP%\NXAI_Assistant_operation_error.log.
    /// Never throws, and never logs secrets (this tool handles no credentials).
    /// </summary>
    private static void LogFailure(Exception ex)
    {
        try
        {
            string logPath = Path.Combine(Path.GetTempPath(), OperationLogFileName);
            string record =
                $"{new string('=', 60)}{Environment.NewLine}" +
                $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] CreateBlock failed{Environment.NewLine}" +
                $"{ex}{Environment.NewLine}{Environment.NewLine}";
            File.AppendAllText(logPath, record, Encoding.UTF8);
        }
        catch
        {
            // Diagnostics must never mask the failure being reported.
        }
    }

    public readonly struct NXOperationResult
    {
        public NXOperationResult(bool success, string message)
        {
            Success = success;
            Message = message;
        }
        public bool Success { get; }
        public string Message { get; }
    }
}
