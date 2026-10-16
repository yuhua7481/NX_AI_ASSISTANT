using NXAI.Assistant.Core;
using Xunit;

namespace NXAI.Assistant.Tests;

/// <summary>
/// Offline tests for the phase logger: lines are appended under an injected
/// path, and logging never throws even when the target path is unusable.
/// </summary>
public class PhaseLogTests
{
    [Fact]
    public void Write_AppendsTimestampedLine()
    {
        var path = Path.Combine(Path.GetTempPath(), $"nxai_phase_{Guid.NewGuid():N}.log");
        PhaseLog.LogFilePathOverride = path;
        try
        {
            PhaseLog.Write("test.phase", "hello world");

            Assert.True(File.Exists(path));
            var line = File.ReadAllLines(path).Single();
            Assert.Contains("test.phase", line);
            Assert.Contains("hello world", line);
            Assert.Matches(@"^\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2}\.\d{3} \[T\d+\]", line);
        }
        finally
        {
            PhaseLog.LogFilePathOverride = null;
            File.Delete(path);
        }
    }

    [Fact]
    public void Write_NeverThrows_WhenTargetPathIsUnusable()
    {
        PhaseLog.LogFilePathOverride = Path.Combine(
            Path.GetTempPath(), "nxai_no_such_dir_phase_test", "x.log");
        try
        {
            // Must complete without any exception (logging is best-effort).
            PhaseLog.Write("test.phase", "must not throw");
        }
        finally
        {
            PhaseLog.LogFilePathOverride = null;
        }
    }
}
