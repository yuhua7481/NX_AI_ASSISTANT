namespace NXAI.Assistant;

/// <summary>
/// MenuScript callback entry invoked by NX when the user clicks the
/// "AI Assistant" menu entry. Runs on the NX UI thread.
/// </summary>
public static class Entry
{
    public static void Main()
    {
        try
        {
            NxHost.NX2512.NxHostBoot.ShowChatWindow();
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                $"NX AI Assistant 启动失败：{ex}",
                "NX AI Assistant",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
        }
    }
}
