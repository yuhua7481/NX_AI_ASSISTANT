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
    public CreateSketchParameters? SketchParameters { get; private init; }
    public CreateExtrudeParameters? ExtrudeParameters { get; private init; }
    public CreateRevolveParameters? RevolveParameters { get; private init; }
    public BooleanOperationParameters? BooleanParameters { get; private init; }
    public CreateHoleParameters? HoleParameters { get; private init; }
    public CreateStandardHoleParameters? StandardHoleParameters { get; private init; }
    public CreateChamferParameters? ChamferParameters { get; private init; }
    public CreateFilletParameters? FilletParameters { get; private init; }
    public CreateLinearPatternParameters? LinearPatternParameters { get; private init; }

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

    public static ToolConfirmationRequest ForSketch(
        string toolCallId, string toolName, CreateSketchParameters parameters) => new()
    {
        ToolCallId = toolCallId,
        ToolName = toolName,
        ParametersKind = ToolParametersKind.Sketch,
        SketchParameters = parameters,
    };

    public static ToolConfirmationRequest ForExtrude(
        string toolCallId, string toolName, CreateExtrudeParameters parameters) => new()
    {
        ToolCallId = toolCallId,
        ToolName = toolName,
        ParametersKind = ToolParametersKind.Extrude,
        ExtrudeParameters = parameters,
    };

    public static ToolConfirmationRequest ForRevolve(
        string toolCallId, string toolName, CreateRevolveParameters parameters) => new()
    {
        ToolCallId = toolCallId,
        ToolName = toolName,
        ParametersKind = ToolParametersKind.Revolve,
        RevolveParameters = parameters,
    };

    public static ToolConfirmationRequest ForBoolean(
        string toolCallId, string toolName, BooleanOperationParameters parameters) => new()
    {
        ToolCallId = toolCallId,
        ToolName = toolName,
        ParametersKind = ToolParametersKind.Boolean,
        BooleanParameters = parameters,
    };

    public static ToolConfirmationRequest ForHole(
        string toolCallId, string toolName, CreateHoleParameters parameters) => new()
    {
        ToolCallId = toolCallId,
        ToolName = toolName,
        ParametersKind = ToolParametersKind.Hole,
        HoleParameters = parameters,
    };

    public static ToolConfirmationRequest ForStandardHole(
        string toolCallId, string toolName, CreateStandardHoleParameters parameters) => new()
    {
        ToolCallId = toolCallId,
        ToolName = toolName,
        ParametersKind = ToolParametersKind.StandardHole,
        StandardHoleParameters = parameters,
    };

    public static ToolConfirmationRequest ForEdgeFinish(
        string toolCallId, string toolName,
        CreateChamferParameters? chamfer, CreateFilletParameters? fillet) => new()
    {
        ToolCallId = toolCallId,
        ToolName = toolName,
        ParametersKind = chamfer != null ? ToolParametersKind.Chamfer : ToolParametersKind.Fillet,
        ChamferParameters = chamfer,
        FilletParameters = fillet,
    };

    public static ToolConfirmationRequest ForLinearPattern(
        string toolCallId, string toolName, CreateLinearPatternParameters parameters) => new()
    {
        ToolCallId = toolCallId,
        ToolName = toolName,
        ParametersKind = ToolParametersKind.LinearPattern,
        LinearPatternParameters = parameters,
    };

    public CreateCircularPatternParameters? CircularPatternParameters { get; private init; }

    public CreateMirrorParameters? MirrorParameters { get; private init; }

    public static ToolConfirmationRequest ForMirror(
        string toolCallId, string toolName, CreateMirrorParameters parameters) => new()
    {
        ToolCallId = toolCallId,
        ToolName = toolName,
        ParametersKind = ToolParametersKind.Mirror,
        MirrorParameters = parameters,
    };

    public static ToolConfirmationRequest ForCircularPattern(
        string toolCallId, string toolName, CreateCircularPatternParameters parameters) => new()
    {
        ToolCallId = toolCallId,
        ToolName = toolName,
        ParametersKind = ToolParametersKind.CircularPattern,
        CircularPatternParameters = parameters,
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
    Sketch,
    Extrude,
    Revolve,
    Boolean,
    Hole,
    StandardHole,
    Fillet,
    Chamfer,
    LinearPattern,
    CircularPattern,
    Mirror,
}
