using NXAI.Assistant.Core;
using Xunit;

namespace NXAI.Assistant.Tests;

public class WorkPartSnapshotTests
{
    [Fact]
    public void None_reports_no_active_part()
    {
        var snapshot = WorkPartSnapshot.None();

        Assert.False(snapshot.HasActivePart);
        Assert.Null(snapshot.DisplayName);
        Assert.Null(snapshot.FullPath);
        Assert.Null(snapshot.Units);
        Assert.NotNull(snapshot.StatusMessage);
    }

    [Fact]
    public void FromError_reports_no_active_part_and_carrys_reason()
    {
        var snapshot = WorkPartSnapshot.FromError("boom");

        Assert.False(snapshot.HasActivePart);
        Assert.Contains("boom", snapshot.StatusMessage);
    }

    [Fact]
    public void Snapshot_with_part_carries_all_fields()
    {
        var snapshot = new WorkPartSnapshot(
            HasActivePart: true,
            DisplayName: "model1",
            FullPath: @"D:\work\model1.prt",
            Units: "Millimeters",
            StatusMessage: null);

        Assert.True(snapshot.HasActivePart);
        Assert.Equal("model1", snapshot.DisplayName);
        Assert.Equal(@"D:\work\model1.prt", snapshot.FullPath);
        Assert.Equal("Millimeters", snapshot.Units);
    }
}
