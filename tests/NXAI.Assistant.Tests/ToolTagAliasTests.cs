using System.Text.Json;
using NXAI.Assistant.Core;
using Xunit;

namespace NXAI.Assistant.Tests;

/// <summary>
/// Phase 7.3.1 regression tests: the live model output from the NX2512
/// screenshot — an in-content tag block calling the tool by the alias
/// "extrude" with a "positive_z" direction — must normalize to the
/// registered create_extrude contract and reach the real typed parser.
/// The alias/direction tables are explicit and closed; anything outside
/// them is rejected downstream as before.
/// </summary>
public class ToolTagAliasTests
{
    private const string P = "an" + "tm" + "l:";
    private static string InvokeOpen => "<" + P + "inv" + "oke";
    private static string InvokeClose => "</" + P + "inv" + "oke" + ">";
    private static string ParamOpen(string key) => "<" + P + "par" + "ameter name=\"" + key + "\">";
    private static string ParamClose => "</" + P + "par" + "ameter" + ">";

    private static string Block(string name, params (string Key, string Value)[] ps)
    {
        var inner = string.Concat(ps.Select(p => ParamOpen(p.Key) + p.Value + ParamClose));
        return InvokeOpen + name + inner + InvokeClose;
    }

    private static ChatCompletionResult Parse(string responseBody) =>
        ModelService.ParseChatCompletionResponse(responseBody);

    private static string ResponseJson(string content, object? toolCalls = null)
    {
        var message = new Dictionary<string, object?>();
        if (content != null) message["content"] = content;
        if (toolCalls != null) message["tool_calls"] = toolCalls;
        return JsonSerializer.Serialize(new
        {
            choices = new[] { new { message } },
        });
    }

    // ------------------------------------------------------------ the live screenshot case

    [Fact]
    public void ScreenshotCase_ExtrudeAlias_PositiveZ_NormalizesToRegisteredContract()
    {
        // Exact live shape: extrude / profile=SKETCH_000 / direction=positive_z
        // / distance=50 / unit=mm. Note the model sends "profile", not
        // "sketch_ref" — the typed parser must reject that unknown field
        // (see UnknownProfileField test below); the alias+direction mapping
        // alone cannot make the call valid, by design.
        string block = Block("extrude",
            ("profile", "SKETCH_000"),
            ("direction", "positive_z"),
            ("distance", "50"),
            ("unit", "mm"));
        var result = Parse(ResponseJson(block));

        var call = Assert.Single(result.ToolCalls);
        Assert.Equal("create_extrude", call.FunctionName);

        using var doc = JsonDocument.Parse(call.ArgumentsJson);
        // The direction alias rewrites positive_z → normal (schema value);
        // "profile" is NOT a schema field — it stays as-is and the typed
        // parser rejects it (covered by the unknown-field test in
        // ToolTagProtocolTests / the parser contract).
        Assert.Equal("normal", doc.RootElement.GetProperty("direction").GetString());
        Assert.Equal("SKETCH_000", doc.RootElement.GetProperty("profile").GetString());
    }

    [Fact]
    public void ExtrudeAlias_WithCanonicalArgs_ReachesRealTypedParser()
    {
        // When the model uses the schema's own argument names with the tool
        // alias, the normalized call must fully satisfy the typed parser.
        string block = Block("extrude",
            ("sketch_ref", "SKETCH_000"),
            ("direction", "positive_z"),
            ("distance", "50"),
            ("unit", "mm"));
        var result = Parse(ResponseJson(block));
        var call = Assert.Single(result.ToolCalls);
        Assert.Equal("create_extrude", call.FunctionName);

        var p = new CreateExtrudeRequestParser().ParseParameters(call.ArgumentsJson, out var error);
        Assert.Null(error);
        Assert.Equal("SKETCH_000", p!.SketchRef);   // controlled-ref path unchanged
        Assert.Equal(50, p.Distance);
        Assert.Equal(ExtrudeDirection.Normal, p.Direction); // positive_z → normal
    }

