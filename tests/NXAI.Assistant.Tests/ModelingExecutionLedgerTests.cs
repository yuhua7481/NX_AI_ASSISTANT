using NXAI.Assistant.Core;
using Xunit;

namespace NXAI.Assistant.Tests;

/// <summary>
/// Offline tests for the modeling execution ledger: controller-independent
/// state machine that preserves the durable fact "the executor was entered"
/// across chat-window rebuilds. Focus: state transitions, uniqueness of the
/// "result unknown" state, and no-secret storage.
/// </summary>
public class ModelingExecutionLedgerTests
{
    private static CreateBlockExecutionResult Ok() =>
        CreateBlockExecutionResult.Success(100, 60, 20, "成功", "part.prt");

    private static CreateBlockExecutionResult Failed(ToolExecutionStatus status) =>
        CreateBlockExecutionResult.Failure(status, "失败", rolledBack: true);

    [Fact]
    public void Begin_marked_attempting_before_result_and_has_unresolved_true()
    {
        var ledger = new ModelingExecutionLedger();
        var id = ledger.BeginAttempt("create_block", "长方体 100×60×20mm");

        var snap = ledger.Snapshot();
        var entry = Assert.Single(snap);
        Assert.Equal(ModelingExecutionState.Attempting, entry.State);
        Assert.Equal("create_block", entry.ToolName);
        Assert.Equal("长方体 100×60×20mm", entry.OperationSummary);
        Assert.True(ledger.HasUnresolved);
        Assert.True(ledger.HasUnacknowledgedAttempt("create_block"));
        Assert.False(ledger.HasUnacknowledgedAttempt("create_cylinder"));
        _ = id;
    }

    [Fact]
    public void Recorded_success_resolves_unresolved_and_unlocks_gate_query()
    {
        var ledger = new ModelingExecutionLedger();
        var id = ledger.BeginAttempt("create_block", "长方体");
        ledger.RecordResult(id, Ok());

        var entry = Assert.Single(ledger.Snapshot());
        Assert.Equal(ModelingExecutionState.Recorded, entry.State);
        Assert.Equal(ToolExecutionStatus.Success, entry.Status);
        Assert.Equal("成功", entry.ResultMessage);
        Assert.False(entry.RolledBack);
        Assert.False(ledger.HasUnresolved);
        Assert.False(ledger.HasUnacknowledgedAttempt("create_block"));
    }

    [Fact]
    public void Structural_failure_is_also_a_terminating_fact()
    {
        var ledger = new ModelingExecutionLedger();
        var id = ledger.BeginAttempt("create_cone", "圆台");
        ledger.RecordResult(id, Failed(ToolExecutionStatus.InvalidParameters));

        var entry = Assert.Single(ledger.Snapshot());
        Assert.Equal(ToolExecutionStatus.InvalidParameters, entry.Status);
        Assert.True(entry.RolledBack);
        Assert.False(ledger.HasUnresolved); // failure is resolved knowledge, not unknown
    }

    [Fact]
    public void Unknown_result_is_never_auto_resolved_or_downgraded()
    {
        var ledger = new ModelingExecutionLedger();
        var id = ledger.BeginAttempt("create_block", "长方体");
        // No RecordResult (simulate process teardown mid-executor).

        var entry = Assert.Single(ledger.Snapshot());
        Assert.Equal(ModelingExecutionState.Attempting, entry.State);
        Assert.Null(entry.Status);           // no status is asserted anywhere
        Assert.Null(entry.ResultMessage);
        Assert.True(ledger.HasUnresolved);
        // And TakeUnseen does NOT turn it into a recorded state.
        var surfaced = ledger.TakeUnseen();
        var e2 = Assert.Single(ledger.Snapshot());
        Assert.Equal(ModelingExecutionState.Attempting, e2.State);
        Assert.Equal(entry.LedgerId, e2.LedgerId);
        _ = surfaced;
    }

    [Fact]
    public void Record_result_is_idempotent_guarded_and_rejects_unknown_id()
    {
        var ledger = new ModelingExecutionLedger();
        var id = ledger.BeginAttempt("create_block", "长方体");

        Assert.True(ledger.RecordResult(id, Ok()));
        // Second result must not overwrite the terminating fact.
        Assert.False(ledger.RecordResult(id, Failed(ToolExecutionStatus.ExecutionFailed)));
        Assert.Single(ledger.Snapshot());
        Assert.Equal(ToolExecutionStatus.Success, Assert.Single(ledger.Snapshot()).Status);

        Assert.False(ledger.RecordResult("ledger_999", Ok()));
        Assert.False(ledger.RecordResult("no_such_id", Ok()));
    }

