using System.Net;
using System.Text;
using NXAI.Assistant.Core;
using Xunit;

namespace NXAI.Assistant.Tests;

/// <summary>
/// Chat-request tests for ModelService using an injected HttpClient backed by
/// a fake HttpMessageHandler — no real network or model API is contacted.
/// </summary>
public class ModelServiceChatTests
{
    private const string BaseUrl = "https://api.example.com";
    private const string ApiKey = "sk-test-key";
    private const string ModelName = "gpt-4o";

    private static ChatMessage Msg(ChatRole role, string text) =>
        new(role, text, DateTimeOffset.UtcNow);

    private static HttpResponseMessage JsonOk(string body) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(body, Encoding.UTF8, "application/json"),
    };

    private static readonly string ValidResponseBody =
        "{\"choices\":[{\"message\":{\"content\":\"你好，我是助手。\"}}]}";

    private sealed class FakeHandler : HttpMessageHandler
    {
        public HttpRequestMessage? LastRequest;
        public string? LastRequestBody;
        public Func<HttpResponseMessage> Responder { get; set; } =
            () => new HttpResponseMessage(HttpStatusCode.OK);
        public Exception? Thrower;

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastRequest = request;
            if (request.Content != null)
            {
                LastRequestBody = await request.Content.ReadAsStringAsync(cancellationToken);
            }
            if (Thrower != null)
                throw Thrower;
            return Responder();
        }
    }

    private static (ModelService Service, FakeHandler Handler) CreateService()
    {
        var handler = new FakeHandler();
        var service = new ModelService(new HttpClient(handler));
        service.SetConfiguration(BaseUrl, ApiKey, ModelName);
        return (service, handler);
    }

    // ---------------------------------------------------------------- success

    [Fact]
    public async Task Success_ReturnsReplyAndSendsBearerAuth()
    {
        var (service, handler) = CreateService();
        handler.Responder = () => JsonOk(ValidResponseBody);

        var result = await service.GetChatCompletionAsync(new[] { Msg(ChatRole.User, "你好") });

        Assert.True(result.Success);
        Assert.Equal("你好，我是助手。", result.Reply);
        Assert.NotNull(handler.LastRequest);
        Assert.Equal("https://api.example.com/v1/chat/completions",
            handler.LastRequest!.RequestUri!.ToString());
        Assert.Equal("Bearer", handler.LastRequest.Headers.Authorization?.Scheme);
        Assert.Equal(ApiKey, handler.LastRequest.Headers.Authorization?.Parameter);
    }

    [Fact]
    public async Task Roles_UserAssistantSystemSent_InfoErrorFiltered()
    {
        var (service, handler) = CreateService();
        handler.Responder = () => JsonOk(ValidResponseBody);

        var result = await service.GetChatCompletionAsync(new[]
        {
            Msg(ChatRole.System, "你是建模助手。"),
            Msg(ChatRole.Info, "本地提示不应发送"),
            Msg(ChatRole.User, "创建一个方块"),
            Msg(ChatRole.Error, "本地错误不应发送"),
            Msg(ChatRole.Assistant, "好的"),
        });

        Assert.True(result.Success);
        Assert.NotNull(handler.LastRequestBody);

        // The serializer escapes non-ASCII; assert against the decoded document.
        using var doc = System.Text.Json.JsonDocument.Parse(handler.LastRequestBody!);
        var root = doc.RootElement;
        Assert.Equal(ModelName, root.GetProperty("model").GetString());
        Assert.False(root.GetProperty("stream").GetBoolean());
        var sent = root.GetProperty("messages");
        Assert.Equal(3, sent.GetArrayLength());
        Assert.Equal("system", sent[0].GetProperty("role").GetString());
        Assert.Equal("你是建模助手。", sent[0].GetProperty("content").GetString());
        Assert.Equal("user", sent[1].GetProperty("role").GetString());
        Assert.Equal("创建一个方块", sent[1].GetProperty("content").GetString());
        Assert.Equal("assistant", sent[2].GetProperty("role").GetString());
        Assert.Equal("好的", sent[2].GetProperty("content").GetString());
    }

    [Fact]
    public async Task BaseUrl_WithVersionSuffix_NotDuplicated()
    {
        var handler = new FakeHandler();
        var service = new ModelService(new HttpClient(handler));
        service.SetConfiguration("https://api.example.com/v1", ApiKey, ModelName);
        handler.Responder = () => JsonOk(ValidResponseBody);

        await service.GetChatCompletionAsync(new[] { Msg(ChatRole.User, "hi") });

        Assert.Equal("https://api.example.com/v1/chat/completions",
            handler.LastRequest!.RequestUri!.ToString());
    }

    // ------------------------------------------------------- local validation

    [Fact]
    public async Task NotConfigured_ReturnsNotConfiguredError_NoHttpCall()
    {
        var handler = new FakeHandler();
        var service = new ModelService(new HttpClient(handler));
        service.SetConfiguration("", "", "");

        var result = await service.GetChatCompletionAsync(new[] { Msg(ChatRole.User, "hi") });

        Assert.False(result.Success);
        Assert.Equal(ModelChatError.NotConfigured, result.Error);
        Assert.Contains("未配置", result.ErrorMessage);
        Assert.Null(handler.LastRequest);
    }

    [Fact]
    public async Task InvalidBaseUrl_ReturnsUrlError_NoHttpCall()
    {
        var handler = new FakeHandler();
        var service = new ModelService(new HttpClient(handler));
        service.SetConfiguration("not a url at all", ApiKey, ModelName);

        var result = await service.GetChatCompletionAsync(new[] { Msg(ChatRole.User, "hi") });

        Assert.False(result.Success);
        Assert.Equal(ModelChatError.InvalidBaseUrl, result.Error);
        Assert.Null(handler.LastRequest);
    }

    [Fact]
    public async Task OnlyInfoMessages_ReturnsEmptyMessagesError_NoHttpCall()
    {
        var (service, handler) = CreateService();
        handler.Responder = () => JsonOk(ValidResponseBody);

        var result = await service.GetChatCompletionAsync(new[] { Msg(ChatRole.Info, "本地记录") });

        Assert.False(result.Success);
        Assert.Equal(ModelChatError.EmptyMessages, result.Error);
        Assert.Null(handler.LastRequest);
    }

    // -------------------------------------------------------- status mapping

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, ModelChatError.AuthFailed, "鉴权失败")]
    [InlineData(HttpStatusCode.Forbidden, ModelChatError.AuthFailed, "鉴权失败")]
    [InlineData(HttpStatusCode.NotFound, ModelChatError.InvalidBaseUrl, "404")]
    [InlineData(HttpStatusCode.BadRequest, ModelChatError.InvalidRequest, "400")]
    [InlineData(HttpStatusCode.TooManyRequests, ModelChatError.RateLimited, "429")]
    [InlineData(HttpStatusCode.InternalServerError, ModelChatError.ServerError, "500")]
    [InlineData(HttpStatusCode.ServiceUnavailable, ModelChatError.ServerError, "503")]
    public async Task HttpFailures_MappedToErrorCategories(
        HttpStatusCode status, ModelChatError expectedError, string expectedMessagePart)
    {
        var (service, handler) = CreateService();
        handler.Responder = () => new HttpResponseMessage(status);

        var result = await service.GetChatCompletionAsync(new[] { Msg(ChatRole.User, "hi") });

        Assert.False(result.Success);
        Assert.Equal(expectedError, result.Error);
        Assert.Contains(expectedMessagePart, result.ErrorMessage);
    }

    // ------------------------------------------------------------ transport

    [Fact]
    public async Task NetworkFailure_ReturnsNetworkError()
    {
        var (service, handler) = CreateService();
        handler.Thrower = new HttpRequestException("无法连接到远程服务器");

        var result = await service.GetChatCompletionAsync(new[] { Msg(ChatRole.User, "hi") });

        Assert.False(result.Success);
        Assert.Equal(ModelChatError.NetworkError, result.Error);
        Assert.Contains("无法连接到远程服务器", result.ErrorMessage);
    }

    [Fact]
    public async Task Cancellation_PropagatesOperationCanceledException()
    {
        var (service, _) = CreateService();
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            service.GetChatCompletionAsync(new[] { Msg(ChatRole.User, "hi") }, cts.Token));
    }

    // ------------------------------------------------------- response parsing

    [Theory]
    [InlineData("not json at all", "合法 JSON")]
    [InlineData("{}", "choices")]
    [InlineData("{\"choices\":[]}", "choices")]
    [InlineData("{\"choices\":[{}]}", "message.content")]
    [InlineData("{\"choices\":[{\"message\":{}}]}", "message.content")]
    [InlineData("{\"choices\":[{\"message\":{\"content\":\"\"}}]}", "为空")]
    [InlineData("{\"choices\":[{\"message\":{\"content\":[{\"type\":\"text\"}]}}]}", "不是文本")]
    public async Task UnusableResponses_ReturnInvalidResponseWithReason(
        string body, string expectedMessagePart)
    {
        var (service, handler) = CreateService();
        handler.Responder = () => JsonOk(body);

        var result = await service.GetChatCompletionAsync(new[] { Msg(ChatRole.User, "hi") });

        Assert.False(result.Success);
        Assert.Equal(ModelChatError.InvalidResponse, result.Error);
        Assert.Contains(expectedMessagePart, result.ErrorMessage);
    }

    [Fact]
    public async Task ErrorMessage_NeverContainsApiKey()
    {
        var (service, handler) = CreateService();
        handler.Responder = () => new HttpResponseMessage(HttpStatusCode.Unauthorized);

        var result = await service.GetChatCompletionAsync(new[] { Msg(ChatRole.User, "hi") });

        Assert.False(result.Success);
        Assert.DoesNotContain(ApiKey, result.ErrorMessage);
    }
}
