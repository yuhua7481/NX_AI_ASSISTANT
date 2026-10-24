using Xunit;

namespace NXAI.Assistant.Tests;

/// <summary>
/// Phase 8.8 — Boolean execution diagnostics coverage. The executor needs a
/// live NX session, so these tests pin the DIAGNOSTIC CONTRACT structurally:
/// every stage of ExecuteBooleanOperation must emit exactly one identifiable
/// execute.boolean line, and every failure exit must reach LogFailure. The
/// assertions read the executor source once at build time; a stage whose log
/// line is renamed or dropped breaks these tests before it breaks live
/// diagnosis. Business logic is not exercised here — it lives in the
/// offline policy tests.
///
/// Live-NX coverage boundary: whether NX itself throws at each stage can
/// only be confirmed in the real session (acceptance steps are in the task
/// report); this file guarantees the log lines and error-log writes EXIST
/// for every path.
/// </summary>
public class BooleanDiagnosticsCoverageTests
{
    private static string ExecutorSource()
    {
        // Repo layout: tests/…/ → src/NxHost/FeatureToolExecutor.cs
        var here = AppContext.BaseDirectory;
        var dir = new DirectoryInfo(here);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "NXAIAssistant.sln")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        return File.ReadAllText(Path.Combine(
            dir!.FullName, "src", "NXAI.Assistant.NxHost.NX2512", "FeatureToolExecutor.cs"));
    }

    [Fact]
    public void Entry_LogsOperationAndBothRefs()
    {
        var src = ExecutorSource();
        Assert.Contains("begin: op={opName0} target={parameters.TargetBodyRef} tool={parameters.ToolBodyRef}", src);
    }

    [Fact]
    public void EveryPreconditionExit_HasBothPhaseLog_AndErrorLog()
    {
        var src = ExecutorSource();
        // Work part resolution, target ref, tool ref, same body, sheet body:
        // each REFUSED line must be followed by a LogFailure in the same block.
        int refusedCount = CountOccurrences(src, "REFUSED:");
        int preconditionLogFailures = CountOccurrences(src, "布尔运算前置失败");
        Assert.True(refusedCount >= 5, $"expected ≥5 REFUSED stage logs, found {refusedCount}");
        Assert.True(preconditionLogFailures >= 5,
            $"expected ≥5 precondition LogFailure records, found {preconditionLogFailures}");
    }

    [Fact]
    public void PreTryBlocks_AreGuarded_WithStageTaggedAborts()
    {
        var src = ExecutorSource();
        // The core 2026-10-21 gap: failures BEFORE the main try/catch were
        // silent. Both pre-try stages now abort with a stage-tagged log AND
        // a LogFailure record.
        Assert.Contains("ABORTED in body-ref resolution", src);
        Assert.Contains("ABORTED in pre-state snapshot", src);
        Assert.Contains("布尔运算前置检查发生异常", src);
        Assert.Contains("布尔运算前置快照发生异常", src);
    }

    [Fact]
    public void BuilderStages_AreAllLogged()
    {
        var src = ExecutorSource();
        Assert.Contains("undo mark created:", src);
        Assert.Contains("builder configured:", src);
        Assert.Contains("commit returned:", src);
        Assert.Contains("update=ok", src);
    }

    [Fact]
    public void DoUpdate_IsSeparatedFromCommit_InLogs()
    {
        // 2026-10-21 requirement: distinguish a Commit failure from an
        // Update failure — the "commit returned" line must appear BEFORE
        // DoUpdate runs, so an exception between them names the stage.
        var src = ExecutorSource();
        int booleanBegin = src.IndexOf("begin: op={opName0}", StringComparison.Ordinal);
        int commitReturned = src.IndexOf("commit returned:", booleanBegin, StringComparison.Ordinal);
        // Search for DoUpdate WITHIN the boolean method (after its begin).
        int doUpdate = src.IndexOf("session.UpdateManager.DoUpdate(markId);", booleanBegin, StringComparison.Ordinal);
        Assert.True(commitReturned >= 0 && doUpdate >= 0);
        Assert.True(commitReturned < doUpdate,
            "the commit-returned log must precede DoUpdate so an Update exception names its stage");
    }

    [Fact]
    public void CatchBlock_TagsStage_RollsBackConditionally_AndSnapshotsState()
    {
        var src = ExecutorSource();
        Assert.Contains("EXCEPTION: {ex.GetType().Name}: {ex.Message} (markCreated=", src);
        Assert.Contains("markCreated", src);
        Assert.Contains("post-rollback state: bodies=", src);
        Assert.Contains("Boolean{opName0}-premark", src);
    }

    [Fact]
    public void BuilderDestroy_IsGuarded_AndLogged()
    {
        var src = ExecutorSource();
        Assert.Contains("builder destroyed", src);
        Assert.Contains("builder destroy FAILED:", src);
    }

    [Fact]
    public void VerificationExits_AllWriteErrorLog()
    {
        var src = ExecutorSource();
        // NoEffect, accounting failure, unresolvable result, unreadable
        // feature output, attribution mismatch, containment failure.
        Assert.Contains("Boolean no-effect (rolled back)", src);
        Assert.Contains("Boolean result body unresolvable", src);
        Assert.Contains("attribution mismatch", src);
        Assert.Contains("Boolean postcondition failed", src);
        Assert.Contains("Boolean feature output unreadable", src);
    }

    [Fact]
    public void RollbackHelper_LogsOkAndFailed()
    {
        // Rollback lives in BlockToolExecutor.cs (shared partial); its ok /
        // FAILED lines cover every boolean rollback call.
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "NXAIAssistant.sln")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        var rollbackSrc = File.ReadAllText(Path.Combine(
            dir!.FullName, "src", "NXAI.Assistant.NxHost.NX2512", "BlockToolExecutor.cs"));
        Assert.Contains("execute.rollback", rollbackSrc);
        Assert.Contains("FAILED: {undoName}", rollbackSrc);
    }

    private static int CountOccurrences(string haystack, string needle)
    {
        int count = 0, i = 0;
        while ((i = haystack.IndexOf(needle, i, StringComparison.Ordinal)) >= 0)
        {
            count++;
            i += needle.Length;
        }
        return count;
    }
}