    [Fact]
    public void ExecutionUncertain_keeps_entry_attempting_and_gate_active()
    {
        // ExecutionUncertain is NOT a terminating fact: the executor ran but
        // cannot vouch for committed geometry (partial modification risk).
        // It must never become Recorded, since Recorded would let an
        // autonomous re-run bypass the unknown gate.
        var ledger = new ModelingExecutionLedger();
        var id = ledger.BeginAttempt("create_block", "不确定长方体");
        var uncertain = CreateBlockExecutionResult.Failure(
            ToolExecutionStatus.ExecutionUncertain,
            "执行器无法确认部件是否被修改", rolledBack: false);

        Assert.True(ledger.RecordResult(id, uncertain));
        var entry = Assert.Single(ledger.Snapshot());
        Assert.Equal(ModelingExecutionState.Attempting, entry.State);
        Assert.Null(entry.Status);                 // NOT claimed as terminated
        Assert.Equal("执行器无法确认部件是否被修改", entry.ResultMessage); // kept for display
        Assert.True(ledger.HasUnresolved);
        Assert.True(ledger.HasUnacknowledgedAttempt("create_block"));
        // Acknowledge (explicit user verification) is the only exit.
        Assert.True(ledger.Acknowledge(id));
        Assert.False(ledger.HasUnacknowledgedAttempt("create_block"));
        Assert.False(ledger.HasUnresolved);
    }

    [Fact]
    public void ExecutionUncertain_surfaces_via_take_unseen_and_stays_after()
    {
        var ledger = new ModelingExecutionLedger();
        var id = ledger.BeginAttempt("create_cone", "不确定圆锥");
        ledger.RecordResult(id, CreateBlockExecutionResult.Failure(
            ToolExecutionStatus.ExecutionUncertain, "无法回滚并无法确认结果"));

        var surfaced = ledger.TakeUnseen();
        var e = Assert.Single(surfaced);
        Assert.Equal(ModelingExecutionState.Attempting, e.State);
        Assert.Null(e.Status);
        Assert.Equal("无法回滚并无法确认结果", e.ResultMessage);
        // A second take returns nothing (seen once) but the state persists.
        Assert.Empty(ledger.TakeUnseen());
        var entry = Assert.Single(ledger.Snapshot());
        Assert.Equal(ModelingExecutionState.Attempting, entry.State);
    }

    [Fact]
    public void Acknowledge_only_applies_to_attempting_entries()
    {
        var ledger = new ModelingExecutionLedger();
        var unknownId = ledger.BeginAttempt("create_block", "长方体");
        var recordedId = ledger.BeginAttempt("create_cone", "圆锥");
        ledger.RecordResult(recordedId, Ok());

        Assert.True(ledger.Acknowledge(unknownId));
        Assert.False(ledger.HasUnacknowledgedAttempt("create_block"));
        // Acknowledge on Recorded is rejected — recorded facts need no review.
        Assert.False(ledger.Acknowledge(recordedId));
        // Double acknowledgement is still a no-op returning true semantics.
        Assert.True(ledger.Acknowledge(unknownId));
        Assert.False(ledger.Acknowledge("no_such_id"));
    }

    [Fact]
    public void Take_unseen_marks_seen_and_returns_once_only()
    {
        var ledger = new ModelingExecutionLedger();
        ledger.BeginAttempt("create_block", "A");
        ledger.BeginAttempt("create_cylinder", "B");

        var first = ledger.TakeUnseen();
        Assert.Equal(2, first.Count);
        Assert.All(first, e => Assert.False(e.Seen, "已take的副本保持take时点的seen=false快照"));
        Assert.Empty(ledger.TakeUnseen()); // nothing left
    }

    [Fact]
    public void Ledger_survives_controller_rebuild_with_full_history()
    {
        // Simulate the NxHostBoot design: one ledger instance, two
        // "windows" (i.e., two controllers' worth of activity) on it.
        var ledger = new ModelingExecutionLedger();
        var oldId = ledger.BeginAttempt("create_block", "旧窗体长方体");
        ledger.RecordResult(oldId, Ok());

        var stillUnknown = ledger.BeginAttempt("create_cylinder", "未知圆筒");
        // (simulate mid-executor teardown: no RecordResult)

        // New window reads what happened.
        var surfaced = ledger.TakeUnseen();
        Assert.Equal(2, surfaced.Count);
        var block = surfaced.Single(e => e.ToolName == "create_block");
        Assert.Equal(ToolExecutionStatus.Success, block.Status);
        var cylinder = surfaced.Single(e => e.ToolName == "create_cylinder");
        Assert.Equal(ModelingExecutionState.Attempting, cylinder.State);
        Assert.True(ledger.HasUnacknowledgedAttempt("create_cylinder"));
        _ = stillUnknown;
    }

