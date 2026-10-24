namespace NXAI.Assistant.Core;

/// <summary>Lifecycle state of one ledger entry.</summary>
public enum ModelingExecutionState
{
    /// <summary>The executor has been invoked; no trustworthy result has arrived yet.</summary>
    Attempting,

    /// <summary>A terminating, structured result was returned by the executor.</summary>
    Recorded,
}

/// <summary>
/// One modeling execution tracked for the whole NX host lifetime, spanning
/// chat-window rebuilds. Contains nothing secret: tool name, a user-facing
/// operation summary, the structured status and the message shown in chat.
/// </summary>
public sealed record ModelingExecutionEntry(
    string LedgerId,
    string ToolName,
    string OperationSummary,
    ModelingExecutionState State,
    DateTimeOffset StartedUtc,
    ToolExecutionStatus? Status = null,
    string? ResultMessage = null,
    bool RolledBack = false,
    bool Seen = false,
    bool Acknowledged = false);

/// <summary>
/// Instance-level ledger shared by chat-window rebuilds inside one NX
/// process. Its purpose is bounded: a controller that dies mid-turn must
/// not turn a possibly-committed modeling step into "never happened".
/// Implementations never store API keys or provider configuration.
/// </summary>
public interface IModelingExecutionLedger
{
    /// <summary>
    /// Registers an execution BEFORE the executor is invoked and returns the
    /// new entry id. A registered-but-never-recorded entry is the explicit
    /// "result unknown" state — it is never auto-resolved to "not executed".
    /// </summary>
    string BeginAttempt(string toolName, string operationSummary, DateTimeOffset? startedUtc = null);

    /// <summary>
    /// Stores the executor's outcome. True terminating outcomes (all states
    /// except <see cref="ToolExecutionStatus.ExecutionUncertain"/>) finalize
    /// the entry as Recorded. ExecutionUncertain keeps the entry Attempting:
    /// geometry may or may not be committed, only explicit user review (via
    /// <see cref="Acknowledge"/>) resolves it, so an autonomous re-run of the
    /// same tool cannot bypass the unknown gate. Returns false when the id is
    /// unknown (already finalized or never begun) — an idempotence guard.
    /// </summary>
    bool RecordResult(string ledgerId, CreateBlockExecutionResult result, DateTimeOffset? recordedUtc = null);

    /// <summary>All entries the caller has not yet surfaced; marks them seen.</summary>
    IReadOnlyList<ModelingExecutionEntry> TakeUnseen();

    /// <summary>
    /// What a freshly constructed chat window should display:
    ///   - every entry never surfaced before and not yet acknowledged
    ///     (marking it seen — a Recorded fact is announced once, like a
    ///     delivered notification, but stays in the audit trail);
    ///   - every Attempting entry not yet acknowledged by a user review —
    ///     these repeat on EVERY window rebuild until explicitly verified,
    ///     and repeating the display never acknowledges anything.
    /// Acknowledged entries (user already reviewed the part state) are
    /// never re-announced on any window. This method only decides
    /// visibility; the audit trail is untouched.
    /// </summary>
    IReadOnlyList<ModelingExecutionEntry> PendingForDisplay();

    /// <summary>Full ledger snapshot (newest last), for audit and gates.</summary>
    IReadOnlyList<ModelingExecutionEntry> Snapshot();

    /// <summary>
    /// Marks a possibly-committed-or-lost execution as human-reviewed. Only an
    /// explicit user decision (e.g. the confirmation dialog used as the
    /// verification step) may call this — never model text or absence of an
    /// entry. Meaningful only for Attempting entries; Recorded entries return
    /// false because they already carry a terminating fact.
    /// </summary>
    bool Acknowledge(string ledgerId);

    /// <summary>True when any entry still lacks a user review: the executor
    /// was entered and either no outcome came back (crash/teardown) or the
    /// executor reported <see cref="ToolExecutionStatus.ExecutionUncertain"/> —
    /// in both cases geometry may or may not be committed, and the user has
    /// yet to explicitly verify the part state.</summary>
    bool HasUnresolved { get; }

    /// <summary>
    /// True when this specific tool has an Attempting entry that no user has
    /// reviewed yet — the exact condition under which an autonomous re-run of
    /// that tool would be blind repetition.
    /// </summary>
    bool HasUnacknowledgedAttempt(string toolName);
}

public sealed class ModelingExecutionLedger : IModelingExecutionLedger
{
    private readonly object _gate = new();
    private readonly Dictionary<string, ModelingExecutionEntry> _entries = new(StringComparer.Ordinal);
    private int _sequence;

