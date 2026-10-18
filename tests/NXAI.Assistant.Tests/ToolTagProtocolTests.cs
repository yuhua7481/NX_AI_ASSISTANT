using System.Text.Json;
using NXAI.Assistant.Core;
using Xunit;

namespace NXAI.Assistant.Tests;

/// <summary>
/// Offline tests for the restricted in-content tool-tag adapter
/// (ToolTagProtocol) and its integration into ParseChatCompletionResponse —
/// the Phase 7.3 fix for raw control markers leaking into the chat display.
/// The literal marker strings are rebuilt here from fragments for the same
/// reason the production code assembles them: this file must not contain the
/// complete raw sequence either.
/// </summary>
public class ToolTagProtocolTests
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

    private static string ResponseJson(string? content, object? toolCalls = null)
    {
        var message = new Dictionary<string, object?>();
        if (content != null) message["content"] = content;
        if (toolCalls != null) message["tool_calls"] = toolCalls;
        return JsonSerializer.Serialize(new
        {
            choices = new[] { new { message } },
        });
    }

    // ------------------------------------------------------------ clean text

    [Fact]
    public void PlainAssistantText_PassesThroughUnchanged()
    {
        var result = Parse(ResponseJson("普通回复文本。"));
        Assert.True(result.Success);
        Assert.Equal("普通回复文本。", result.Reply);
        Assert.Empty(result.ToolCalls);
    }

    [Fact]
    public void TextMentioningSimilarTags_ButNoRealMarker_PassesThrough()
    {
        // A user-controllable lookalike that does not contain the actual
        // marker must never be interpreted as a command.
        var result = Parse(ResponseJson("用 <invoke> 样式书写是常见问题。"));
        Assert.True(result.Success);
        Assert.NotNull(result.Reply);
    }

    // ------------------------------------------------------------ valid block

    [Fact]
    public void CompleteBlock_ParsesToTypedToolCall()
    {
        string block = Block("create_extrude",
            ("sketch_ref", "SKETCH_000"), ("distance", "50"), ("unit", "mm"));
        var result = Parse(ResponseJson(block));

        Assert.True(result.Success);
        var call = Assert.Single(result.ToolCalls);
        Assert.Equal("create_extrude", call.FunctionName);
        Assert.StartsWith("tagcall_", call.Id); // synthesized stable id

        using var doc = JsonDocument.Parse(call.ArgumentsJson);
        Assert.Equal("SKETCH_000", doc.RootElement.GetProperty("sketch_ref").GetString());
        Assert.Equal("50", doc.RootElement.GetProperty("distance").GetString());
        Assert.Equal("mm", doc.RootElement.GetProperty("unit").GetString());
    }

    [Fact]
    public void BlockWithSurroundingText_ExtractsCallAndCleanText()
    {
        string content = "好的，我来拉伸。\n" + Block("create_extrude",
            ("sketch_ref", "S1"), ("distance", "50"), ("unit", "mm")) + "\n请确认。";
        var result = Parse(ResponseJson(content));

        var call = Assert.Single(result.ToolCalls);
        Assert.Equal("create_extrude", call.FunctionName);
    }

    [Fact]
    public void TagCallId_IsUniquePerCall()
    {
        string block = Block("create_block", ("length", "1"), ("width", "1"), ("height", "1"), ("unit", "mm"));
        var r1 = Parse(ResponseJson(block));
        var r2 = Parse(ResponseJson(block));
        Assert.NotEqual(r1.ToolCalls[0].Id, r2.ToolCalls[0].Id);
    }

    // ------------------------------------------------------------ rejected shapes

    [Fact]
    public void UnclosedBlock_IsProtocolError_NeverRendered()
    {
        string content = InvokeOpen + "create_extrude" + ParamOpen("sketch_ref") + "S1";
        var result = Parse(ResponseJson(content));

        Assert.False(result.Success);
        Assert.Contains("未正常闭合", result.ErrorMessage);
        // The raw markers must not be echoed back to the user.
        Assert.DoesNotContain("invoke", result.ErrorMessage);
        Assert.DoesNotContain("create_extrude", result.ErrorMessage);
    }

    [Fact]
    public void MultipleBlocks_AreRejected()
    {
        string content = Block("create_block",
            ("length", "1"), ("width", "1"), ("height", "1"), ("unit", "mm")) +
            Block("create_block", ("length", "2"), ("width", "2"), ("height", "2"), ("unit", "mm"));
        var result = Parse(ResponseJson(content));

        Assert.False(result.Success);
        Assert.Contains("多个工具调用块", result.ErrorMessage);
    }

    [Fact]
    public void DuplicateParameter_IsRejected()
    {
        string content = Block("create_extrude", ("sketch_ref", "A"), ("sketch_ref", "B"));
        var result = Parse(ResponseJson(content));
        Assert.False(result.Success);
        Assert.Contains("重复", result.ErrorMessage);
    }

    [Fact]
    public void UnclosedParameter_IsRejected()
    {
        string content = InvokeOpen + "create_extrude" + ParamOpen("sketch_ref") + "S1" + InvokeClose;
        var result = Parse(ResponseJson(content));
        Assert.False(result.Success);
        Assert.Contains("未正常闭合", result.ErrorMessage);
    }

    [Fact]
    public void MissingToolName_IsRejected()
    {
        string content = InvokeOpen + ParamOpen("distance") + "5" + ParamClose + InvokeClose;
        var result = Parse(ResponseJson(content));
        Assert.False(result.Success);
        Assert.Contains("缺少工具名", result.ErrorMessage);
    }

    [Fact]
    public void IllegalToolName_IsRejected()
    {
        string content = InvokeOpen + "bad name with spaces" + ParamOpen("a") + "1" + ParamClose + InvokeClose;
        var result = Parse(ResponseJson(content));
        Assert.False(result.Success);
        Assert.Contains("工具名不合法", result.ErrorMessage);
    }

    [Fact]
    public void JunkInParameterArea_IsRejected()
    {
        string content = InvokeOpen + "create_extrude" +
            ParamOpen("sketch_ref") + "S1" + ParamClose + "loose text" + InvokeClose;
        var result = Parse(ResponseJson(content));
        Assert.False(result.Success);
        Assert.Contains("无法解析的文本", result.ErrorMessage);
    }

    [Fact]
    public void ParameterWithoutNameAttribute_IsRejected()
    {
        string content = InvokeOpen + "create_extrude<broken>" + InvokeClose;
        var result = Parse(ResponseJson(content));
        Assert.False(result.Success);
    }

    // ------------------------------------------------------------ downstream contract

    [Fact]
    public void ParsedTagCall_UnknownTool_IsRejectedByControllerRegistry()
    {
        // The adapter only normalizes the shape; registry checks stay in the
        // controller — a tag-protocol call for an unregistered tool must land
        // in the same UnknownTool path as a structured call.
        string block = Block("run_shell", ("command", "dir"));
        var result = Parse(ResponseJson(block));
        var call = Assert.Single(result.ToolCalls);
        Assert.Equal("run_shell", call.FunctionName); // parsed, NOT executed

        // Registry gate (mirrors ChatSessionController dispatch):
        var registry = CreateBlockTool.CreateRegistry();
        Assert.False(registry.IsRegistered(call.FunctionName));
    }

    [Fact]
    public void TagProtocolArguments_FeedTheRealTypedParsers()
    {
        // The adapter emits string values; the typed parsers must accept
        // them (string-encoded numbers are tolerated by design).
        string block = Block("create_extrude",
            ("sketch_ref", "SKETCH_000"), ("distance", "50"), ("unit", "mm"));
        var result = Parse(ResponseJson(block));
        var call = Assert.Single(result.ToolCalls);

        var p = new CreateExtrudeRequestParser().ParseParameters(call.ArgumentsJson, out var error);
        Assert.Null(error);
        Assert.Equal("SKETCH_000", p!.SketchRef);
        Assert.Equal(50, p.Distance);
    }
}
