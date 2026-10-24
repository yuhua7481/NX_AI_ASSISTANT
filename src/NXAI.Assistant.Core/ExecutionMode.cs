using System.Text.Json;

namespace NXAI.Assistant.Core;

/// <summary>
/// How modeling tool calls are authorized before execution. Confirm is the
/// safe default: every modifying tool waits for an explicit user decision.
/// Autonomous skips the per-call confirmation only for tools on the
/// autonomous allowlist, and never relaxes parameter validation, work-part
/// checks, NX UI-thread dispatch, UndoMark/rollback or postcondition
/// verification.
/// </summary>
public enum ExecutionMode
{
    Confirm,
    Autonomous,
}

public static class ExecutionModeExtensions
{
    /// <summary>Chinese display name shown in the UI and chat log.</summary>
    public static string DisplayName(this ExecutionMode mode) => mode switch
    {
        ExecutionMode.Confirm => "确认模式",
        ExecutionMode.Autonomous => "自主模式",
        _ => "确认模式",
    };

    /// <summary>Stable wire/file name; unknown values never parse back.</summary>
    public static string ToFileName(this ExecutionMode mode) => mode switch
    {
        ExecutionMode.Autonomous => "autonomous",
        _ => "confirm",
    };

    public static ExecutionMode? FromFileName(string? value) => value switch
    {
        "confirm" => ExecutionMode.Confirm,
        "autonomous" => ExecutionMode.Autonomous,
        _ => null,
    };
}

/// <summary>
/// The closed set of registered, non-destructive modeling tools that
/// autonomous mode may execute without a per-call confirmation. The list is
/// explicit and hand-maintained: future destructive tools (delete, modify,
/// suppress, rename, save, save-as, export, …) must never be added here
/// without a separately approved permission decision.
/// </summary>
public static class AutonomousToolAllowlist
{
    public static readonly IReadOnlyList<string> Tools = new[]
    {
        CreateBlockTool.Name,
        CreateCylinderTool.Name,
        CreateConeTool.Name,
        CreateSphereTool.Name,
        CreateSketchTool.Name,
        CreateExtrudeTool.Name,
        CreateRevolveTool.Name,
        BooleanOperationTool.Name,
        CreateHoleTool.Name,
        StandardHoleToolContract.ThreadedName,
        StandardHoleToolContract.ClearanceName,
        StandardHoleToolContract.CounterboredName,
        StandardHoleToolContract.CountersunkName,
        EdgeFinishToolContract.FilletName,
        EdgeFinishToolContract.ChamferName,
        LinearPatternToolContract.Name,
        CircularPatternToolContract.Name,
        MirrorToolContract.Name,
    };

    private static readonly HashSet<string> ToolSet =
        new(Tools, StringComparer.Ordinal);

    /// <summary>True only for a tool name on the autonomous allowlist.</summary>
    public static bool Contains(string toolName) =>
        !string.IsNullOrWhiteSpace(toolName) && ToolSet.Contains(toolName);
}

/// <summary>
/// Persistence seam for the user-selected execution mode. Load returns null
/// when nothing usable is stored — the caller then applies the safe default
/// (Confirm). Implementations must never throw for a missing file.
/// </summary>
public interface IExecutionModeStore
{
    ExecutionMode? Load();

    void Save(ExecutionMode mode);
}

/// <summary>
/// JSON-file store for the execution mode, following the same scheme as the
/// model configuration (%APPDATA%\NX AI Assistant\). Contains no secrets.
/// A missing, unreadable or malformed file loads as null (→ default Confirm);
/// saving never silently corrupts: failures propagate to the caller so the
/// UI can keep the mode that is actually in effect.
/// </summary>
public sealed class JsonFileExecutionModeStore : IExecutionModeStore
{
    public static string DefaultFilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "NX AI Assistant",
        "execution-mode.json");

    private readonly string _filePath;

    public JsonFileExecutionModeStore(string? filePath = null) =>
        _filePath = string.IsNullOrWhiteSpace(filePath) ? DefaultFilePath : filePath;

    public ExecutionMode? Load()
    {
        if (!File.Exists(_filePath))
            return null;
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(_filePath));
            if (doc.RootElement.ValueKind != JsonValueKind.Object ||
                !doc.RootElement.TryGetProperty("executionMode", out var modeElement) ||
                modeElement.ValueKind != JsonValueKind.String)
            {
                return null;
            }
            return ExecutionModeExtensions.FromFileName(modeElement.GetString());
        }
        catch (JsonException)
        {
            return null;
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    public void Save(ExecutionMode mode)
    {
        string? directory = Path.GetDirectoryName(_filePath);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);
        File.WriteAllText(
            _filePath,
            JsonSerializer.Serialize(new { executionMode = mode.ToFileName() }));
    }
}

/// <summary>
/// The pure authorization rule enforced at the controller's dispatch stage:
/// in Confirm mode every modifying tool waits for the user; in Autonomous
/// mode only allowlisted tools may skip the per-call confirmation. Read-only
/// tools never pass through here. This is the single place the decision is
/// made — the UI switch only changes the mode, never this rule.
/// </summary>
public sealed class ExecutionModePolicy
{
    public bool RequiresConfirmation(ExecutionMode currentMode, string toolName) =>
        currentMode != ExecutionMode.Autonomous || !AutonomousToolAllowlist.Contains(toolName);
}

/// <summary>
/// Thread-safe holder of the effective execution mode, backed by an optional
/// store. The constructor applies the stored mode or the Confirm default, so
/// the backend policy and the UI switch can never disagree about the startup
/// state. SetMode persists first: a failed save keeps the previous mode in
/// memory, so the mode in effect is always the mode that was successfully
/// applied — the UI must re-read <see cref="CurrentMode"/> after a failure.
/// </summary>
public sealed class ExecutionModeManager
{
    private readonly object _gate = new();
    private readonly IExecutionModeStore? _store;
    private ExecutionMode _current;

    public ExecutionModeManager(IExecutionModeStore? store = null)
    {
        _store = store;
        _current = store?.Load() ?? ExecutionMode.Confirm;
    }

    public event EventHandler? ModeChanged;

    public ExecutionMode CurrentMode
    {
        get { lock (_gate) return _current; }
    }

    /// <summary>
    /// Applies a new mode. Persists before publishing: on a store failure the
    /// in-memory mode stays unchanged and the exception propagates. The
    /// caller decides whether persistence failures are fatal — the UI shows
    /// an error and keeps the switch on the still-effective mode.
    /// </summary>
    public void SetMode(ExecutionMode mode)
    {
        lock (_gate)
        {
            if (mode == _current)
                return;
            _store?.Save(mode);
            _current = mode;
        }
        ModeChanged?.Invoke(this, EventArgs.Empty);
    }
}
