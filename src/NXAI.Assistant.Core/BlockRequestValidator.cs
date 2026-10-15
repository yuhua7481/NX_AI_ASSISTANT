namespace NXAI.Assistant.Core;

/// <summary>
/// NXOpen-free input validation for block-creation requests, so the checks can
/// be unit-tested outside the NX process (the NX adapter calls this before
/// touching the session). Returns null when the request is valid, otherwise a
/// user-facing error message.
///
/// The magnitude bounds below are sanity guards against mistyped input
/// (e.g. meters pasted as millimeters), not documented NX limits: values that
/// pass validation may still be rejected by NX itself, and a failure inside
/// NX is reported by the adapter, not assumed away here.
/// </summary>
public static class BlockRequestValidator
{
    /// <summary>Sanity bound for origin coordinates, in millimeters.</summary>
    public const double MaxCoordinateMm = 1_000_000_000;

    /// <summary>Sanity bound for block dimensions, in millimeters.</summary>
    public const double MaxDimensionMm = 1_000_000;

    public static string? Validate(double originX, double originY, double originZ,
        double length, double width, double height, string? targetPath)
    {
        if (!double.IsFinite(originX) || !double.IsFinite(originY) || !double.IsFinite(originZ))
            return "Origin coordinates must be finite numbers.";
        if (Math.Abs(originX) > MaxCoordinateMm || Math.Abs(originY) > MaxCoordinateMm ||
            Math.Abs(originZ) > MaxCoordinateMm)
            return $"Origin coordinates exceed the sanity bound of ±{MaxCoordinateMm} mm.";

        if (!double.IsFinite(length) || !double.IsFinite(width) || !double.IsFinite(height))
            return "Dimensions must be finite numbers.";
        if (length <= 0 || width <= 0 || height <= 0)
            return "Dimensions must be positive numbers.";
        if (length > MaxDimensionMm || width > MaxDimensionMm || height > MaxDimensionMm)
            return $"Dimensions exceed the sanity bound of {MaxDimensionMm} mm.";

        if (string.IsNullOrWhiteSpace(targetPath))
            return "Target path must be specified.";
        try
        {
            string? dir = Path.GetDirectoryName(targetPath);
            if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir))
                return "Target directory does not exist.";
            if (File.Exists(targetPath))
                return "Target file already exists.";
        }
        catch (Exception ex)
        {
            return $"Invalid target path: {ex.Message}";
        }
        return null;
    }
}
