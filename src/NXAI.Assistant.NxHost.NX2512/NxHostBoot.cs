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

        _chatForm = new AssistantChatForm(new NxSessionReader(), new NxToolExecutor());
        _chatForm.Show();
    }
}