    [Fact]
    public void PendingForDisplay_recorded_shown_once_unknown_repeats_until_acknowledged()
    {
        // Display policy across window rebuilds:
        //   Recorded facts: announced on the first window that sees them,
        //   then stay in the audit trail without re-announcing.
        //   Unacknowledged unknowns: repeat on EVERY rebuild until an
        //   explicit user review acknowledges them — and displaying them
        //   never acknowledges (gate is unaffected).
        var ledger = new ModelingExecutionLedger();
        var successId = ledger.BeginAttempt("create_block", "长方体");
        ledger.RecordResult(successId, Ok());
        var unknownId = ledger.BeginAttempt("create_cylinder", "未知圆柱"); // stays Attempting

        // Window #1: both visible.
        var w1 = ledger.PendingForDisplay();
        Assert.Equal(2, w1.Count);

        // Window #2 (rebuild): the Recorded success does NOT repeat…
        var w2 = ledger.PendingForDisplay();
        Assert.Single(w2);
        Assert.Equal("create_cylinder", w2[0].ToolName);   // the unknown does repeat
        // …and repeating it changed nothing: still Attempting, not acknowledged.
        var cylinderEntry = ledger.Snapshot().Single(e => e.LedgerId == unknownId);
        Assert.Equal(ModelingExecutionState.Attempting, cylinderEntry.State);
        Assert.False(cylinderEntry.Acknowledged);
        Assert.True(ledger.HasUnacknowledgedAttempt("create_cylinder"));
        _ = successId;

        // Window #3 (another rebuild): unknown STILL visible until review.
        var w3 = ledger.PendingForDisplay();
        Assert.Single(w3);
        Assert.Equal("create_cylinder", w3[0].ToolName);

        // Explicit user verification: gate releases; the audit entry stays.
        Assert.True(ledger.Acknowledge(unknownId));
        Assert.False(ledger.HasUnacknowledgedAttempt("create_cylinder"));
        Assert.False(ledger.HasUnresolved);
        var after = ledger.Snapshot().Single(e => e.LedgerId == unknownId);
        Assert.Equal(ModelingExecutionState.Attempting, after.State);   // audit keeps the fact
        Assert.True(after.Acknowledged);

        // Window #4: nothing repeats anymore.
        Assert.Empty(ledger.PendingForDisplay());
        // Full audit trail remains queryable regardless of display.
        Assert.Equal(2, ledger.Snapshot().Count);
    }

    [Fact]
    public void PendingForDisplay_acknowledged_unknown_is_never_reannounced()
    {
        var ledger = new ModelingExecutionLedger();
        var id = ledger.BeginAttempt("create_cone", "圆锥");
        ledger.RecordResult(id, CreateBlockExecutionResult.Failure(
            ToolExecutionStatus.ExecutionUncertain, "不确定"));
        Assert.True(ledger.Acknowledge(id));

        // Acknowledged entries are settled facts; no window re-announces.
        Assert.Empty(ledger.PendingForDisplay());
        // Audit trail still carries them.
        Assert.Single(ledger.Snapshot());
    }

    [Fact]
    public void Take_unseen_and_pending_for_display_are_independent_views()
    {
        // A recorded entry consumed by TakeUnseen is not re-announced by
        // PendingForDisplay (seen is shared); an unacknowledged unknown is
        // visible to both views.
        var ledger = new ModelingExecutionLedger();
        var okId = ledger.BeginAttempt("create_block", "A");
        ledger.RecordResult(okId, Ok());
        ledger.BeginAttempt("create_cylinder", "B");

        Assert.Equal(2, ledger.TakeUnseen().Count);
        // Seen now applies to both; only the unknown re-surfaces.
        var view = ledger.PendingForDisplay();
        Assert.Single(view);
        Assert.Equal("create_cylinder", view[0].ToolName);
    }

    [Fact]
    public void Empty_operation_summary_and_blank_tool_are_rejected()
    {
        var ledger = new ModelingExecutionLedger();
        Assert.Throws<ArgumentException>(
            () => ledger.BeginAttempt("", "摘要"));
        Assert.Throws<ArgumentNullException>(
            () => ledger.BeginAttempt("create_block", null!));
    }
}
