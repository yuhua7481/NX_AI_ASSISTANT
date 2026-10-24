namespace NXAI.Assistant.Core;

/// <summary>
/// A structured modeling plan: the model's natural-language intent turned
/// into an ordered list of typed tool steps with dependencies and expected
/// geometry. The MODEL produces the intent; CODE validates the plan, binds
/// object references between steps, schedules serial execution on the NX UI
/// thread and verifies postconditions. The model never decides execution by
/// free-form text or tag splicing — every step is a registered, Schema-
/// validated tool invocation routed through the same authorization gates as
/// a single-shot call.
/// </summary>
public sealed class ModelingPlan
{
    /// <summary>Short summary of the user's intent (for display/logging).</summary>
    public required string IntentSummary { get; init; }

    /// <summary>Ordered steps. Execution is strictly serial: step N runs only
    /// after step N-1 committed successfully (and any declared dependencies).</summary>
    public required IReadOnlyList<ModelingStep> Steps { get; init; }

    /// <summary>Parameters that could not be resolved from the request — the
    /// coordinator must ASK instead of guessing when this is non-empty.</summary>
    public IReadOnlyList<string> UnresolvedAmbiguities { get; init; } = Array.Empty<string>();
}

/// <summary>
/// One step of a modeling plan: a registered tool with Schema-validated
/// typed parameters, its declared dependencies and the expected geometry
/// used for postcondition verification.
/// </summary>
public sealed class ModelingStep
{
    public required int Index { get; init; }
    public required string ToolName { get; init; }

    /// <summary>Typed parameters, already normalized to millimeters and
    /// Schema-validated. Which record type matches is determined by the
    /// registered tool contract (see ModelingPlanFactory).</summary>
    public required object Parameters { get; init; }

    /// <summary>Step indices whose successful output this step consumes
    /// (e.g. the extrude depends on the sketch step). Steps may only depend
    /// on EARLIER indices — the chain is serial.</summary>
    public IReadOnlyList<int> DependsOn { get; init; } = Array.Empty<int>();

    /// <summary>
    /// When set, the executor injects the named reference from the
    /// dependency's real result into this step's parameters, overriding any
    /// model-supplied placeholder: "sketch_ref" ← the dependency's
    /// sketchRef. The model never gets to name objects across steps.
    /// </summary>
    public string? InjectReferenceFromDependency { get; init; }

    /// <summary>Human-readable expectation used in confirmation cards and
    /// the final summary (e.g. "圆形草图 d30 @ XZ 平面").</summary>
    public required string Expectation { get; init; }

    /// <summary>Postcondition kind the executor should verify after commit
    /// (see GeometricExpectation).</summary>
    public GeometricExpectation Expected { get; init; } = GeometricExpectation.None;
}

/// <summary>Postcondition families the self-check and coordinator verify.</summary>
public enum GeometricExpectation
{
    /// <summary>No geometric postcondition beyond tool-reported success.</summary>
    None,

    /// <summary>Sketch exists, is re-resolvable, and has the expected curve count.</summary>
    SketchProfile,

    /// <summary>Exactly one new solid body with an axis-aligned bounding box
    /// equal to the expected box (within tolerance).</summary>
    BodyBoundingBox,

    /// <summary>A revolution solid: verify the body exists and the bounding
    /// box matches the analytic envelope; shape-type confirmation (torus vs
    /// cylinder, same box) requires live feature/topology checks and is
    /// reported as unverified when unavailable — never silently passed.</summary>
    RevolutionEnvelope,
}

/// <summary>Execution state of one plan step.</summary>
public enum PlanStepStatus
{
    Pending,
    AwaitingConfirmation,
    Running,
    Succeeded,
    Failed,
    Skipped,
}

/// <summary>Result of executing one plan step (real NX outcome, not text).</summary>
public sealed record PlanStepResult(
    int Index,
    string ToolName,
    PlanStepStatus Status,
    string Message,
    CreateBlockExecutionResult? Execution,
    IReadOnlyDictionary<string, string>? References);

/// <summary>
/// One self-check case result: input, expectation, actual verified geometry
/// and the failure stage. Produced by the NX-hosted self-check; the shape is
/// defined in Core so the UI can display it without referencing NXOpen.
/// </summary>
public sealed record GeometrySelfCheckCase(
    string Name,
    bool Passed,
    string Stage,
    string Input,
    string Expected,
    string Actual,
    string? Error);

/// <summary>Aggregate self-check report for one temporary-part run.</summary>
public sealed record GeometrySelfCheckReport(
    string PartIdentity,
    bool AllPassed,
    int PassedCount,
    int FailedCount,
    IReadOnlyList<GeometrySelfCheckCase> Cases)
{
    /// <summary>Phase 10.2 first-page coverage fields: how many of the
    /// registered modeling tools this report's cases actually exercised,
    /// and the isolation outcome (temp-part close + user-part restore).
    /// Optional with defaults so existing constructor call sites compile.</summary>
    public int RegisteredToolCount { get; init; } = 18;
    public int CoveredToolCount { get; init; } = 0;
    public int NegativePassedCount { get; init; } = 0;
    public int NegativeFailedCount { get; init; } = 0;
    public string CleanupResult { get; init; } = "";
}

/// <summary>Aggregate result of a plan run: exactly what completed, what
/// failed and at which stage — the partial-truth contract (never "all done"
/// when step 2 of 3 failed).</summary>
public sealed class ModelingPlanResult
{
    public required string IntentSummary { get; init; }
    public required IReadOnlyList<PlanStepResult> StepResults { get; init; }

    /// <summary>True only when every step reached Succeeded.</summary>
    public bool AllSucceeded => StepResults.Count > 0 && StepResults.All(r => r.Status == PlanStepStatus.Succeeded);

    /// <summary>Index of the first failed step, null when all succeeded.</summary>
    public int? FirstFailedIndex =>
        StepResults.Where(r => r.Status == PlanStepStatus.Failed)
            .Select(r => (int?)r.Index)
            .FirstOrDefault();

    /// <summary>User-facing summary: honest about partial completion.</summary>
    public string Describe()
    {
        if (AllSucceeded)
            return $"建模计划全部完成（{StepResults.Count} 步）：" +
                string.Join("；", StepResults.Select(r => r.Message));

        var done = StepResults.Where(r => r.Status == PlanStepStatus.Succeeded).ToList();
        var failed = StepResults.First(r => r.Status == PlanStepStatus.Failed);
        var skipped = StepResults.Where(r => r.Status is PlanStepStatus.Skipped or PlanStepStatus.Pending).ToList();
        var parts = new List<string>();
        if (done.Count > 0)
            parts.Add($"已完成 {done.Count} 步：" + string.Join("；", done.Select(r => r.Message)));
        parts.Add($"第 {failed.Index + 1} 步（{failed.ToolName}）失败：{failed.Message}");
        if (skipped.Count > 0)
            parts.Add($"未执行 {skipped.Count} 步（后续步骤依赖失败步骤）。");
        return string.Join("。", parts) + "。";
    }
}
