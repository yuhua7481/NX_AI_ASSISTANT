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

    /// <summary>
    /// READ-ONLY probe: did a previous attempt of this modeling operation
    /// actually commit a feature in the current work part? Used by the
    /// autonomous mode's unknown-outcome recovery — the controller asks the
    /// live part instead of showing a human confirmation dialog.
    ///
    /// Implementations must not mutate anything: no undo mark, no builder,
    /// no update. The probe returns one of three verifiable answers:
    ///   - Committed: a feature matching the operation exists and is not
    ///     suppressed (operationSummary + feature identity returned in
    ///     DetailsJson);
    ///   - NotCommitted: no such feature exists (the part carries no trace
    ///     of the attempt);
    ///   - the attempt could not be judged (returns null) — e.g. the probe
    ///     itself cannot run safely; the caller must then treat the state
    ///     as unknown and stop, never retry blindly.
    /// </summary>
    ModelFeatureProbeResult? ProbeCommittedFeature(string toolName, string operationSummary);

    /// <summary>
    /// READ-ONLY probe with a pre-attempt feature-identity snapshot for
    /// ATTRIBUTION. The snapshot is taken BEFORE the unknown attempt ran
    /// (by the caller, from ProbeFeatureSnapshot taken at BeginAttempt
    /// time); a feature NOT in the snapshot but matching the operation's
    /// feature kinds is attributable to THIS attempt — pre-existing
    /// same-kind features are correctly excluded. Snapshot entries are
    /// JournalIdentifier strings.
    ///
    /// Attribution rules:
    ///   - a matching-kind feature absent from the snapshot → attributable
    ///     → CommittedFound;
    ///   - only snapshot-known features of matching kind → NOT attributable
    ///     to this attempt → NotCommitted (the pre-existing features are
    ///     named in the message);
    ///   - cannot judge (no snapshot, unreadable part, unknown tool) → null.
    /// </summary>
    ModelFeatureProbeResult? ProbeCommittedFeature(
        string toolName, string operationSummary, IReadOnlySet<string>? preExistingFeatureIds);

    /// <summary>
    /// READ-ONLY pre-attempt snapshot: the JournalIdentifiers of all live,
    /// un-suppressed features of the kinds the given tool creates (boolean:
    /// UNITE/SUBTRACT/INTERSECT; extrude: EXTRUDE; …). The caller stores
    /// this BEFORE BeginAttempt and hands it back to
    /// ProbeCommittedFeature(…, snapshot) for attribution. Returns null
    /// when no work part is available (nothing to snapshot); never mutates.
    /// </summary>
    IReadOnlySet<string>? ProbeFeatureSnapshot(string toolName);
}

/// <summary>
/// The outcome of a read-only committed-feature probe. ProbeCommittedFeature
/// returning null (not this record) means "cannot judge".
/// </summary>
public sealed class ModelFeatureProbeResult
{
    private ModelFeatureProbeResult(bool committed, string message, string? detailsJson)
    {
        Committed = committed;
        Message = message;
        DetailsJson = detailsJson;
    }

    /// <summary>True when a matching committed feature was found.</summary>
    public bool Committed { get; }

    /// <summary>Human-readable finding (Chinese), safe for chat display.</summary>
    public string Message { get; }

    /// <summary>Optional tool-specific extras (e.g. feature identity).</summary>
    public string? DetailsJson { get; }

    public static ModelFeatureProbeResult CommittedFound(string message, string? detailsJson = null) =>
        new(true, message, detailsJson);

    public static ModelFeatureProbeResult NotCommitted(string message, string? detailsJson = null) =>
        new(false, message, detailsJson);
}