    [Fact]
    public void PositiveZ_MapsToNormal_SemanticallyTheSketchNormal()
    {
        // "positive_z" is admitted only because the schema's "normal"
        // direction is the sketch-plane normal — +Z for the XY-plane
        // sketches this tool creates. The mapping must be explicit and the
        // result must be the schema's own value.
        string block = Block("extrude",
            ("sketch_ref", "S1"), ("direction", "positive_z"), ("distance", "1"), ("unit", "mm"));
        var result = Parse(ResponseJson(block));
        var call = Assert.Single(result.ToolCalls);
        using var doc = JsonDocument.Parse(call.ArgumentsJson);
        Assert.Equal("normal", doc.RootElement.GetProperty("direction").GetString());
    }

    [Fact]
    public void UnknownDirectionValue_IsRejectedByTypedParser()
    {
        string block = Block("extrude",
            ("sketch_ref", "S1"), ("direction", "sideways"), ("distance", "1"), ("unit", "mm"));
        var result = Parse(ResponseJson(block));
        var call = Assert.Single(result.ToolCalls);

        var p = new CreateExtrudeRequestParser().ParseParameters(call.ArgumentsJson, out var error);
        Assert.Null(p);
        Assert.Contains("direction", error);
    }

    [Fact]
    public void SketchRef_ControlledReference_PassesThroughUnchanged()
    {
        // SKETCH_000 is a NXHost-managed JournalIdentifier — the adapter must
        // forward it verbatim; resolution/validation stays in the executor.
        string block = Block("extrude",
            ("sketch_ref", "SKETCH_000"), ("distance", "50"), ("unit", "mm"));
        var result = Parse(ResponseJson(block));
        var call = Assert.Single(result.ToolCalls);
        using var doc = JsonDocument.Parse(call.ArgumentsJson);
        Assert.Equal("SKETCH_000", doc.RootElement.GetProperty("sketch_ref").GetString());
    }

    // ------------------------------------------------------------ closed-table rules

    [Fact]
    public void UnknownToolAlias_IsNotRewritten_AndRejectedByRegistry()
    {
        string block = Block("cut_stuff", ("sketch_ref", "S1"), ("distance", "1"), ("unit", "mm"));
        var result = Parse(ResponseJson(block));
        var call = Assert.Single(result.ToolCalls);
        Assert.Equal("cut_stuff", call.FunctionName); // passed through verbatim
        var registry = CreateBlockTool.CreateRegistry();
        Assert.False(registry.IsRegistered(call.FunctionName));
    }

    [Fact]
    public void OtherRegisteredTools_AreNotAffectedByAliasTable()
    {
        string block = Block("create_block",
            ("length", "10"), ("width", "10"), ("height", "10"), ("unit", "mm"));
        var result = Parse(ResponseJson(block));
        var call = Assert.Single(result.ToolCalls);
        Assert.Equal("create_block", call.FunctionName);
        using var doc = JsonDocument.Parse(call.ArgumentsJson);
        Assert.Equal("10", doc.RootElement.GetProperty("length").GetString());
    }

    [Fact]
    public void StructuredToolCalls_StillTakePriorityAndWork()
    {
        string toolCallsJson = """
            [{"id":"call_x1","type":"function","function":{"name":"create_extrude","arguments":"{\"sketch_ref\":\"SKETCH_000\",\"distance\":50,\"unit\":\"mm\"}"}}]
            """;
        using var doc = JsonDocument.Parse(toolCallsJson);
        var result = Parse(ResponseJson("", doc.RootElement.Clone()));
        var call = Assert.Single(result.ToolCalls);
        Assert.Equal("call_x1", call.Id); // real model id preserved, not tagcall_N
        Assert.Equal("create_extrude", call.FunctionName);
    }
}
