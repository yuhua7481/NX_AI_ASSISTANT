using System.Threading;
using System.Windows.Forms;
using NXAI.Assistant.Core;
using NXAI.Assistant.UI;
using Xunit;

namespace NXAI.Assistant.Tests;

/// <summary>
/// Form keeps its session alive across ✕-close / re-open:
/// user close hides the form (never disposes the controller — requests in
/// flight continue and the ChatLog survives), re-showing the SAME instance
/// restores the full history, and only the real teardown path detaches.
/// WinForms handles need STA, so every case runs on a dedicated STA thread.
/// No NX, no network, no model mutation.
/// </summary>
public class ChatFormSessionSurvivalTests
{
    private static (AssistantChatForm, ChatLog, CountingToolExecutor) Create()
    {
        var executor = new CountingToolExecutor();
        // No part reader needed for these paths: the form tolerates a reader
        // that reports nothing (RefreshActivePart handles empty snapshots).
        var form = new AssistantChatForm(new NullPartReader(), executor);
        return (form, form.SessionChatLog, executor);
    }

    private static void RunOnStaThread(Action body)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                body();
            }
            catch (Exception ex)
            {
                failure = ex;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join(30_000);
        Assert.False(thread.IsAlive, "STA 测试线程超时未完成");
        if (failure != null)
            throw failure;
    }

    [Fact]
    public void UserClose_HidesWithoutDisposing_SessionStillReadable()
    {
        RunOnStaThread(() =>
        {
            var (form, log, _) = Create();
            form.Show();
            log.Add(ChatRole.User, "会话保留测试消息");

            form.Close(); // Simulate ✕ — CloseReason.UserClosing

            Assert.False(form.IsDisposed);
            Assert.False(form.Visible);
            Assert.NotEmpty(log.Messages);
            // Controller must still accept requests after re-open.
            Assert.True(form.SessionControllerAlive);
            form.Dispose();
        });
    }

    [Fact]
    public void ReopenAfterClose_SameInstanceWithHistory()
    {
        RunOnStaThread(() =>
        {
            var (form, log, _) = Create();
            form.Show();
            log.Add(ChatRole.User, "历史消息");
            form.Close(); // hidden, alive

            form.Show(); // the re-open path (NxHostBoot.ShowChatWindow)

            Assert.True(form.Visible);
            Assert.False(form.IsDisposed);
            Assert.Contains(log.Messages, m => m.Text == "历史消息");
            form.Dispose();
        });
    }

    [Fact]
    public void RealDispose_DetachesControllerAndUnsubscribesLog()
    {
        RunOnStaThread(() =>
        {
            // No real Show: Dispose without a handle still runs the managed
            // teardown path, which is exactly what is under test.
            var (form, log, _) = Create();
            form.Dispose(); // real teardown (NX exit path)

            // Controller detached — no new request can be accepted, and the
            // form's own log handler is unsubscribed (Dispose is idempotent;
            // an unsubscription that did not exist would throw below).
            Assert.True(form.IsDisposed);
            Assert.False(form.SessionControllerAlive);

            // 成功取消订阅的直接证据：再次 Dispose 不会因重复取消订阅而抛出。
            form.Dispose();
        });
    }

    // ------------------------------------------------------ lifecycle states

    [Fact]
    public void LifecycleState_TracksHideAndRestore()
    {
        RunOnStaThread(() =>
        {
            var (form, log, _) = Create();
            Assert.Equal(AssistantChatForm.FormLifecycleState.Active, form.LifecycleState);
            form.Show();
            form.Close(); // ✕

            Assert.Equal(AssistantChatForm.FormLifecycleState.UserHidden, form.LifecycleState);
            Assert.True(form.CancelledByHiddenClose);
            Assert.True(form.SessionControllerAlive); // ✕ did NOT detach

            form.Show(); // re-open path
            Assert.Equal(AssistantChatForm.FormLifecycleState.Active, form.LifecycleState);
            Assert.False(form.CancelledByHiddenClose);
            form.Dispose();
        });
    }

    [Fact]
    public void SessionExitClose_DetachesController_AsSessionExiting()
    {
        RunOnStaThread(() =>
        {
            var (form, log, _) = Create();
            form.Show();

            form.CloseForSessionShutdown(); // the host teardown entry point

            Assert.Equal(AssistantChatForm.FormLifecycleState.SessionExiting, form.LifecycleState);
            Assert.False(form.SessionControllerAlive); // controller detached
            log.Add(ChatRole.Info, "卸载后的消息不应到达窗体"); // no throw: log handler removed
        });
    }

    [Fact]
    public void CloseForSessionShutdown_FromNonUiThread_SkipsWinFormsCalls()
    {
        // AppDomain.ProcessExit runs on an arbitrary thread: the method must
        // silently reduce to the thread-safe parts (controller cancel + state
        // marker) — no cross-thread WinForms calls during process teardown.
        AssistantChatForm? form = null;
        Exception? failure = null;
        RunOnStaThread(() =>
        {
            var (f, _, _) = Create();
            form = f;
            form.Show();
        });
        // Form handle lives on the STA thread; this test-thread call site is
        // a DIFFERENT thread → InvokeRequired must be true and the method
        // must return without touching WinForms.
        var other = new Thread(() =>
        {
            try
            {
                Assert.True(form!.InvokeRequired);
                form.CloseForSessionShutdown(); // must not throw
            }
            catch (Exception ex) { failure = ex; }
        });
        other.SetApartmentState(ApartmentState.MTA);
        other.Start();
        other.Join(30_000);
        Assert.False(other.IsAlive, "worker 线程超时未完成");
        Assert.Null(failure);

        RunOnStaThread(() =>
        {
            // The form was never disposed by the worker call (Close skipped).
            // Dispose through the STA teardown now; the full same-thread
            // CloseForSessionShutdown chain is covered by
            // SessionExitClose_DetachesController_AsSessionExiting.
            Assert.False(form!.IsDisposed);
            form.Dispose();
            Assert.True(form.IsDisposed);
        });
    }

    [Fact]
    public void HiddenClose_ThenRealDispose_ControllerDetachedExactlyOnce()
    {
        // The full chain: ✕ hides (UserHidden), the host tears down
        // (CloseForSessionShutdown sets SessionExiting and closes), Dispose
        // runs exactly once — a second Dispose must not double-detach or
        // throw (controller Dispose is idempotent through Detach).
        RunOnStaThread(() =>
        {
            var (form, log, _) = Create();
            form.Show();
            form.Close();                      // UserHidden, controller alive
            Assert.True(form.SessionControllerAlive);

            form.CloseForSessionShutdown();    // SessionExiting → real close
            Assert.False(form.SessionControllerAlive);
            Assert.True(form.IsDisposed);

            form.CloseForSessionShutdown();    // idempotent: IsDisposed guard
            form.Dispose();                    // idempotent
            Assert.Equal(AssistantChatForm.FormLifecycleState.SessionExiting, form.LifecycleState);
            _ = log;
        });
    }
}

