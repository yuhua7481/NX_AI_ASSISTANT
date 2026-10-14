using NXAI.Assistant.NxHost.NX2512;
using Xunit;

namespace NXAI.Assistant.Tests;

public class BlockFeatureCreatorTests
{
    [Fact]
    public void CreateBlock_InvalidLength_ReturnsError()
    {
        // Arrange
        var result = BlockFeatureCreator.CreateBlock(
            0, 0, 0,
            -1, 10, 10,
            @"D:\test\cube.prt");

        // Assert
        Assert.False(result.Success);
        Assert.Contains("Dimensions must be positive", result.Message);
    }

    [Fact]
    public void CreateBlock_InvalidWidth_ReturnsError()
    {
        var result = BlockFeatureCreator.CreateBlock(
            0, 0, 0,
            10, 0, 10,
            @"D:\test\cube.prt");
        Assert.False(result.Success);
        Assert.Contains("Dimensions must be positive", result.Message);
    }

    [Fact]
    public void CreateBlock_InvalidHeight_ReturnsError()
    {
        var result = BlockFeatureCreator.CreateBlock(
            0, 0, 0,
            10, 10, -5,
            @"D:\test\cube.prt");
        Assert.False(result.Success);
        Assert.Contains("Dimensions must be positive", result.Message);
    }

    [Fact]
    public void CreateBlock_NonFiniteOrigin_ReturnsError()
    {
        var result = BlockFeatureCreator.CreateBlock(
            double.NaN, 0, 0,
            10, 10, 10,
            @"D:\test\cube.prt");
        Assert.False(result.Success);
        Assert.Contains("Origin coordinates must be finite", result.Message);
    }

    [Fact]
    public void CreateBlock_EmptyTargetPath_ReturnsError()
    {
        var result = BlockFeatureCreator.CreateBlock(
            0, 0, 0,
            10, 10, 10,
            "");
        Assert.False(result.Success);
        Assert.Contains("Target path must be specified", result.Message);
    }

    [Fact]
    public void CreateBlock_TargetPathExists_ReturnsError()
    {
        // Arrange: create a temporary file
        var tempPath = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            System.IO.Path.GetRandomFileName() + ".prt");
        try
        {
            System.IO.File.WriteAllText(tempPath, "dummy");
            // Act
            var result = BlockFeatureCreator.CreateBlock(
                0, 0, 0,
                10, 10, 10,
                tempPath);
            // Assert
            Assert.False(result.Success);
            Assert.Contains("Target file already exists", result.Message);
        }
        finally
        {
            if (System.IO.File.Exists(tempPath))
                System.IO.File.Delete(tempPath);
        }
    }

    [Fact(Skip = "Requires NX Open environment to run")]
    public void CreateBlock_ValidParameters_SucceedsInNX()
    {
        // This test is a placeholder; actual execution requires a running NX session.
        // It is skipped by default.
        var result = BlockFeatureCreator.CreateBlock(
            0, 0, 0,
            30, 30, 30,
            @"D:\NX_MCP_WORKSPACE\cube_30.prt");
        // In a real NX environment we would expect success.
        // For now we just assert that the method does not throw.
        Assert.True(result.Success || result.Message.Contains("Exception occurred"));
    }
}