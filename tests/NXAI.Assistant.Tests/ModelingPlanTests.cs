using NXAI.Assistant.Core;
using Xunit;

namespace NXAI.Assistant.Tests;

/// <summary>
/// Phase 8 offline tests: structured modeling plans (A), the serial plan
/// coordinator (B), and the fixed natural-language evaluation set (D —
/// deterministic: fixed model responses → asserted plans; live model
/// evaluation is only a supplement). Fakes throughout — no NX, no network.
/// </summary>
public class ModelingPlanTests
{
    private static ParsedToolCall Call(string name, string args) => new($"call_{name}", name, args);

    private static ModelingPlan? Plan(string intent, params ParsedToolCall[] calls)
    {
        var factory = new ModelingPlanFactory();
        return factory.Create(intent, calls, out _);
    }

    private static ModelingPlan? Plan(string intent, ParsedToolCall[] calls, out string? error)
    {
        var factory = new ModelingPlanFactory();
        return factory.Create(intent, calls, out error);
    }

    // ------------------------------------------------------------ scenario A: sketch → extrude

    [Fact]
    public void ScenarioA_SketchThenExtrude_TwoStepsWithDependencyAndInjection()
    {
        var plan = Plan("创建直径30圆形草图并拉伸50",
            Call("create_sketch", """{"plane_mode":"wcs_xy","profile_type":"circle","diameter":30,"unit":"mm"}"""),
            Call("create_extrude", """{"sketch_ref":"SKETCH_000","distance":50,"unit":"mm"}"""));

        Assert.NotNull(plan);
        Assert.Equal(2, plan.Steps.Count);
        Assert.Equal("create_sketch", plan.Steps[0].ToolName);
        Assert.Equal("create_extrude", plan.Steps[1].ToolName);
        // The extrude binds its reference to the sketch step's REAL result.
        Assert.Equal("sketch_ref", plan.Steps[1].InjectReferenceFromDependency);
        Assert.Equal(new[] { 0 }, plan.Steps[1].DependsOn);
        var ep = Assert.IsType<CreateExtrudeParameters>(plan.Steps[1].Parameters);
        Assert.Equal(ModelingPlanFactory.PlaceholderMarker, ep.SketchRef); // placeholder, replaced at run
        Assert.Equal(50, ep.Distance);
        Assert.Equal(GeometricExpectation.SketchProfile, plan.Steps[0].Expected);
        Assert.Equal(GeometricExpectation.BodyBoundingBox, plan.Steps[1].Expected);
    }

    // ------------------------------------------------------------ scenario B: XZ circle → revolve

    [Fact]
    public void ScenarioB_XZCircleAtU45_RevolveZ360_PlanMatchesIntent()
    {
        // Live scenario: XZ plane, U=45 (→ world (45,0,0)), d10, revolve
        // around absolute Z 360°. Expected result: torus with bbox
        // (-50,-50,-5)–(50,50,5).
        var plan = Plan("XZ平面X45处d10圆绕Z轴旋转360",
            Call("create_sketch", """{"plane_mode":"wcs_xz","profile_type":"circle","diameter":10,"center_u":45,"center_v":0,"unit":"mm"}"""),
            Call("create_revolve", """{"sketch_ref":"SKETCH_000","axis_mode":"wcs_z","angle":360}"""));

        Assert.NotNull(plan);
        var sp = Assert.IsType<CreateSketchParameters>(plan.Steps[0].Parameters);
        Assert.Equal(SketchPlaneMode.WcsXZ, sp.PlaneMode);
        Assert.Equal(SketchProfileType.Circle, sp.ProfileType);
        Assert.Equal(10, sp.CircleDiameter);
        Assert.Equal(45, sp.CenterU);   // local U → world X on the XZ plane
        Assert.Equal(0, sp.CenterV);

        var rp = Assert.IsType<CreateRevolveParameters>(plan.Steps[1].Parameters);
        Assert.Equal(RevolveAxisMode.WcsZ, rp.AxisMode);
        Assert.Equal(360, rp.AngleDeg);
        Assert.Equal("sketch_ref", plan.Steps[1].InjectReferenceFromDependency);
        Assert.Equal(GeometricExpectation.RevolutionEnvelope, plan.Steps[1].Expected);
    }

    /// <summary>Analytic torus envelope for the scenario-B geometry: circle
    /// center (45,0,0), radius 5, revolved around the Z axis → world bbox
    /// (-50,-50,-5)–(50,50,5). Pinned here as the expected value for the NX
    /// self-check and live acceptance.</summary>
    public static (double MinX, double MinY, double MinZ, double MaxX, double MaxY, double MaxZ)
        ScenarioB_ExpectedEnvelope() => (-50, -50, -5, 50, 50, 5);

