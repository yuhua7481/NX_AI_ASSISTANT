using NXAI.Assistant.Core;
using NXAI.Assistant.UI;
using Xunit;

namespace NXAI.Assistant.Tests;

/// <summary>
/// Phase 9.4 create_fillet / create_chamfer contract tests (offline):
/// parsing, registry, allowlist, schema validity, and the
/// fillet≠chamfer exclusivity the live session demands. NX builder
/// behavior (EdgeBlendBuilder / ChamferBuilder) is a separate live
/// acceptance item — these tests pin the CONTRACT layer only.
/// </summary>
public class EdgeFinishToolContractTests
{
    private static CreateFilletParameters? ParseFillet(string json, out string? error) =>
        new CreateFilletRequestParser().ParseParameters(json, out error);

    private static CreateChamferParameters? ParseChamfer(string json, out string? error) =>
        new CreateChamferRequestParser().ParseParameters(json, out error);

    // ---------- happy paths ----------

    [Fact]
    public void Fillet_R2mm_Parses()
    {
        var p = ParseFillet("""{"radius":2,"unit":"mm"}""", out var error);
        Assert.Null(error);
        Assert.Equal(2, p!.RadiusMm);
    }

    [Fact]
    public void Chamfer_C2mm_Parses()
    {
        var p = ParseChamfer("""{"distance":2,"unit":"mm"}""", out var error);
        Assert.Null(error);
        Assert.Equal(2, p!.DistanceMm);
    }

    [Fact]
    public void Fillet_UnitConverted()
    {
        var p = ParseFillet("""{"radius":0.2,"unit":"cm"}""", out var error);
        Assert.Null(error);
        Assert.Equal(2, p!.RadiusMm);
    }

    [Fact]
    public void Chamfer_UnitConverted()
    {
        var p = ParseChamfer("""{"distance":0.07874015748031496,"unit":"inch"}""", out var error);
        Assert.Null(error);
        Assert.Equal(2, p!.DistanceMm, 6);
    }

    // ---------- rejections ----------

    [Theory]
    [InlineData("""{"radius":0,"unit":"mm"}""")]          // zero radius
    [InlineData("""{"radius":-2,"unit":"mm"}""")]         // negative
    [InlineData("""{"unit":"mm"}""")]                      // missing radius
    [InlineData("""{"radius":2}""")]                       // missing unit
    [InlineData("""{"radius":1001,"unit":"mm"}""")]       // above sanity bound
    [InlineData("""{"radius":2,"unit":"mm","edges":["E1"]}""")] // unknown field
    [InlineData("not json")]
    public void Fillet_InvalidInputs_Rejected(string args)
    {
        var p = ParseFillet(args, out var error);
        Assert.Null(p);
        Assert.NotNull(error);
    }

    [Theory]
    [InlineData("""{"distance":0,"unit":"mm"}""")]
    [InlineData("""{"distance":-2,"unit":"mm"}""")]
    [InlineData("""{"unit":"mm"}""")]
    [InlineData("""{"distance":2}""")]
    [InlineData("""{"distance":1001,"unit":"mm"}""")]
    [InlineData("""{"distance":2,"unit":"mm","angle":45}""")] // offset+angle NOT offered
    [InlineData("not json")]
    public void Chamfer_InvalidInputs_Rejected(string args)
    {
        var p = ParseChamfer(args, out var error);
        Assert.Null(p);
        Assert.NotNull(error);
    }

    // ---------- registry + allowlist ----------

    [Fact]
    public void BothTools_Registered_InToolRegistry()
    {
        var registry = CreateBlockTool.CreateRegistry();
        Assert.Equal(18, registry.All.Count);
        Assert.True(registry.IsRegistered(EdgeFinishToolContract.FilletName));
        Assert.True(registry.IsRegistered(EdgeFinishToolContract.ChamferName));
    }

    [Fact]
    public void BothTools_OnAutonomousAllowlist()
    {
        Assert.Equal(18, AutonomousToolAllowlist.Tools.Count);
        Assert.Contains(EdgeFinishToolContract.FilletName, AutonomousToolAllowlist.Tools);
        Assert.Contains(EdgeFinishToolContract.ChamferName, AutonomousToolAllowlist.Tools);
    }

    // ---------- schema validity (ModelService parse path) ----------

