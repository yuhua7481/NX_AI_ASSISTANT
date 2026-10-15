using System.Text;

namespace NXAI.Assistant;

/// <summary>
/// MenuScript callback entry invoked by NX when the user clicks the
/// "AI Assistant" menu entry. Runs on the NX UI thread.
/// Startup exceptions are first appended to %TEMP%\NXAI_Assistant_startup_error.log
/// (the write itself never throws), then shown in a WinForms MessageBox.
/// WinForms is the permanent UI stack: NX2512's in-proc default context cannot
/// bind WPF's WindowsBase (the base runtime ships a Version=4.0.0.0 stub that
/// shadows the real 8.0.0.0 one inside NX's trusted platform assemblies).
/// </summary>
public static class Entry
{
    private const string LogFileName = "NXAI_Assistant_startup_error.log";

    public static void Main()
    {
        try
        {
            NxHost.NX2512.NxHostBoot.ShowChatWindow();
        }
        catch (Exception ex)
        {
            LogStartupError(ex);
            MessageBox.Show(
                $"NX AI Assistant 启动失败：{ex}",
                "NX AI Assistant",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
        }
    }

    /// <summary>
    /// Appends the full original exception (type, message, stack, inner
    /// exceptions) to %TEMP%\NXAI_Assistant_startup_error.log.
    /// Never throws: a failed diagnostic write must not mask the original
    /// exception being reported.
    /// </summary>
    private static void LogStartupError(Exception ex)
    {
        try
        {
            string logPath = Path.Combine(Path.GetTempPath(), LogFileName);
            string record =
                $"{new string('=', 60)}{Environment.NewLine}" +
                $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] NX AI Assistant 启动异常{Environment.NewLine}" +
                $"{ex}{Environment.NewLine}{Environment.NewLine}";
            File.AppendAllText(logPath, record, Encoding.UTF8);
        }
        catch
        {
            // Swallow on purpose: diagnostics must never hide the original failure.
        }
    }
}
