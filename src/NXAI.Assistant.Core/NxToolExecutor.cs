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
        string? workPartIdentity)
    {
        Status = status;
        Message = message;
        VerifiedLengthMm = verifiedLengthMm;
        VerifiedWidthMm = verifiedWidthMm;
        VerifiedHeightMm = verifiedHeightMm;
        RolledBack = rolledBack;
        WorkPartIdentity = workPartIdentity;
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

    public static CreateBlockExecutionResult Success(
        double verifiedLengthMm, double verifiedWidthMm, double verifiedHeightMm,
        string message, string? workPartIdentity) =>
        new(ToolExecutionStatus.Success, message,
            verifiedLengthMm, verifiedWidthMm, verifiedHeightMm, false, workPartIdentity);

    public static CreateBlockExecutionResult Failure(
        ToolExecutionStatus status, string message,
        bool rolledBack = false, string? workPartIdentity = null) =>
        new(status, message, null, null, null, rolledBack, workPartIdentity);
}

/// <summary>
/// Executes a registered modeling tool inside the NX process. Implementations
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
}
