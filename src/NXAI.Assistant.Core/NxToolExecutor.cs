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
    /// Creates a SIMPLE blind hole on the referenced planar face (Phase 9.1
    /// minimum). The face reference must resolve to exactly one planar face
    /// in the current work part; the hole axis runs along the face normal at
    /// the requested UV offset from the face's parameter midpoint. Same
    /// confirmation/precondition contract as the other geometry tools.
    /// </summary>
    CreateBlockExecutionResult ExecuteCreateHole(
        CreateHoleParameters parameters, string? expectedWorkPartFullPath);

    /// <summary>
    /// Creates a STANDARD hole (threaded / simple screw clearance /
    /// counterbored / countersunk) of the requested family and size on the
    /// referenced planar face. All diameters, angles, chamfers and (for the
    /// counterbored/countersunk families) depths come from the installed NX
    /// modeling-standards XML tables — the record must match UNIQUELY or the
    /// execution is refused. Same confirmation/precondition contract as the
    /// other geometry tools.
    /// </summary>
    CreateBlockExecutionResult ExecuteCreateStandardHole(
        CreateStandardHoleParameters parameters, string? expectedWorkPartFullPath);

    /// <summary>
    /// Creates a constant-radius edge blend (fillet) on the edges of the
    /// CURRENT selection. The selection is re-resolved and re-verified at
    /// execution time (after user confirmation); a stale or empty selection
    /// refuses. Same confirmation/precondition contract as the other
    /// geometry tools.
    /// </summary>
    CreateBlockExecutionResult ExecuteCreateFillet(
        CreateFilletParameters parameters, string? expectedWorkPartFullPath);

    /// <summary>
    /// Creates a symmetric chamfer (NX ChamferOption.SymmetricOffsets) on
    /// the edges of the CURRENT selection. Same selection/confirmation
    /// contract as the fillet.
    /// </summary>
    CreateBlockExecutionResult ExecuteCreateChamfer(
        CreateChamferParameters parameters, string? expectedWorkPartFullPath);

    /// <summary>
    /// Creates a 1-D LINEAR pattern of the auto-resolved target: feature
    /// mode arrays the most recent verified FEATURE (PatternFeatureBuilder);
    /// geometry mode arrays the most recent verified BODY
    /// (PatternGeometryBuilder). Direction/count/pitch come from the
    /// parameters; the target re-resolves live at execution time. Same
    /// confirmation/precondition contract as the other geometry tools.
    /// </summary>
    CreateBlockExecutionResult ExecuteCreateLinearPattern(
        CreateLinearPatternParameters parameters, string? expectedWorkPartFullPath);

    /// <summary>
    /// Creates a native CIRCULAR pattern of the resolved target: feature
    /// mode arrays a resolved FEATURE, geometry mode a resolved BODY —
    /// same modes/semantics as the linear tool. The rotation axis comes
    /// from the parameters' axis kind (absolute axis through the origin,
    /// or the current selection's datum axis / straight edge, resolved and
    /// verified host-side). Count = total instances including the seed;
    /// full circle (no total angle) divides 360° evenly, partial arcs end
    /// ON the requested end angle.
    /// </summary>
    CreateBlockExecutionResult ExecuteCreateCircularPattern(
        CreateCircularPatternParameters parameters, string? expectedWorkPartFullPath);

    /// <summary>
    /// Creates a native MIRROR of the resolved objects: geometry mode
    /// mirrors one or more solid bodies (MirrorBodyBuilder, sources kept);
    /// feature mode mirrors one or more modeling features
    /// (MirrorFeatureBuilder, history preserved). The mirror plane is a
    /// selected datum plane or a planar solid face, resolved and verified
    /// host-side with roles separated from the seeds.
    /// </summary>
    CreateBlockExecutionResult ExecuteCreateMirror(
        CreateMirrorParameters parameters, string? expectedWorkPartFullPath);

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
    /// feature kind is attributable to THIS attempt — pre-existing
    /// same-kind features are correctly excluded. Snapshot entries are
    /// JournalIdentifier strings. <paramref name="operationType"/> is the
    /// STRUCTURED boolean operation of the pending attempt (Unite/Subtract/
    /// Intersect) — only that exact NX feature kind counts as evidence; a
    /// null operationType is unjudgeable for booleans.
    ///
    /// Attribution rules:
    ///   - a matching-kind feature absent from the snapshot → attributable
    ///     → CommittedFound;
    ///   - only snapshot-known features of matching kind → NOT attributable
    ///     to this attempt → NotCommitted (the pre-existing features are
    ///     named in the message);
    ///   - null snapshot = snapshot UNAVAILABLE (cannot attribute — fail
    ///     safe); a NON-NULL EMPTY snapshot is a VALID baseline ("no
    ///     matching features existed before the attempt") — a new matching
    ///     feature is then attributable;
    ///   - cannot judge (unreadable part, unknown tool, missing operation
    ///     type) → null.
    /// </summary>
    ModelFeatureProbeResult? ProbeCommittedFeature(
        string toolName, string operationSummary, IReadOnlySet<string>? preExistingFeatureIds,
        BooleanOperationType? operationType = null);

    /// <summary>
    /// READ-ONLY pre-attempt snapshot: the JournalIdentifiers of all live,
    /// un-suppressed features of the given NX feature kind (for a boolean
    /// attempt: exactly UNITE, SUBTRACT or INTERSECT per the attempt's
    /// structured operation type — never all three at once). The caller
    /// stores this BEFORE BeginAttempt and hands it back to
    /// ProbeCommittedFeature(…, snapshot, operationType) for attribution.
    /// Returns null when no work part is available (nothing to snapshot);
    /// never mutates. An EMPTY non-null set is a valid baseline.
    /// </summary>
    IReadOnlySet<string>? ProbeFeatureSnapshot(string toolName, string? featureKind = null);
}

/// <summary>
/// Phase 9.5: optional hook an executor implements so the controller can
/// hand it the session's most recent executor-verified body reference (e.g.
/// "BLOCK(5)"). The edge-finish tools use it as an auto-target candidate
/// when the user's selection is empty — the candidate is only a HINT: the
/// executor must still re-resolve and re-verify the body in the live part
/// (deleted/undone refs refuse). The provider returns the controller's
/// CURRENT value (a delegate, not a copy) so a new conversation yields
/// null immediately.
/// </summary>
public interface NxTargetResolutionSink
{
    Func<string?>? LastCreatedBodyRefProvider { get; set; }

    /// <summary>The controller's most recent executor-verified FEATURE
    /// reference (hole/blend/chamfer) for the linear-pattern FEATURE mode;
    /// null until a feature-creating result succeeds this conversation.</summary>
    Func<string?>? LastCreatedFeatureRefProvider { get; set; }
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
