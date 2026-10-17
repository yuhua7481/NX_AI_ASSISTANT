using System.Text.Json;

namespace NXAI.Assistant.Core;

/// <summary>
/// Builds a validated <see cref="ModelingPlan"/> from an ordered list of raw
/// tool calls (either structured tool_calls or tag-protocol-normalized
/// calls — both arrive as the same ParsedToolCall shape). Every step is
/// parsed by the SAME typed parsers used by single-shot execution; unknown
/// tools, Schema violations, non-serial dependencies and placeholder
/// references that should be injected are caught HERE, before any executor
/// runs. This factory is the single point where "model said" becomes "code
/// verified".
/// </summary>
public sealed class ModelingPlanFactory
{
    /// <summary>
    /// Normalizes raw tool-call arguments per step: parses typed parameters,
    /// canonicalizes tool names through the closed alias table, and strips
    /// placeholder reference values that will be injected from a dependency
    /// (the model may not name objects across steps — a placeholder like
    /// "SKETCH_000" in a dependent step is replaced at execution time by the
    /// dependency's REAL result reference; a non-placeholder value must
    /// still resolve, and is validated then).
    /// </summary>
    public ModelingPlan? Create(
        string intentSummary,
        IReadOnlyList<ParsedToolCall> calls,
        out string? error)
    {
        error = null;
        if (calls.Count == 0)
        {
            error = "建模计划为空：模型未给出任何工具调用。";
            return null;
        }
        if (calls.Count > MaxPlanSteps)
        {
            error = $"建模计划包含 {calls.Count} 个步骤，超过当前版本上限 {MaxPlanSteps}。";
            return null;
        }

        var steps = new List<ModelingStep>(calls.Count);
        var registry = CreateBlockTool.CreateRegistry();

        for (int i = 0; i < calls.Count; i++)
        {
            var call = calls[i];
            var (canonicalName, argumentsJson) = ToolTagProtocol.NormalizeCall(call.FunctionName, call.ArgumentsJson, out string? normalizeError);
            if (normalizeError != null)
            {
                error = normalizeError;
                return null;
            }
            if (!registry.IsRegistered(canonicalName))
            {
                error = $"计划第 {i + 1} 步使用了未注册工具：{canonicalName}。已拒绝。";
                return null;
            }

            var step = ParseStep(i, canonicalName, argumentsJson, steps, out error);
            if (step == null)
                return null;
            steps.Add(step);
        }

        return new ModelingPlan
        {
            IntentSummary = intentSummary,
            Steps = steps,
        };
    }

    internal const int MaxPlanSteps = 2;

    private ModelingStep? ParseStep(
        int index, string toolName, string argumentsJson,
        List<ModelingStep> priorSteps, out string? error)
    {
        error = null;
        string? injectFrom = null;

        switch (toolName)
        {
            case CreateSketchTool.Name:
            {
                var p = new CreateSketchRequestParser().ParseParameters(argumentsJson, out var parseError);
                if (p == null)
                {
                    error = $"计划第 {index + 1} 步（create_sketch）参数无效：{parseError}";
                    return null;
                }
                return new ModelingStep
                {
                    Index = index,
                    ToolName = toolName,
                    Parameters = p,
                    Expectation = DescribeSketch(p),
                    Expected = GeometricExpectation.SketchProfile,
                };
            }
            case CreateExtrudeTool.Name:
            {
                var (args, inject) = ResolveDependentReference(argumentsJson, priorSteps, "sketch_ref", out var resolveError);
                if (resolveError != null)
                {
                    error = $"计划第 {index + 1} 步（create_extrude）：{resolveError}";
                    return null;
                }
                injectFrom = inject;
                var p = new CreateExtrudeRequestParser().ParseParameters(args, out var parseError);
                if (p == null)
                {
                    error = $"计划第 {index + 1} 步（create_extrude）参数无效：{parseError}";
                    return null;
                }
                // When the reference is injected from the sketch step, that
                // sketch step is the immediately prior sketch step (serial
                // chain — only earlier steps can be dependencies).
                var dependsOn = injectFrom != null
                    ? new[] { priorSteps.FindLastIndex(s => s.ToolName == CreateSketchTool.Name) }
                    : Array.Empty<int>();
                return new ModelingStep
                {
                    Index = index,
                    ToolName = toolName,
                    Parameters = p,
                    DependsOn = dependsOn,
                    InjectReferenceFromDependency = injectFrom,
                    Expectation = $"拉伸 {p.Distance:F3} 毫米（{(p.Direction == ExtrudeDirection.Reverse ? "法向反向" : "草图法向")}）",
                    Expected = GeometricExpectation.BodyBoundingBox,
                };
            }
            case CreateRevolveTool.Name:
            {
                var (args, inject) = ResolveDependentReference(argumentsJson, priorSteps, "sketch_ref", out var resolveError);
                if (resolveError != null)
                {
                    error = $"计划第 {index + 1} 步（create_revolve）：{resolveError}";
                    return null;
                }
                injectFrom = inject;
                var p = new CreateRevolveRequestParser().ParseParameters(args, out var parseError);
                if (p == null)
                {
                    error = $"计划第 {index + 1} 步（create_revolve）参数无效：{parseError}";
                    return null;
                }
                return new ModelingStep
                {
                    Index = index,
                    ToolName = toolName,
                    Parameters = p,
                    DependsOn = inject != null ? new[] { index - 1 } : Array.Empty<int>(),
                    InjectReferenceFromDependency = injectFrom,
                    Expectation = $"绕轴旋转 {p.AngleDeg:F1} 度",
                    Expected = GeometricExpectation.RevolutionEnvelope,
                };
            }
            case CreateBlockTool.Name:
            case CreateCylinderTool.Name:
            case CreateConeTool.Name:
            case CreateSphereTool.Name:
            {
                var (parserName, parsed) = ParsePrimitive(toolName, argumentsJson, out var parseError);
                if (parsed == null)
                {
                    error = $"计划第 {index + 1} 步（{toolName}）参数无效：{parseError}";
                    return null;
                }
                return new ModelingStep
                {
                    Index = index,
                    ToolName = toolName,
                    Parameters = parsed,
                    Expectation = DescribePrimitive(parserName, parsed),
                    Expected = GeometricExpectation.BodyBoundingBox,
                };
            }
            case BooleanOperationTool.Name:
            {
                var p = new BooleanOperationRequestParser().ParseParameters(argumentsJson, out var parseError);
                if (p == null)
                {
                    error = $"计划第 {index + 1} 步（boolean_operation）参数无效：{parseError}";
                    return null;
                }
                // Boolean within a plan requires BOTH refs to already exist
                // (validated at execution time); no injection.
                return new ModelingStep
                {
                    Index = index,
                    ToolName = toolName,
                    Parameters = p,
                    Expectation = $"布尔 {p.Operation}",
                    Expected = GeometricExpectation.BodyBoundingBox,
                };
            }
            default:
                error = $"计划第 {index + 1} 步使用了未注册工具：{toolName}。已拒绝。";
                return null;
        }
    }