    [Fact]
    public void ScenarioB_ExpectedEnvelope_IsTheAnalyticTorusBox()
    {
        var (minX, minY, minZ, maxX, maxY, maxZ) = ScenarioB_ExpectedEnvelope();
        // Outer radius = 45+5 = 50; inner radius = 45-5 = 40 (not visible in
        // the box); Z half-extent = 5.
        Assert.Equal(-50, minX);
        Assert.Equal(-50, minY);
        Assert.Equal(-5, minZ);
        Assert.Equal(50, maxX);
        Assert.Equal(50, maxY);
        Assert.Equal(5, maxZ);
    }

    // ------------------------------------------------------------ rejection

    [Fact]
    public void UnknownToolInPlan_IsRejected()
    {
        var plan = Plan("未知工具",
            new[] { Call("run_shell", """{"command":"dir"}""") }, out var error);
        Assert.Null(plan);
        Assert.Contains("未注册工具", error);
    }

    [Fact]
    public void InvalidParametersInPlan_AreRejected()
    {
        var plan = Plan("非法距离",
            new[] { Call("create_sketch", """{"plane_mode":"wcs_xy","profile_type":"circle","diameter":-5,"unit":"mm"}""") },
            out var error);
        Assert.Null(plan);
        Assert.Contains("参数无效", error);
    }

    [Fact]
    public void TooManySteps_AreRejected()
    {
        var calls = Enumerable.Range(0, 3)
            .Select(i => Call("create_sketch",
                $$"""{"plane_mode":"wcs_xy","profile_type":"circle","diameter":{{i + 5}},"unit":"mm"}"""))
            .ToArray();
        var plan = Plan("三步", calls, out var error);
        Assert.Null(plan);
        Assert.Contains("上限", error);
    }

    [Fact]
    public void EmptyPlan_IsRejected()
    {
        var plan = Plan("空", Array.Empty<ParsedToolCall>(), out var error);
        Assert.Null(plan);
        Assert.Contains("为空", error);
    }

    [Fact]
    public void AliasName_InPlan_IsCanonicalized()
    {
        // The model's "extrude" alias must reach the same typed step. With no
        // prior sketch step in the plan there is nothing to inject from — the
        // model-supplied reference stays and is validated at execution time.
        var plan = Plan("别名",
            Call("extrude", """{"sketch":"SKETCH_000","distance":50,"unit":"mm"}"""));
        Assert.NotNull(plan);
        Assert.Equal("create_extrude", plan.Steps[0].ToolName);
        var p = Assert.IsType<CreateExtrudeParameters>(plan.Steps[0].Parameters);
        Assert.Equal("SKETCH_000", p.SketchRef);
        Assert.Equal(50, p.Distance);
        Assert.Null(plan.Steps[0].InjectReferenceFromDependency);
    }

    // ------------------------------------------------------------ coordinator

    private sealed class FakeHost : IPlanExecutionHost
    {
        public List<(string Tool, string? RefOverride)> Executed { get; } = new();
        public int ConfirmStepCalls { get; private set; }
        public int ConfirmPlanCalls { get; private set; }
        public bool Approve { get; set; } = true;
        public int FailAtIndex { get; set; } = -1;
        public string SketchRef = "SKETCH_000";

        public CreateBlockExecutionResult ExecuteStep(ModelingStep step, string? referenceOverride)
        {
            Executed.Add((step.ToolName, referenceOverride));
            if (Executed.Count - 1 == FailAtIndex)
                return CreateBlockExecutionResult.Failure(ToolExecutionStatus.ExecutionFailed, "模拟失败");
            if (step.ToolName == "create_sketch")
            {
                return CreateBlockExecutionResult.Success(30, 30, 0, "草图已创建。",
                    "part.prt", $$"""{"sketchRef":"{{SketchRef}}"}""");
            }
            return CreateBlockExecutionResult.Success(50, 50, 50, "实体已创建。",
                "part.prt", """{"newBodyRef":"BODY_1"}""");
        }

        public Task<bool> ConfirmStepAsync(ModelingStep step, ModelingPlan plan, CancellationToken ct)
        {
            ConfirmStepCalls++;
            return Task.FromResult(Approve);
        }

