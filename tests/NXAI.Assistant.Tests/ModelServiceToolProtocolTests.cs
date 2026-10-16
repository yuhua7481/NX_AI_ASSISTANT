using System.Net;
using System.Text;
using NXAI.Assistant.Core;
using Xunit;

namespace NXAI.Assistant.Tests;

/// <summary>
/// Offline tests for the tool-calling protocol in ModelService: tools
/// declaration in the request body, tool_calls parsing, assistant/tool message
/// serialization, and rejection of malformed responses. Uses a fake
/// HttpMessageHandler — no real network.
/// </summary>
public class ModelServiceToolProtocolTests
{
    private const string Endpoint = "https://fake.test/v1/chat/completions";

    private sealed class FakeHandler : HttpMessageHandler
    {
        private readonly HttpResponseMessage _response;
        public string? LastRequestBody { get; private set; }

        public FakeHandler(HttpResponseMessage response) => _response = response;

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.Content != null)
            {
                LastRequestBody = request.Content.ReadAsStringAsync(CancellationToken.None).GetAwaiter().GetResult();
            }
            return Task.FromResult(_response);
        }
    }

    private static ModelService CreateService(FakeHandler handler)
    {
        var service = new ModelService(new HttpClient(handler) { BaseAddress = new Uri(Endpoint) });
        service.SetConfiguration("https://fake.test/v1", "test-key", "test-model");
        return service;
    }

    private static HttpResponseMessage JsonOk(string body) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(body, Encoding.UTF8, "application/json"),
    };

    private static readonly ToolDefinition[] OneTool =
    {
        new("create_block", "create a block", """{"type":"object","additionalProperties":false}"""),
    };

    private static readonly OutboundMessage UserMessage = new(OutboundMessage.RoleUser, "创建长方体");

    // ------------------------------------------------------------ plain text

    [Fact]
    public async Task PlainTextReply_StillWorks_NoRegression()
    {
        var handler = new FakeHandler(JsonOk(
            """{"choices":[{"message":{"role":"assistant","content":"你好。"}}]}"""));
        var service = CreateService(handler);

        var result = await service.GetChatCompletionWithToolsAsync(
            [UserMessage], OneTool, CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal("你好。", result.Reply);
        Assert.Empty(result.ToolCalls);
    }

    // --------------------------------------------------------- tool_calls

    [Fact]
    public async Task ToolCallResponse_ParsesIdNameArguments()
    {
        var handler = new FakeHandler(JsonOk(
            """{"choices":[{"message":{"role":"assistant","content":null,"tool_calls":[{"id":"call_1","type":"function","function":{"name":"create_block","arguments":"{\"length\":100,\"width\":60,\"height\":20,\"unit\":\"mm\"}"}}]}}]}"""));
        var service = CreateService(handler);

        var result = await service.GetChatCompletionWithToolsAsync(
            [UserMessage], OneTool, CancellationToken.None);

        Assert.True(result.Success);
        Assert.Null(result.Reply);
        var call = Assert.Single(result.ToolCalls);
        Assert.Equal("call_1", call.Id);
        Assert.Equal("create_block", call.FunctionName);
        Assert.Contains("length", call.ArgumentsJson);
    }

    [Fact]
    public async Task EmptyAssistantContent_WithToolCalls_IsValid()
    {
        var handler = new FakeHandler(JsonOk(
            """{"choices":[{"message":{"role":"assistant","content":"","tool_calls":[{"id":"call_x","type":"function","function":{"name":"create_block","arguments":"{}"}}]}}]}"""));
        var service = CreateService(handler);

        var result = await service.GetChatCompletionWithToolsAsync(
            [UserMessage], OneTool, CancellationToken.None);

        Assert.True(result.Success);
        Assert.Single(result.ToolCalls);
    }

    [Fact]
    public async Task EmptyContent_NoToolCalls_IsInvalidResponse()
    {
        var handler = new FakeHandler(JsonOk(
            """{"choices":[{"message":{"role":"assistant","content":""}}]}"""));
        var service = CreateService(handler);

        var result = await service.GetChatCompletionWithToolsAsync(
            [UserMessage], OneTool, CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal(ModelChatError.InvalidResponse, result.Error);
    }

    [Fact]
    public async Task ParallelToolCalls_Rejected()
    {
        var handler = new FakeHandler(JsonOk(
            """{"choices":[{"message":{"role":"assistant","content":null,"tool_calls":[{"id":"c1","type":"function","function":{"name":"create_block","arguments":"{}"}},{"id":"c2","type":"function","function":{"name":"create_block","arguments":"{}"}}]}}]}"""));
        var service = CreateService(handler);

        var result = await service.GetChatCompletionWithToolsAsync(
            [UserMessage], OneTool, CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal(ModelChatError.InvalidResponse, result.Error);
        Assert.Contains("最多", result.ErrorMessage);
    }

    [Fact]
    public async Task ToolCallMissingId_Rejected()
    {
        var handler = new FakeHandler(JsonOk(
            """{"choices":[{"message":{"role":"assistant","content":null,"tool_calls":[{"type":"function","function":{"name":"create_block","arguments":"{}"}}]}}]}"""));
        var service = CreateService(handler);

        var result = await service.GetChatCompletionWithToolsAsync(
            [UserMessage], OneTool, CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal(ModelChatError.InvalidResponse, result.Error);
    }

    [Fact]
    public async Task ToolCallMissingFunction_Rejected()
    {
        var handler = new FakeHandler(JsonOk(
            """{"choices":[{"message":{"role":"assistant","content":null,"tool_calls":[{"id":"c1","type":"function"}]}}]}"""));
        var service = CreateService(handler);

        var result = await service.GetChatCompletionWithToolsAsync(
            [UserMessage], OneTool, CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal(ModelChatError.InvalidResponse, result.Error);
    }

    [Fact]
    public async Task MalformedToolCallsShape_Rejected()
    {
        var handler = new FakeHandler(JsonOk(
            """{"choices":[{"message":{"role":"assistant","content":null,"tool_calls":"not-an-array"}}]}"""));
        var service = CreateService(handler);

        var result = await service.GetChatCompletionWithToolsAsync(
            [UserMessage], OneTool, CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal(ModelChatError.InvalidResponse, result.Error);
    }

    // ------------------------------------------------------- request shape

    [Fact]
    public async Task Request_IncludesToolsDeclaration()
    {
        var handler = new FakeHandler(JsonOk(
            """{"choices":[{"message":{"role":"assistant","content":"ok"}}]}"""));
        var service = CreateService(handler);

        await service.GetChatCompletionWithToolsAsync([UserMessage], OneTool, CancellationToken.None);

        Assert.NotNull(handler.LastRequestBody);
        using var doc = System.Text.Json.JsonDocument.Parse(handler.LastRequestBody!);
        var root = doc.RootElement;
        Assert.True(root.TryGetProperty("tools", out var tools));
        Assert.Equal(1, tools.GetArrayLength());
        Assert.Equal("function", tools[0].GetProperty("type").GetString());
        Assert.Equal("create_block", tools[0].GetProperty("function").GetProperty("name").GetString());
        Assert.Equal("test-model", root.GetProperty("model").GetString());
    }

    [Fact]
    public async Task Request_WithoutTools_OmitsDeclaration()
    {
        var handler = new FakeHandler(JsonOk(
            """{"choices":[{"message":{"role":"assistant","content":"ok"}}]}"""));
        var service = CreateService(handler);

        await service.GetChatCompletionWithToolsAsync([UserMessage], null, CancellationToken.None);

        using var doc = System.Text.Json.JsonDocument.Parse(handler.LastRequestBody!);
        Assert.False(doc.RootElement.TryGetProperty("tools", out _));
    }

    [Fact]
    public async Task Request_SerializesAssistantToolCallsAndToolResult()
    {
        var handler = new FakeHandler(JsonOk(
            """{"choices":[{"message":{"role":"assistant","content":"done"}}]}"""));
        var service = CreateService(handler);

        var messages = new List<OutboundMessage>
        {
            UserMessage,
            new(OutboundMessage.RoleAssistant, null,
                new[] { new OutboundToolCall("call_9", "create_block", "{\"length\":1}") }),
            new OutboundMessage(OutboundMessage.RoleTool,
                """{"status":"success"}""", ToolCallId: "call_9"),
        };
        await service.GetChatCompletionWithToolsAsync(messages, OneTool, CancellationToken.None);

        using var doc = System.Text.Json.JsonDocument.Parse(handler.LastRequestBody!);
        var sent = doc.RootElement.GetProperty("messages");
        Assert.Equal(3, sent.GetArrayLength());

        var assistant = sent[1];
        Assert.Equal("assistant", assistant.GetProperty("role").GetString());
        Assert.Equal(1, assistant.GetProperty("tool_calls").GetArrayLength());
        var call = assistant.GetProperty("tool_calls")[0];
        Assert.Equal("call_9", call.GetProperty("id").GetString());
        Assert.Equal("create_block", call.GetProperty("function").GetProperty("name").GetString());
        Assert.Equal("{\"length\":1}", call.GetProperty("function").GetProperty("arguments").GetString());

        var tool = sent[2];
        Assert.Equal("tool", tool.GetProperty("role").GetString());
        Assert.Equal("call_9", tool.GetProperty("tool_call_id").GetString());
        Assert.Equal("""{"status":"success"}""", tool.GetProperty("content").GetString());
    }

    [Fact]
    public async Task BearerHeader_StillSent_OnToolRequest()
    {
        string? auth = null;
        var handler = new FakeHandler(JsonOk(
            """{"choices":[{"message":{"role":"assistant","content":"ok"}}]}"""));
        var service = new ModelService(new HttpClient(new RecordingAuthHandler(handler, v => auth = v)));
        service.SetConfiguration("https://fake.test/v1", "test-key", "test-model");

        await service.GetChatCompletionWithToolsAsync([UserMessage], OneTool, CancellationToken.None);

        Assert.Equal("Bearer test-key", auth);
    }

    private sealed class RecordingAuthHandler : DelegatingHandler
    {
        private readonly Action<string?> _capture;
        public RecordingAuthHandler(HttpMessageHandler inner, Action<string?> capture)
            : base(inner) => _capture = capture;

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            _capture(request.Headers.Authorization?.ToString());
            return base.SendAsync(request, cancellationToken);
        }
    }

    // ---------------------------------------------------- parse edge cases

    private ChatCompletionResult ParseDirect(string body)
    {
        var handler = new FakeHandler(JsonOk(body));
        var service = CreateService(handler);
        return service.GetChatCompletionWithToolsAsync(
            [UserMessage], OneTool, CancellationToken.None).GetAwaiter().GetResult();
    }

    [Fact]
    public void Parse_NotJson_Fails()
    {
        var result = ParseDirect("}{");
        Assert.False(result.Success);
        Assert.Equal(ModelChatError.InvalidResponse, result.Error);
    }

    [Fact]
    public void Parse_NoChoices_Fails()
    {
        var result = ParseDirect("""{"choices":[]}""");
        Assert.False(result.Success);
    }

    [Fact]
    public void Parse_TextPlusToolCall_TreatedAsToolTurn()
    {
        var result = ParseDirect(
            """{"choices":[{"message":{"role":"assistant","content":"正在创建","tool_calls":[{"id":"c1","type":"function","function":{"name":"create_block","arguments":"{}"}}]}}]}""");
        Assert.True(result.Success);
        Assert.Single(result.ToolCalls);
    }
}