    /// <summary>
    /// Marker recorded in a dependent step's parameters when its reference
    /// field holds a placeholder that will be replaced by the dependency's
    /// real result at execution time.
    /// </summary>
    internal const string PlaceholderMarker = "__INJECT_FROM_DEPENDENCY__";

    private (string Args, string? InjectFrom) ResolveDependentReference(
        string argumentsJson, List<ModelingStep> priorSteps,
        string referenceField, out string? error)
    {
        error = null;
        string? injectFrom = null;
        string args = argumentsJson;

        try
        {
            using var doc = JsonDocument.Parse(argumentsJson);
            var root = doc.RootElement.Clone();
            if (root.ValueKind == JsonValueKind.Object &&
                root.TryGetProperty(referenceField, out var refElement) &&
                refElement.ValueKind == JsonValueKind.String)
            {
                var value = refElement.GetString();
                if (!string.IsNullOrWhiteSpace(value))
                {
                    var prior = priorSteps.LastOrDefault(s => s.ToolName == CreateSketchTool.Name);
                    if (prior != null)
                    {
                        // The dependent step consumes the sketch step's output:
                        // bind by dependency, replacing whatever name the model
                        // guessed (it cannot know the real JournalIdentifier
                        // before the sketch exists).
                        injectFrom = referenceField;
                        var rewritten = new Dictionary<string, object?>();
                        foreach (var prop in root.EnumerateObject())
                        {
                            rewritten[prop.Name] = prop.Name == referenceField
                                ? PlaceholderMarker
                                : JsonSerializer.Deserialize<object>(prop.Value.GetRawText());
                        }
                        args = JsonSerializer.Serialize(rewritten);
                    }
                }
            }
        }
        catch (JsonException)
        {
            // The typed parser will report the malformed JSON properly.
        }
        return (args, injectFrom);
    }

    private static (string Name, object? Params) ParsePrimitive(
        string toolName, string argumentsJson, out string? error)
    {
        error = null;
        switch (toolName)
        {
            case CreateBlockTool.Name:
                return (toolName, new CreateBlockRequestParser().ParseParameters(argumentsJson, out error));
            case CreateCylinderTool.Name:
                return (toolName, new CreateCylinderRequestParser().ParseParameters(argumentsJson, out error));
            case CreateConeTool.Name:
                return (toolName, new CreateConeRequestParser().ParseParameters(argumentsJson, out error));
            default:
                return (toolName, new CreateSphereRequestParser().ParseParameters(argumentsJson, out error));
        }
    }

    private static string DescribeSketch(CreateSketchParameters p)
    {
        string plane = p.PlaneMode switch
        {
            SketchPlaneMode.SelectedPlane => "选中平面",
            SketchPlaneMode.WcsXY => "XY 平面",
            SketchPlaneMode.WcsXZ => "XZ 平面",
            _ => "YZ 平面",
        };
        string profile = p.ProfileType == SketchProfileType.Rectangle
            ? $"矩形 {p.RectangleWidth:F3}×{p.RectangleHeight:F3}"
            : $"圆 d{p.CircleDiameter:F3}";
        return $"{plane} {profile}";
    }

    private static string DescribePrimitive(string name, object? p) => name switch
    {
        CreateBlockTool.Name when p is CreateBlockParameters b =>
            $"长方体 {b.Length:F3}×{b.Width:F3}×{b.Height:F3}",
        CreateCylinderTool.Name when p is CreateCylinderParameters c =>
            $"圆柱 d{c.Diameter:F3}×h{c.Height:F3}",
        CreateConeTool.Name when p is CreateConeParameters k =>
            k.TopDiameter > 0
                ? $"圆台 D{k.BaseDiameter:F3}/d{k.TopDiameter:F3}×h{k.Height:F3}"
                : $"圆锥 d{k.BaseDiameter:F3}×h{k.Height:F3}",
        CreateSphereTool.Name when p is CreateSphereParameters s =>
            $"球 d{s.Diameter:F3}",
        _ => "基本体",
    };
}
