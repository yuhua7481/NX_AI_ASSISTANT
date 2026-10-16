using NXAI.Assistant.Core;

namespace NXAI.Assistant.UI;

/// <summary>What the user decided on a pending tool confirmation card.</summary>
public sealed record ToolConfirmationDecision(bool Confirmed, string? ExpectedWorkPartFullPath);

/// <summary>
/// A tool call awaiting user confirmation. The controller builds it; the
/// window presents it (dimensions, unit, origin, target work part) and
/// resolves it through <see cref="Response"/> exactly once. Parameters is
/// the typed parameter object of whichever tool is pending — check
/// <see cref="ParametersKind"/> before reading the value.
/// </summary>
public sealed class ToolConfirmationRequest
{
    private readonly TaskCompletionSource<ToolConfirmationDecision?> _tcs =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    public required string ToolCallId { get; init; }
    public required string ToolName { get; init; }

    public ToolParametersKind ParametersKind { get; private init; }
    public CreateBlockParameters? BlockParameters { get; private init; }
    public CreateCylinderParameters? CylinderParameters { get; private init; }
    public CreateConeParameters? ConeParameters { get; private init; }
    public CreateSphereParameters? SphereParameters { get; private init; }

    public static ToolConfirmationRequest ForBlock(
        string toolCallId, string toolName, CreateBlockParameters parameters) => new()
    {
        ToolCallId = toolCallId,
        ToolName = toolName,
        ParametersKind = ToolParametersKind.Block,
        BlockParameters = parameters,
    };

    public static ToolConfirmationRequest ForCylinder(
        string toolCallId, string toolName, CreateCylinderParameters parameters) => new()
    {
        ToolCallId = toolCallId,
        ToolName = toolName,
        ParametersKind = ToolParametersKind.Cylinder,
        CylinderParameters = parameters,
    };

    public static ToolConfirmationRequest ForCone(
        string toolCallId, string toolName, CreateConeParameters parameters) => new()
    {
        ToolCallId = toolCallId,
        ToolName = toolName,
        ParametersKind = ToolParametersKind.Cone,
        ConeParameters = parameters,
    };

    public static ToolConfirmationRequest ForSphere(
        string toolCallId, string toolName, CreateSphereParameters parameters) => new()
    {
        ToolCallId = toolCallId,
        ToolName = toolName,
        ParametersKind = ToolParametersKind.Sphere,
        SphereParameters = parameters,
    };

    /// <summary>Resolved by the window: the user's decision, or null when the
    /// flow was aborted (window closing, request cancelled) — never executes.</summary>
    public Task<ToolConfirmationDecision?> Response => _tcs.Task;

    public bool TryRespond(ToolConfirmationDecision decision) =>
        _tcs.TrySetResult(decision);

    public bool TryAbort() =>
        _tcs.TrySetResult(null);
}

public enum ToolParametersKind
{
    Block,
    Cylinder,
    Cone,
    Sphere,
}
