using NXAI.Assistant.Core;

namespace NXAI.Assistant.UI;

/// <summary>What the user decided on a pending tool confirmation card.</summary>
public sealed record ToolConfirmationDecision(bool Confirmed, string? ExpectedWorkPartFullPath);

/// <summary>
/// A tool call awaiting user confirmation. The controller builds it; the
/// window presents it (dimensions, unit, origin, target work part) and
/// resolves it through <see cref="Response"/> exactly once.
/// </summary>
public sealed class ToolConfirmationRequest
{
    private readonly TaskCompletionSource<ToolConfirmationDecision?> _tcs =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    public required string ToolCallId { get; init; }
    public required string ToolName { get; init; }
    public required CreateBlockParameters Parameters { get; init; }

    /// <summary>Resolved by the window: the user's decision, or null when the
    /// flow was aborted (window closing, request cancelled) — never executes.</summary>
    public Task<ToolConfirmationDecision?> Response => _tcs.Task;

    public bool TryRespond(ToolConfirmationDecision decision) =>
        _tcs.TrySetResult(decision);

    public bool TryAbort() =>
        _tcs.TrySetResult(null);
}
