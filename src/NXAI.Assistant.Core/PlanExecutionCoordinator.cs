using System.Text.Json;

namespace NXAI.Assistant.Core;

/// <summary>
/// Callbacks the coordinator needs from its host (implemented by the chat
/// layer). All of them run on the NX UI thread. The executor callback is the
/// SAME INxToolExecutor instance single-shot calls use — plans get no new
/// execution powers.
/// </summary>
public interface IPlanExecutionHost
{
    /// <summary>Runs one typed step through the registered executor.
    /// Returns the real NX outcome.</summary>
    CreateBlockExecutionResult ExecuteStep(ModelingStep step, string? referenceOverride);

    /// <summary>Confirmation-mode gate: ask the user for THIS step (showing
    /// the whole plan for context). Return true to proceed.</summary>
    Task<bool> ConfirmStepAsync(ModelingStep step, ModelingPlan plan, CancellationToken ct);

    /// <summary>Autonomous-mode gate: one notice that the whole plan will run
    /// serially (no per-step cards). Return true to proceed.</summary>
    Task<bool> ConfirmPlanAsync(ModelingPlan plan, CancellationToken ct);
}

/// <summary>
/// Serially executes a validated <see cref="ModelingPlan"/> step by step:
///   - per-step authorization: Confirm mode asks before EVERY modifying
///     step; Autonomous mode asks once for the plan then runs it (both via
///     the same ExecutionModePolicy rules the single-shot path uses);
///   - reference hand-off: a dependent step receives the dependency's REAL
///     result reference (e.g. sketchRef) — never the model's guess;
///   - fail-fast: the first failed step stops the plan; later steps are
///     reported as Skipped; the aggregate result is honest about partial
///     completion;
///   - every step is one executor call, one UndoMark/rollback cycle, one
///     postcondition verification — the plan adds coordination, not powers.
/// </summary>
public sealed class PlanExecutionCoordinator
{
    private readonly IPlanExecutionHost _host;
    private readonly ExecutionModeManager? _modeManager;
    private readonly ExecutionModePolicy _policy = new();

    public PlanExecutionCoordinator(IPlanExecutionHost host, ExecutionModeManager? modeManager = null)
    {
        _host = host;
        _modeManager = modeManager;
    }

    public async Task<ModelingPlanResult> ExecuteAsync(
        ModelingPlan plan, CancellationToken cancellationToken)
    {
        var results = new List<PlanStepResult>();
        var references = new Dictionary<string, string>(StringComparer.Ordinal);

        for (int i = 0; i < plan.Steps.Count; i++)
        {
            var step = plan.Steps[i];
            cancellationToken.ThrowIfCancellationRequested();

            // Dependencies must have succeeded (serial chain).
            foreach (var dep in step.DependsOn)
            {
                var depResult = results.FirstOrDefault(r => r.Index == dep);
                if (depResult == null || depResult.Status != PlanStepStatus.Succeeded)
                {
                    results.Add(new PlanStepResult(i, step.ToolName, PlanStepStatus.Skipped,
                        $"依赖的步骤 {dep + 1} 未成功完成。", null, null));
                    return Finish(plan, results, skippedFrom: i + 1);
                }
            }

            // Reference hand-off: replace the placeholder with the real
            // reference from the dependency's result. The schema field name
            // (sketch_ref) maps to the payload's reference key (sketchRef).
            string? referenceOverride = null;
            if (step.InjectReferenceFromDependency != null)
            {
                var payloadKey = step.InjectReferenceFromDependency switch
                {
                    "sketch_ref" => "sketchRef",
                    var other => other,
                };
                if (!references.TryGetValue(payloadKey, out var reference))
                {
                    results.Add(new PlanStepResult(i, step.ToolName, PlanStepStatus.Failed,
                        $"依赖步骤未产生 {payloadKey} 引用。", null, null));
                    return Finish(plan, results, skippedFrom: i + 1);
                }
                referenceOverride = reference;
            }

            // Authorization — same policy as single-shot dispatch.
            var mode = _modeManager?.CurrentMode ?? ExecutionMode.Confirm;
            if (_policy.RequiresConfirmation(mode, step.ToolName))
            {
                if (!await _host.ConfirmStepAsync(step, plan, cancellationToken))
                {
                    results.Add(new PlanStepResult(i, step.ToolName, PlanStepStatus.Failed,
                        "用户取消了本步骤。", null, null));
                    return Finish(plan, results, skippedFrom: i + 1);
                }
            }
            else if (results.Count == 0)
            {
                // First step of the plan in autonomous mode: one plan-level
                // notice, then the whole serial chain runs without per-step
                // cards.
                if (!await _host.ConfirmPlanAsync(plan, cancellationToken))
                {
                    results.Add(new PlanStepResult(i, step.ToolName, PlanStepStatus.Failed,
                        "用户取消了整个建模计划。", null, null));
                    return Finish(plan, results, skippedFrom: i);
                }
            }

            results.Add(new PlanStepResult(i, step.ToolName, PlanStepStatus.Running, "", null, null));
            var execution = _host.ExecuteStep(step, referenceOverride);
            if (execution.Status != ToolExecutionStatus.Success)
            {
                results[^1] = new PlanStepResult(i, step.ToolName, PlanStepStatus.Failed,
                    execution.Message, execution, null);
                return Finish(plan, results, skippedFrom: i + 1);
            }

            // Collect real references from the result payload for hand-off.
            foreach (var (key, value) in ExtractReferences(execution.DetailsJson))
                references[key] = value;

            results[^1] = new PlanStepResult(i, step.ToolName, PlanStepStatus.Succeeded,
                execution.Message, execution, new Dictionary<string, string>(references));
        }

        return Finish(plan, results, skippedFrom: null);
    }

    private static ModelingPlanResult Finish(
        ModelingPlan plan, List<PlanStepResult> results, int? skippedFrom)
    {
        if (skippedFrom.HasValue)
        {
            for (int i = skippedFrom.Value; i < plan.Steps.Count; i++)
            {
                if (results.All(r => r.Index != i))
                {
                    results.Add(new PlanStepResult(i, plan.Steps[i].ToolName,
                        PlanStepStatus.Skipped, "前序步骤失败，本步骤未执行。", null, null));
                }
            }
        }
        results.Sort((a, b) => a.Index.CompareTo(b.Index));
        return new ModelingPlanResult
        {
            IntentSummary = plan.IntentSummary,
            StepResults = results,
        };
    }

    /// <summary>Pulls NXHost-managed references (sketchRef/newBodyRef/…) out
    /// of the execution's DetailsJson payload.</summary>
    internal static IReadOnlyDictionary<string, string> ExtractReferences(string? detailsJson)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        if (string.IsNullOrWhiteSpace(detailsJson))
            return result;
        try
        {
            using var doc = JsonDocument.Parse(detailsJson);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
                return result;
            foreach (var prop in doc.RootElement.EnumerateObject())
            {
                if (prop.Value.ValueKind == JsonValueKind.String &&
                    prop.Name is "sketchRef" or "newBodyRef" or "resultBodyRef")
                {
                    result[prop.Name] = prop.Value.GetString() ?? "";
                }
            }
        }
        catch (JsonException)
        {
            // Malformed details: no references to hand off.
        }
        return result;
    }
}
