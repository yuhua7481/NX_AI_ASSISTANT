namespace NXAI.Assistant.Core;

/// <summary>
/// Structured outcome of one create_block execution attempt. Implementations
/// run inside the NX process; every field must be safe to show to the user
/// and to send back to the model (no secrets, no file paths of user config).
/// </summary>
public sealed class CreateBlockExecutionResult
{
    private CreateBlockExecutionResult(
        ToolExecutionStatus status,
        string message,
        double? verifiedLengthMm,
        double? verifiedWidthMm,
        double? verifiedHeightMm,
        bool rolledBack,
        string? workPartIdentity,
        string? detailsJson = null)
    {
        Status = status;
        Message = message;
        VerifiedLengthMm = verifiedLengthMm;
        VerifiedWidthMm = verifiedWidthMm;
        VerifiedHeightMm = verifiedHeightMm;
        RolledBack = rolledBack;
        WorkPartIdentity = workPartIdentity;
        DetailsJson = detailsJson;
    }

    public ToolExecutionStatus Status { get; }
    public string Message { get; }
    public double? VerifiedLengthMm { get; }
    public double? VerifiedWidthMm { get; }
    public double? VerifiedHeightMm { get; }

    /// <summary>True when a failed or unverifiable attempt was undone in NX.</summary>
    public bool RolledBack { get; }

    /// <summary>Identity (name + full path) of the work part the executor saw, null when none.</summary>
    public string? WorkPartIdentity { get; }

    /// <summary>
    /// Optional JSON object of tool-specific extras merged into the tool
    /// result payload sent back to the model (e.g. sketchRef/newBodyRef —
    /// NXHost-managed references for follow-up tools). Contains no secrets.
    /// </summary>
    public string? DetailsJson { get; }

    public static CreateBlockExecutionResult Success(
        double verifiedLengthMm, double verifiedWidthMm, double verifiedHeightMm,
        string message, string? workPartIdentity, string? detailsJson = null) =>
        new(ToolExecutionStatus.Success, message,
            verifiedLengthMm, verifiedWidthMm, verifiedHeightMm, false, workPartIdentity, detailsJson);

    public static CreateBlockExecutionResult Failure(
        ToolExecutionStatus status, string message,
        bool rolledBack = false, string? workPartIdentity = null, string? detailsJson = null) =>
        new(status, message, null, null, null, rolledBack, workPartIdentity, detailsJson);
}

/// <summary>
/// Executes registered modeling tools inside the NX process. Implementations
/// must run on the NX UI thread, validate parameters before touching the
/// session, and verify postconditions against real geometry — never trusting
/// the caller or an absence of exceptions. No method here may save, rename,
/// or re-path the work part.
/// </summary>
public interface INxToolExecutor
{
    /// <summary>
    /// Creates a block in the current work part. <paramref name="expectedWorkPartFullPath"/>
    /// is the part identity the user confirmed; when the work part has changed
    /// since confirmation the execution must be refused (PreconditionsChanged).
    /// </summary>
    CreateBlockExecutionResult ExecuteCreateBlock(
        CreateBlockParameters parameters, string? expectedWorkPartFullPath);

    /// <summary>
    /// Creates a cylinder (axis +Z, origin = bottom-face center) in the current
    /// work part. Same confirmation/precondition contract as the block.
    /// </summary>
    CreateBlockExecutionResult ExecuteCreateCylinder(
        CreateCylinderParameters parameters, string? expectedWorkPartFullPath);

    /// <summary>
    /// Creates a right circular cone (axis +Z, apex above the base, origin =
    /// bottom-face center) in the current work part. Same confirmation /
    /// precondition contract as the block.
    /// </summary>
    CreateBlockExecutionResult ExecuteCreateCone(
        CreateConeParameters parameters, string? expectedWorkPartFullPath);

    /// <summary>
    /// Creates a real spherical solid (origin = sphere center) in the current
    /// work part. Same confirmation/precondition contract as the block.
    /// </summary>
    CreateBlockExecutionResult ExecuteCreateSphere(
        CreateSphereParameters parameters, string? expectedWorkPartFullPath);

    /// <summary>
    /// Creates a real parametric NX sketch with one closed profile (rectangle
    /// or circle) on the requested plane. planeMode=SelectedPlane resolves the
    /// single selected planar face / datum plane from the current selection;
    /// anything else refuses. Returns the sketch reference in DetailsJson.
    /// </summary>
    CreateBlockExecutionResult ExecuteCreateSketch(
        CreateSketchParameters parameters, string? expectedWorkPartFullPath);

    /// <summary>
    /// Extrudes a previously created sketch (resolved by its controlled
    /// reference) along the sketch normal into a solid. Refuses stale or
    /// unknown sketch references. Returns the new body reference in DetailsJson.
    /// </summary>
    CreateBlockExecutionResult ExecuteCreateExtrude(
        CreateExtrudeParameters parameters, string? expectedWorkPartFullPath);

    /// <summary>
    /// Revolves a previously created sketch around an explicit axis (selected
    /// linear object or absolute coordinate axis) to build a solid of
    /// revolution. The axis must lie in the sketch plane; refuses otherwise.
    /// Returns the new body reference in DetailsJson.
    /// </summary>
    CreateBlockExecutionResult ExecuteCreateRevolve(
        CreateRevolveParameters parameters, string? expectedWorkPartFullPath);

    /// <summary>
    /// Runs unite/subtract/intersect on two explicitly referenced distinct
    /// solid bodies. Body refs are NXHost-managed (from earlier tool results
    /// or the context snapshot); stale or ambiguous refs refuse. Returns the
    /// result body reference in DetailsJson.
    /// </summary>
    CreateBlockExecutionResult ExecuteBooleanOperation(
        BooleanOperationParameters parameters, string? expectedWorkPartFullPath);
}
