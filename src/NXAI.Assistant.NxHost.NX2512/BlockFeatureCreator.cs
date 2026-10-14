using NXAI.Assistant.Core;
using NXOpen;
using NXOpen.Features;

namespace NXAI.Assistant.NxHost.NX2512;

public static class BlockFeatureCreator
{
    /// <summary>
    /// Creates a block feature at the specified origin with given dimensions and saves the part.
    /// </summary>
    /// <param name="originX">X coordinate of origin (mm).</param>
    /// <param name="originY">Y coordinate of origin (mm).</param>
    /// <param name="originZ">Z coordinate of origin (mm).</param>
    /// <param name="length">X dimension (mm).</param>
    /// <param name="width">Y dimension (mm).</param>
    /// <param name="height">Z dimension (mm).</param>
    /// <param name="targetPath">Full path to save the part file.</param>
    /// <returns>Result indicating success or error message.</returns>
    public static NXOperationResult CreateBlock(double originX, double originY, double originZ,
        double length, double width, double height,
        string targetPath)
    {
        // Basic validation of numeric values
        if (!double.IsFinite(originX) || !double.IsFinite(originY) || !double.IsFinite(originZ))
            return new NXOperationResult(false, "Origin coordinates must be finite numbers.");
        if (length <= 0 || width <= 0 || height <= 0)
            return new NXOperationResult(false, "Dimensions must be positive numbers.");
        if (string.IsNullOrWhiteSpace(targetPath))
            return new NXOperationResult(false, "Target path must be specified.");
        try
        {
            var dir = System.IO.Path.GetDirectoryName(targetPath);
            if (string.IsNullOrEmpty(dir) || !System.IO.Directory.Exists(dir))
                return new NXOperationResult(false, "Target directory does not exist.");
            if (System.IO.File.Exists(targetPath))
                return new NXOperationResult(false, "Target file already exists.");
        }
        catch (Exception ex)
        {
            return new NXOperationResult(false, $"Invalid target path: {ex.Message}");
        }

        try
        {
            Session session = Session.GetSession();
            Part workPart = session.Parts.Work;
            if (workPart == null)
                return new NXOperationResult(false, "No active work part.");

            // Start a visible undo mark
            Session.UndoMarkId markId = session.SetUndoMark(Session.MarkVisibility.Visible,
                "Create Block Feature");

            // Build the block
            var blockBuilder = workPart.Features.CreateBlockFeatureBuilder(default(Feature)!);
            blockBuilder.Type = BlockFeatureBuilder.Types.OriginAndEdgeLengths;
            blockBuilder.SetOriginAndLengths(new Point3d(originX, originY, originZ),
                System.String.Format("{0}", length),
                System.String.Format("{0}", width),
                System.String.Format("{0}", height));

            // Commit the feature
            Feature feature = blockBuilder.CommitFeature();
            // TODO: Verify if builder cleanup/disposal is required

            // Apply updates
            session.UpdateManager.DoUpdate(markId);

            // Save the part
            workPart.SaveAs(targetPath);

            return new NXOperationResult(true, "Operation succeeded.");
        }
        catch (System.Exception ex)
        {
            return new NXOperationResult(false, $"Exception occurred: {ex.Message}");
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