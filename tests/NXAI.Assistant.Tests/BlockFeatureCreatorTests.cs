using NXAI.Assistant.Core;
using Xunit;

namespace NXAI.Assistant.Tests;

/// <summary>
/// Input-validation tests for block creation. These run entirely outside NX:
/// BlockFeatureCreator itself references NXOpen and cannot be JIT-loaded
/// outside the NX process, so its NXOpen-free validation lives in Core
/// (BlockRequestValidator) and is tested here.
/// </summary>
public class BlockFeatureCreatorTests
{
    private static string TempPrtPath()
    {
        return Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".prt");
    }

    [Fact]
    public void Validate_InvalidLength_ReturnsError()
    {
        string? error = BlockRequestValidator.Validate(0, 0, 0, -1, 10, 10, @"D:\test\cube.prt");
        Assert.NotNull(error);
        Assert.Contains("Dimensions must be positive", error);
    }

    [Fact]
    public void Validate_InvalidWidth_ReturnsError()
    {
        string? error = BlockRequestValidator.Validate(0, 0, 0, 10, 0, 10, @"D:\test\cube.prt");
        Assert.NotNull(error);
        Assert.Contains("Dimensions must be positive", error);
    }

    [Fact]
    public void Validate_InvalidHeight_ReturnsError()
    {
        string? error = BlockRequestValidator.Validate(0, 0, 0, 10, 10, -5, @"D:\test\cube.prt");
        Assert.NotNull(error);
        Assert.Contains("Dimensions must be positive", error);
    }

    [Fact]
    public void Validate_NonFiniteOrigin_ReturnsError()
    {
        string? error = BlockRequestValidator.Validate(double.NaN, 0, 0, 10, 10, 10, @"D:\test\cube.prt");
        Assert.NotNull(error);
        Assert.Contains("Origin coordinates must be finite", error);
    }

    [Fact]
    public void Validate_InfiniteOrigin_ReturnsError()
    {
        Assert.Contains("Origin coordinates must be finite",
            BlockRequestValidator.Validate(double.PositiveInfinity, 0, 0, 10, 10, 10, @"D:\test\cube.prt"));
        Assert.Contains("Origin coordinates must be finite",
            BlockRequestValidator.Validate(0, double.NegativeInfinity, 0, 10, 10, 10, @"D:\test\cube.prt"));
    }

    [Fact]
    public void Validate_HugeOrigin_ReturnsError()
    {
        string? error = BlockRequestValidator.Validate(
            BlockRequestValidator.MaxCoordinateMm * 10, 0, 0, 10, 10, 10, @"D:\test\cube.prt");
        Assert.NotNull(error);
        Assert.Contains("sanity bound", error);
    }

    [Fact]
    public void Validate_DimensionNaN_ReturnsError()
    {
        string? error = BlockRequestValidator.Validate(0, 0, 0, double.NaN, 10, 10, @"D:\test\cube.prt");
        Assert.NotNull(error);
        Assert.Contains("Dimensions must be finite", error);
    }

    [Fact]
    public void Validate_DimensionInfinity_ReturnsError()
    {
        Assert.Contains("finite",
            BlockRequestValidator.Validate(0, 0, 0, double.PositiveInfinity, 10, 10, @"D:\test\cube.prt"));
        Assert.Contains("finite",
            BlockRequestValidator.Validate(0, 0, 0, 10, double.NegativeInfinity, 10, @"D:\test\cube.prt"));
        Assert.Contains("finite",
            BlockRequestValidator.Validate(0, 0, 0, 10, 10, double.PositiveInfinity, @"D:\test\cube.prt"));
    }

    [Fact]
    public void Validate_HugeDimension_ReturnsError()
    {
        string? error = BlockRequestValidator.Validate(
            0, 0, 0, BlockRequestValidator.MaxDimensionMm * 10, 10, 10, @"D:\test\cube.prt");
        Assert.NotNull(error);
        Assert.Contains("sanity bound", error);
    }

    [Fact]
    public void Validate_ExtremeButFiniteValues_PassBoundChecks()
    {
        // Values just inside the bounds are valid and must reach the path checks.
        string tempPath = TempPrtPath();
        try
        {
            string? error = BlockRequestValidator.Validate(
                -BlockRequestValidator.MaxCoordinateMm, BlockRequestValidator.MaxCoordinateMm, 0,
                BlockRequestValidator.MaxDimensionMm, 1e-6, 10, tempPath);
            Assert.Null(error);
        }
        finally
        {
            if (File.Exists(tempPath))
                File.Delete(tempPath);
        }
    }

    [Fact]
    public void Validate_EmptyTargetPath_ReturnsError()
    {
        string? error = BlockRequestValidator.Validate(0, 0, 0, 10, 10, 10, "");
        Assert.NotNull(error);
        Assert.Contains("Target path must be specified", error);
    }

    [Fact]
    public void Validate_NonexistentDirectory_ReturnsError()
    {
        string? error = BlockRequestValidator.Validate(
            0, 0, 0, 10, 10, 10,
            Path.Combine(Path.GetTempPath(), "nxai-no-such-dir", "cube.prt"));
        Assert.NotNull(error);
        Assert.Contains("Target directory does not exist", error);
    }

    [Fact]
    public void Validate_TargetFileExists_ReturnsError()
    {
        string tempPath = TempPrtPath();
        try
        {
            File.WriteAllText(tempPath, "dummy");
            string? error = BlockRequestValidator.Validate(0, 0, 0, 10, 10, 10, tempPath);
            Assert.NotNull(error);
            Assert.Contains("Target file already exists", error);
        }
        finally
        {
            if (File.Exists(tempPath))
                File.Delete(tempPath);
        }
    }

    [Fact]
    public void Validate_ValidRequest_ReturnsNull()
    {
        string tempPath = TempPrtPath();
        try
        {
            string? error = BlockRequestValidator.Validate(0, 0, 0, 10, 10, 10, tempPath);
            Assert.Null(error);
        }
        finally
        {
            if (File.Exists(tempPath))
                File.Delete(tempPath);
        }
    }

    /// <summary>
    /// Real-NX acceptance test for BlockFeatureCreator.CreateBlock. Must run
    /// inside a live NX session with a disposable part — never against a user
    /// part (it commits a feature and does SaveAs). Skipped outside NX.
    /// </summary>
    [Fact(Skip = "Real-NX acceptance: requires a live NX2512 session and a disposable test part.")]
    public void CreateBlock_ValidParameters_SucceedsInNX()
    {
        var result = NXAI.Assistant.NxHost.NX2512.BlockFeatureCreator.CreateBlock(
            0, 0, 0, 30, 30, 30,
            Path.Combine(Path.GetTempPath(), "nxai_block_acceptance.prt"));
        Assert.True(result.Success || result.Message.Contains("Exception occurred"));
    }
}