        public Task<bool> ConfirmPlanAsync(ModelingPlan plan, CancellationToken ct)
        {
            ConfirmPlanCalls++;
            return Task.FromResult(Approve);
        }
    }

    private static ModelingPlan ValidTwoStepPlan() =>
        Plan("两步",
            Call("create_sketch", """{"plane_mode":"wcs_xy","profile_type":"circle","diameter":30,"unit":"mm"}"""),
            Call("create_extrude", """{"sketch_ref":"SKETCH_000","distance":50,"unit":"mm"}"""))!;

    [Fact]
    public async Task ConfirmMode_AsksPerStep_InjectsRealReference()
    {
        var host = new FakeHost();
        var coordinator = new PlanExecutionCoordinator(host, new ExecutionModeManager()); // Confirm default

        var result = await coordinator.ExecuteAsync(ValidTwoStepPlan(), CancellationToken.None);

        Assert.True(result.AllSucceeded);
        Assert.Equal(2, host.ConfirmStepCalls);   // one card per step
        Assert.Equal(0, host.ConfirmPlanCalls);
        // Step 2 received the REAL sketch reference, not the placeholder.
        Assert.Equal(("create_extrude", "SKETCH_000"), host.Executed[1]);
        Assert.Contains("全部完成", result.Describe());
    }

    [Fact]
    public async Task AutonomousMode_OnePlanNotice_ZeroStepCards()
    {
        var modeManager = new ExecutionModeManager();
        modeManager.SetMode(ExecutionMode.Autonomous);
        var host = new FakeHost();
        var coordinator = new PlanExecutionCoordinator(host, modeManager);

        var result = await coordinator.ExecuteAsync(ValidTwoStepPlan(), CancellationToken.None);

        Assert.True(result.AllSucceeded);
        Assert.Equal(1, host.ConfirmPlanCalls);   // one plan-level notice
        Assert.Equal(0, host.ConfirmStepCalls);   // no per-step cards
        Assert.Equal(2, host.Executed.Count);
    }

    [Fact]
    public async Task UserCancelsFirstStep_NothingExecutes()
    {
        var host = new FakeHost { Approve = false };
        var coordinator = new PlanExecutionCoordinator(host, new ExecutionModeManager());

        var result = await coordinator.ExecuteAsync(ValidTwoStepPlan(), CancellationToken.None);

        Assert.Empty(host.Executed);
        Assert.False(result.AllSucceeded);
        Assert.Equal(0, result.StepResults[0].Index); // failed at step 1
        Assert.Contains("取消", result.Describe());
    }

    [Fact]
    public async Task SecondStepFails_PartialTruthReported_SketchWasCreated()
    {
        var host = new FakeHost { FailAtIndex = 1 };
        var coordinator = new PlanExecutionCoordinator(host, new ExecutionModeManager());

        var result = await coordinator.ExecuteAsync(ValidTwoStepPlan(), CancellationToken.None);

        Assert.False(result.AllSucceeded);
        // Both steps were dispatched; the second returned a real failure.
        Assert.Equal(2, host.Executed.Count);
        Assert.Equal(PlanStepStatus.Succeeded, result.StepResults[0].Status);
        Assert.Equal(PlanStepStatus.Failed, result.StepResults[1].Status);
        // Honest summary: sketch done, extrude failed — no fake success.
        Assert.Contains("已创建", result.Describe());
        Assert.Contains("失败", result.Describe());
    }

    [Fact]
    public async Task FirstStepFails_SecondStepSkipped()
    {
        var host = new FakeHost { FailAtIndex = 0 };
        var coordinator = new PlanExecutionCoordinator(host, new ExecutionModeManager());

        var result = await coordinator.ExecuteAsync(ValidTwoStepPlan(), CancellationToken.None);

        Assert.False(result.AllSucceeded);
        Assert.Single(host.Executed);
        Assert.Equal(PlanStepStatus.Failed, result.StepResults[0].Status);
        Assert.Equal(PlanStepStatus.Skipped, result.StepResults[1].Status);
        Assert.Contains("未执行", result.Describe());
    }

    [Fact]
    public void ReferenceExtraction_PicksOnlyManagedRefKeys()
    {
        var refs = PlanExecutionCoordinator.ExtractReferences(
            """{"sketchRef":"SKETCH_000","newBodyRef":"BODY_1","evil":"x","n":5}""");
        Assert.Equal(2, refs.Count);
        Assert.Equal("SKETCH_000", refs["sketchRef"]);
        Assert.Equal("BODY_1", refs["newBodyRef"]);
    }
}
