namespace NXAI.Assistant.Core;

/// <summary>
/// Lightweight, always-on phase logger for crash diagnosis: one timestamped
/// line per modeling-pipeline stage (context capture, tool parsing,
/// confirmation, builder commit/verify/rollback). Written to
/// %TEMP%\NXAI_Assistant_phase.log.
///
/// Logging must never break NX or the chat: every failure (IO errors, disk
/// full, invalid path) is swallowed. The file is capped so long sessions
/// cannot grow it unbounded. Entries never contain API keys, request bodies
/// or part file paths — only stage names, counts and geometry summaries.
/// </summary>
public static class PhaseLog
{
    private static readonly object SyncRoot = new();
    private const long MaxFileBytes = 512 * 1024;

    /// <summary>Test hook: overrides the log file location.</summary>
    internal static string? LogFilePathOverride = null;

    public static void Write(string phase, string detail)
    {
        try
        {
            var path = LogFilePathOverride
                ?? Path.Combine(Path.GetTempPath(), "NXAI_Assistant_phase.log");
            lock (SyncRoot)
            {
                try
                {
                    if (File.Exists(path) && new FileInfo(path).Length > MaxFileBytes)
                        File.WriteAllText(path, "");
                }
                catch
                {
                    // cap failure must not prevent the actual log write attempt
                }
                File.AppendAllText(path,
                    $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [T{Environment.CurrentManagedThreadId}] {phase} {detail}{Environment.NewLine}");
            }
        }
        catch
        {
            // logging must never break the app
        }
    }
}
