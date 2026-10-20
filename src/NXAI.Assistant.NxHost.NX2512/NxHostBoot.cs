using NXAI.Assistant.Core;
using NXAI.Assistant.UI;

namespace NXAI.Assistant.NxHost.NX2512;

/// <summary>
/// Lifetime manager for the modeless chat window inside the NX process.
/// Repeated entries activate the existing window instead of creating duplicates.
/// The form lives on the NX UI thread and is pumped by NX's message loop,
/// so the NX main window stays interactive.
/// </summary>
public static class NxHostBoot
{
    private static AssistantChatForm? _chatForm;
    private static readonly object RegisterGate = new();
    private static bool _teardownHookRegistered;

    // Per-process ledger (approved design): instance-level, shared across
    // chat-window rebuilds for the whole NX host lifetime. Created once here
    // (NX process start) so a torn-down chat window cannot erase what its
    // executor already did. Only holds tool names, plain-language summaries,
    // statuses — never API keys or configuration.
    private static readonly ModelingExecutionLedger SharedExecutionLedger = new();

    public static void ShowChatWindow()
    {
        if (_chatForm is not null && !_chatForm.IsDisposed)
        {
            if (_chatForm.WindowState == FormWindowState.Minimized)
            {
                _chatForm.WindowState = FormWindowState.Normal;
            }
            _chatForm.Show();
            _chatForm.Activate();
            return;
        }

        var contextReader = new NxContextReader();
        var selfCheck = new NxGeometrySelfCheck(new NxToolExecutor());
        _chatForm = new AssistantChatForm(
            new NxSessionReader(), new NxToolExecutor(),
            executionLedger: SharedExecutionLedger,
            contextProvider: () =>
            {
                try
                {
                    return contextReader.Capture();
                }
                catch (Exception ex)
                {
                    // Context capture must never break the chat flow; a null
                    // snapshot simply means no NX context was injected. The
                    // failure reason is logged so missing context is visible.
                    PhaseLog.Write("context.capture",
                        $"FAILED provider: {ex.GetType().Name}: {ex.Message}");
                    return null;
                }
            },
            geometrySelfCheck: () =>
            {
                var report = selfCheck.RunAll();
                PhaseLog.Write("selfcheck",
                    $"{report.PassedCount} passed / {report.FailedCount} failed in {report.PartIdentity}");
                return report;
            });
        _chatForm.Show();
        EnsureTeardownHookRegistered();
        // Session survival: a hidden (✕-closed) window keeps its session.
        // Only the real teardown (see OnNxProcessTeardown) reaches the
        // controller Dispose path. The user's ✕ is a hide — the lifecycle
        // state machinery on the form distinguishes the two.
    }

    /// <summary>
    /// Registers the CLR teardown hook once per process. When the CLR inside
    /// the NX process unloads (NX exits the .NET session cleanly), the chat
    /// form is closed explicitly with a real teardown: the in-flight request
    /// is cancelled and the controller detached. A hard NX crash cannot run
    /// this — the execution ledger stays the durable record for that case.
    /// </summary>
    private static void EnsureTeardownHookRegistered()
    {
        lock (RegisterGate)
        {
            if (_teardownHookRegistered)
                return;
            _teardownHookRegistered = true;
            AppDomain.CurrentDomain.ProcessExit += (_, _) => OnNxProcessTeardown();
        }
    }

    private static void OnNxProcessTeardown()
    {
        try
        {
            _chatForm?.CloseForSessionShutdown();
        }
        catch (Exception ex)
        {
            PhaseLog.Write("chatwindow.teardown", $"FAILED: {ex.GetType().Name}: {ex.Message}");
        }
    }
}
