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

    public static void ShowChatWindow()
    {
        if (_chatForm is not null && !_chatForm.IsDisposed)
        {
            if (_chatForm.WindowState == FormWindowState.Minimized)
            {
                _chatForm.WindowState = FormWindowState.Normal;
            }
            _chatForm.Activate();
            return;
        }

        var contextReader = new NxContextReader();
        _chatForm = new AssistantChatForm(
            new NxSessionReader(), new NxToolExecutor(),
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
            });
        _chatForm.Show();
    }
}