// ------------------------------------------------------------- test doubles

internal sealed class NullPartReader : IActivePartReader
{
    public WorkPartSnapshot ReadActivePart() => WorkPartSnapshot.None();
}

internal sealed class CountingToolExecutor : INxToolExecutor
{
    private readonly Func<Task<CreateBlockExecutionResult>>? _behavior;
    public int CallCount { get; private set; }

    public CountingToolExecutor(Func<Task<CreateBlockExecutionResult>>? behavior = null)
    {
        _behavior = behavior;
    }

    private CreateBlockExecutionResult Result()
    {
        var a = _behavior;
        if (a != null)
            return a().GetAwaiter().GetResult();
        return CreateBlockExecutionResult.Failure(
            ToolExecutionStatus.ExecutionFailed, "测试桩未配置返回结果", rolledBack: false);
    }

    public CreateBlockExecutionResult ExecuteCreateBlock(
        CreateBlockParameters parameters, string? expectedWorkPartFullPath)
    {
        CallCount++;
        return Result();
    }

    public CreateBlockExecutionResult ExecuteCreateCylinder(
        CreateCylinderParameters parameters, string? expectedWorkPartFullPath)
    {
        CallCount++;
        return Result();
    }

    public CreateBlockExecutionResult ExecuteCreateCone(
        CreateConeParameters parameters, string? expectedWorkPartFullPath)
    {
        CallCount++;
        return Result();
    }

    public CreateBlockExecutionResult ExecuteCreateSphere(
        CreateSphereParameters parameters, string? expectedWorkPartFullPath)
    {
        CallCount++;
        return Result();
    }

    public CreateBlockExecutionResult ExecuteCreateSketch(
        CreateSketchParameters parameters, string? expectedWorkPartFullPath)
    {
        CallCount++;
        return Result();
    }

    public CreateBlockExecutionResult ExecuteCreateExtrude(
        CreateExtrudeParameters parameters, string? expectedWorkPartFullPath)
    {
        CallCount++;
        return Result();
    }

    public CreateBlockExecutionResult ExecuteCreateRevolve(
        CreateRevolveParameters parameters, string? expectedWorkPartFullPath)
    {
        CallCount++;
        return Result();
    }

    public CreateBlockExecutionResult ExecuteBooleanOperation(
        BooleanOperationParameters parameters, string? expectedWorkPartFullPath)
    {
        CallCount++;
        return Result();
    }

    public ModelFeatureProbeResult? ProbeCommittedFeature(string toolName, string operationSummary) => null;

    public ModelFeatureProbeResult? ProbeCommittedFeature(
        string toolName, string operationSummary, IReadOnlySet<string>? preExistingFeatureIds,
        BooleanOperationType? operationType = null) =>
        ProbeCommittedFeature(toolName, operationSummary);

    public IReadOnlySet<string>? ProbeFeatureSnapshot(string toolName, string? featureKind = null) => null;
}