    [Fact]
    public void BothSchemas_ParseWithModelServiceParser()
    {
        using var f = System.Text.Json.JsonDocument.Parse(EdgeFinishToolContract.FilletSchema);
        using var c = System.Text.Json.JsonDocument.Parse(EdgeFinishToolContract.ChamferSchema);
        Assert.Equal("object", f.RootElement.GetProperty("type").GetString());
        Assert.Equal("object", c.RootElement.GetProperty("type").GetString());
        // Fillet requires radius; chamfer requires distance — never swapped.
        var fReq = f.RootElement.GetProperty("required").EnumerateArray()
            .Select(e => e.GetString()!).ToArray();
        var cReq = c.RootElement.GetProperty("required").EnumerateArray()
            .Select(e => e.GetString()!).ToArray();
        Assert.Equal(new[] { "radius", "unit" }, fReq);
        Assert.Equal(new[] { "distance", "unit" }, cReq);
    }

    // ---------- exclusivity: fillet ≠ chamfer ----------

    [Fact]
    public void FilletTool_NeverClaimsChamfer()
    {
        Assert.DoesNotContain("create_chamfer", EdgeFinishToolContract.FilletDescription);
        Assert.Contains("圆角", EdgeFinishToolContract.FilletDescription);
        Assert.DoesNotContain("倒斜角", EdgeFinishToolContract.FilletDescription);
        // Phase 9.5: the fillet description must advertise the auto-target
        // path (empty selection + just-created box → 12 perimeter edges).
        Assert.Contains("周边", EdgeFinishToolContract.FilletDescription);
        Assert.Contains("12 条外棱边", EdgeFinishToolContract.FilletDescription);
    }

    [Fact]
    public void ChamferTool_NeverClaimsFillet_AndPinsSymmetricMode()
    {
        // Referencing the other tool's SHARED resolution rule is fine; what
        // matters is the chamfer never claims to CREATE a fillet (圆角).
        Assert.DoesNotContain("圆角", EdgeFinishToolContract.ChamferDescription);
        Assert.Contains("倒斜角", EdgeFinishToolContract.ChamferDescription);
        // The C2 form is SymmetricOffsets — the description must pin it and
        // explicitly disclaim the asymmetric modes (no silent mode switch).
        Assert.Contains("SymmetricOffsets", EdgeFinishToolContract.ChamferDescription);
        Assert.Contains("对称", EdgeFinishToolContract.ChamferDescription);
        Assert.Contains("暂不支持", EdgeFinishToolContract.ChamferDescription);
    }

    // ---------- controller routing (dispatch gate membership) ----------

    [Fact]
    public async Task FilletTool_RoutesToExecutor()
    {
        var executor = new ChatSessionControllerToolTests.CountingExecutor();
        var log = new ChatLog();
        var sender = new ChatSessionControllerToolTests.ScriptedSender();
        var gate = new ChatSessionControllerToolTests.ConfirmationGate();
        var controller = new ChatSessionController(
            new ModelService(), log, () => ("https://api.test/v1", "key", "model"),
            sender.SendAsync, 20, executor, gate.Handler);
        gate.EnqueuePending(out var pending);
        sender.Enqueue(ChatSessionControllerToolTests.ScriptedSender.ToolCall(
            name: CreateFilletRequestParser.ToolName,
            args: """{"radius":2,"unit":"mm"}"""));

        controller.StartSend("给选中的边倒 R2 圆角");
        await ChatSessionControllerToolTestsPublicHelpers.UntilAsyncPublic(
            () => gate.Requests.Count > 0);
        pending.TrySetResult(new ToolConfirmationDecision(true, "D:\\part.prt"));
        await ChatSessionControllerToolTestsPublicHelpers.WaitForCompletedAsyncPublic(controller);

        Assert.Equal(1, executor.CallCount);
        Assert.Contains(CreateFilletRequestParser.ToolName, executor.Calls);
    }

    [Fact]
    public async Task ChamferTool_RoutesToExecutor()
    {
        var executor = new ChatSessionControllerToolTests.CountingExecutor();
        var log = new ChatLog();
        var sender = new ChatSessionControllerToolTests.ScriptedSender();
        var gate = new ChatSessionControllerToolTests.ConfirmationGate();
        var controller = new ChatSessionController(
            new ModelService(), log, () => ("https://api.test/v1", "key", "model"),
            sender.SendAsync, 20, executor, gate.Handler);
        gate.EnqueuePending(out var pending);
        sender.Enqueue(ChatSessionControllerToolTests.ScriptedSender.ToolCall(
            name: CreateChamferRequestParser.ToolName,
            args: """{"distance":2,"unit":"mm"}"""));

        controller.StartSend("给选中的边倒 C2 斜角");
        await ChatSessionControllerToolTestsPublicHelpers.UntilAsyncPublic(
            () => gate.Requests.Count > 0);
        pending.TrySetResult(new ToolConfirmationDecision(true, "D:\\part.prt"));
        await ChatSessionControllerToolTestsPublicHelpers.WaitForCompletedAsyncPublic(controller);

        Assert.Equal(1, executor.CallCount);
        Assert.Contains(CreateChamferRequestParser.ToolName, executor.Calls);
    }
}