    public string BeginAttempt(string toolName, string operationSummary, DateTimeOffset? startedUtc = null)
    {
        if (string.IsNullOrWhiteSpace(toolName))
            throw new ArgumentException("工具名不能为空。", nameof(toolName));
        if (operationSummary == null)
            throw new ArgumentNullException(nameof(operationSummary));

        lock (_gate)
        {
            var id = $"ledger_{++_sequence}";
            _entries[id] = new ModelingExecutionEntry(
                LedgerId: id,
                ToolName: toolName,
                OperationSummary: operationSummary.Trim(),
                State: ModelingExecutionState.Attempting,
                StartedUtc: startedUtc ?? DateTimeOffset.UtcNow);
            return id;
        }
    }

    public bool RecordResult(string ledgerId, CreateBlockExecutionResult result, DateTimeOffset? recordedUtc = null)
    {
        if (result == null)
            throw new ArgumentNullException(nameof(result));
        lock (_gate)
        {
            // A second RecordResult for the same id is programmer error; the
            // executor may not be re-fired behind the controller's back. An
            // unknown id is left untouched — never auto-resolved.
            if (!_entries.TryGetValue(ledgerId, out var entry) ||
                entry.State != ModelingExecutionState.Attempting)
            {
                return false;
            }
            // ExecutionUncertain is NOT a terminating fact: the executor ran
            // but cannot say whether geometry was committed (or the NX model
            // was partially modified). Treating it as Recorded would let an
            // autonomous re-run bypass the human gate for the same tool, so
            // the entry keeps its Attempting semantics; the observable
            // message is preserved for display. Acknowledge (explicit user
            // review) is the only way out.
            if (result.Status == ToolExecutionStatus.ExecutionUncertain)
            {
                _entries[ledgerId] = entry with
                {
                    ResultMessage = result.Message,
                    RolledBack = result.RolledBack,
                };
                return true;
            }
            _entries[ledgerId] = entry with
            {
                State = ModelingExecutionState.Recorded,
                Status = result.Status,
                ResultMessage = result.Message,
                RolledBack = result.RolledBack,
            };
            return true;
        }
    }

    public IReadOnlyList<ModelingExecutionEntry> TakeUnseen()
    {
        List<ModelingExecutionEntry> taken;
        lock (_gate)
        {
            taken = _entries.Values
                .Where(e => !e.Seen)
                .OrderBy(e => e.StartedUtc)
                .ToList();
            foreach (var e in taken)
            {
                _entries[e.LedgerId] = e with { Seen = true };
            }
        }
        return taken;
    }

    public IReadOnlyList<ModelingExecutionEntry> PendingForDisplay()
    {
        List<ModelingExecutionEntry> pending;
        lock (_gate)
        {
            // First-surface entries are announced once (an already
            // acknowledged unknown needs no announcement — the user already
            // reviewed it); unacknowledged unknowns repeat on every rebuild
            // until user review.
            pending = _entries.Values
                .Where(e => !e.Seen && !e.Acknowledged ||
                            (e.State == ModelingExecutionState.Attempting && !e.Acknowledged))
                .OrderBy(e => e.StartedUtc)
                .ToList();
            foreach (var e in pending)
            {
                if (!e.Seen)
                    _entries[e.LedgerId] = e with { Seen = true };
            }
        }
        return pending;
    }

    public IReadOnlyList<ModelingExecutionEntry> Snapshot()
    {
        lock (_gate)
        {
            return _entries.Values
                .OrderBy(e => e.StartedUtc)
                .ToList();
        }
    }

    public bool Acknowledge(string ledgerId)
    {
        lock (_gate)
        {
            // Acknowledging is only meaningful for Attempting entries: it
            // resolves the "we don't know" state by explicit human review.
            // Recorded entries already carry a terminating fact.
            if (!_entries.TryGetValue(ledgerId, out var entry) ||
                entry.State != ModelingExecutionState.Attempting)
            {
                return false;
            }
            if (!entry.Acknowledged)
                _entries[ledgerId] = entry with { Acknowledged = true };
            return true;
        }
    }

    public bool HasUnresolved
    {
        get
        {
            lock (_gate)
            {
                // "Unresolved" covers both sources of doubt (lost result via
                // crash/teardown AND ExecutionUncertain) until a user has
                // explicitly acknowledged the entry. Acknowledged entries are
                // still Attempting facts in the audit trail, but they no
                // longer need anything from anyone.
                return _entries.Values.Any(e =>
                    e.State == ModelingExecutionState.Attempting &&
                    !e.Acknowledged);
            }
        }
    }

    public bool HasUnacknowledgedAttempt(string toolName)
    {
        lock (_gate)
        {
            return _entries.Values.Any(e =>
                e.ToolName == toolName &&
                e.State == ModelingExecutionState.Attempting &&
                !e.Acknowledged);
        }
    }
}
