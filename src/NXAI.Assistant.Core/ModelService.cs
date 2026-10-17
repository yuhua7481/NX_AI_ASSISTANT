using System;
using System.IO;
using System.Net;
using System.Net.Http.Headers;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace NXAI.Assistant.Core;

/// <summary>
/// Windows DPAPI (CurrentUser scope) interop. Protect returns a Base64 blob
/// safe to persist to disk; Unprotect reverses it and returns null on failure.
/// DPAPI generates per-call random entropy internally — no fixed salt or IV.
/// </summary>
internal static class Dpapi
{
    [StructLayout(LayoutKind.Sequential)]
    private struct DATA_BLOB
    {
        public int cbSize;
        public IntPtr pBlobData;
    }

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Auto)]
    private static extern bool CryptUnprotectData(
        ref DATA_BLOB pDataIn,
        IntPtr ppszDataDescr,
        IntPtr pReserved,
        IntPtr pOptionalEntropy,
        IntPtr pPromptStruct,
        int dwFlags,
        out DATA_BLOB pDataOut);

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Auto)]
    private static extern bool CryptProtectData(
        ref DATA_BLOB pDataIn,
        IntPtr ppszDataDescr,
        IntPtr pReserved,
        IntPtr pOptionalEntropy,
        IntPtr pPromptStruct,
        int dwFlags,
        out DATA_BLOB pDataOut);

    private const int CRYPTPROTECT_UI_FORBIDDEN = 0x1;

    /// <summary>Encrypts clear text with DPAPI (current user scope).</summary>
    /// <returns>Base64-encoded encrypted blob; empty string for empty input.</returns>
    public static string Protect(string clearText)
    {
        if (string.IsNullOrEmpty(clearText))
            return "";

        byte[] inputBytes = Encoding.Unicode.GetBytes(clearText);
        DATA_BLOB input = new DATA_BLOB
        {
            cbSize = inputBytes.Length,
            pBlobData = Marshal.AllocHGlobal(inputBytes.Length),
        };
        try
        {
            Marshal.Copy(inputBytes, 0, input.pBlobData, inputBytes.Length);
            if (!CryptProtectData(ref input, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero,
                    CRYPTPROTECT_UI_FORBIDDEN, out DATA_BLOB output))
            {
                throw new CryptographicException($"DPAPI 加密失败，错误码: {Marshal.GetLastWin32Error()}");
            }
            try
            {
                byte[] blobBytes = new byte[output.cbSize];
                Marshal.Copy(output.pBlobData, blobBytes, 0, output.cbSize);
                return Convert.ToBase64String(blobBytes);
            }
            finally
            {
                Marshal.FreeHGlobal(output.pBlobData);
            }
        }
        finally
        {
            Marshal.FreeHGlobal(input.pBlobData);
        }
    }

    /// <summary>Decrypts a Base64-encoded DPAPI blob back to clear text.</summary>
    /// <returns>Clear text, or null when the blob is invalid or not decryptable by this user.</returns>
    public static string? Unprotect(string base64)
    {
        if (string.IsNullOrEmpty(base64))
            return null;

        byte[] blobBytes;
        try
        {
            blobBytes = Convert.FromBase64String(base64);
        }
        catch (FormatException)
        {
            return null;
        }

        DATA_BLOB input = new DATA_BLOB
        {
            cbSize = blobBytes.Length,
            pBlobData = Marshal.AllocHGlobal(blobBytes.Length),
        };
        try
        {
            Marshal.Copy(blobBytes, 0, input.pBlobData, blobBytes.Length);
            if (!CryptUnprotectData(ref input, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero,
                    CRYPTPROTECT_UI_FORBIDDEN, out DATA_BLOB output))
            {
                return null;
            }
            try
            {
                byte[] decryptedBytes = new byte[output.cbSize];
                Marshal.Copy(output.pBlobData, decryptedBytes, 0, output.cbSize);
                return Encoding.Unicode.GetString(decryptedBytes);
            }
            finally
            {
                Marshal.FreeHGlobal(output.pBlobData);
            }
        }
        finally
        {
            Marshal.FreeHGlobal(input.pBlobData);
        }
    }
}

/// <summary>
/// Outcome category of a chat-completions request. Callers use this to tell
/// configuration problems, auth failures, rate limits, server faults and
/// network problems apart.
/// </summary>
public enum ModelChatError
{
    None = 0,

    /// <summary>Base URL / API key / model name not configured.</summary>
    NotConfigured,

    /// <summary>Base URL invalid, or the endpoint does not exist (404).</summary>
    InvalidBaseUrl,

    /// <summary>No sendable message in the conversation (only Info/Error entries).</summary>
    EmptyMessages,

    /// <summary>Provider rejected the request (HTTP 400 or another unexpected 4xx).</summary>
    InvalidRequest,

    /// <summary>API key invalid or lacking permission (HTTP 401/403).</summary>
    AuthFailed,

    /// <summary>Rate limited or quota exhausted (HTTP 429).</summary>
    RateLimited,

    /// <summary>Provider-side failure (HTTP 5xx).</summary>
    ServerError,

    /// <summary>Connection failure or timeout.</summary>
    NetworkError,

    /// <summary>Success status but the body is unusable (bad JSON, no choices/content).</summary>
    InvalidResponse,
}

/// <summary>
/// Result of a chat-completions request: either Success with the assistant
/// Reply, or an Error category with a user-facing ErrorMessage. Error
/// messages never contain the API key, auth header or other secrets.
/// </summary>
public sealed class ModelChatResult
{
    private ModelChatResult(bool success, string? reply, ModelChatError error, string errorMessage)
    {
        Success = success;
        Reply = reply;
        Error = error;
        ErrorMessage = errorMessage;
    }

    public bool Success { get; }
    public string? Reply { get; }
    public ModelChatError Error { get; }
    public string ErrorMessage { get; }

    public static ModelChatResult Ok(string reply) =>
        new(true, reply, ModelChatError.None, "");

    public static ModelChatResult Fail(ModelChatError error, string message) =>
        new(false, null, error, message);
}

/// <summary>
/// Model API client. Sends chat messages as an OpenAI-compatible
/// Chat Completions request and returns the assistant reply text.
/// HTTP runs async — callers must not block the NX UI thread on it.
/// </summary>
public sealed class ModelService
{
    private readonly HttpClient _httpClient;
    private string _apiBaseUrl;
    private string _apiKey;
    private string _modelName;

    /// <summary>
    /// Creates the service. Tests inject an HttpClient backed by a fake
    /// HttpMessageHandler; production uses the default client.
    /// </summary>
    public ModelService(HttpClient? httpClient = null)
    {
        _httpClient = httpClient ?? new HttpClient();
        _apiBaseUrl = "";
        _apiKey = "";
        _modelName = "";
    }

    /// <summary>
    /// Sets the provider configuration. Credentials come from the persisted
    /// configuration (DPAPI-protected) or direct user input — never hard-coded.
    /// </summary>
    public void SetConfiguration(string apiBaseUrl, string apiKey, string modelName)
    {
        _apiBaseUrl = apiBaseUrl;
        _apiKey = apiKey;
        _modelName = modelName;
    }

    /// <summary>True when API Base URL, API key and model name are all configured.</summary>
    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(_apiBaseUrl) &&
        !string.IsNullOrWhiteSpace(_apiKey) &&
        !string.IsNullOrWhiteSpace(_modelName);

    /// <summary>
    /// Normalizes a provider base URL into a full chat-completions endpoint.
    /// Supported conventions (OpenAI-compatible):
    ///   - a URL already ending with "/chat/completions" is used unchanged;
    ///   - a URL ending with a version segment "/v{digits}" (e.g. /v1, /v4)
    ///     gets "/chat/completions" appended;
    ///   - anything else gets the default "/v1/chat/completions" appended.
    /// Rejects empty, relative and non-HTTP(S) URLs, and URLs carrying a query
    /// or fragment — these are reported instead of silently producing a wrong
    /// request address.
    /// </summary>
    /// <param name="apiBaseUrl">用户配置的服务基础地址。</param>
    /// <returns>The absolute chat-completions endpoint URL.</returns>
    /// <exception cref="ArgumentException">The input is empty, not an absolute HTTP(S) URL, or carries a query/fragment.</exception>
    public static string NormalizeChatCompletionsUrl(string? apiBaseUrl)
    {
        if (string.IsNullOrWhiteSpace(apiBaseUrl))
            throw new ArgumentException("API Base URL 未配置。", nameof(apiBaseUrl));

        string trimmed = apiBaseUrl.Trim();
        if (!Uri.TryCreate(trimmed, UriKind.Absolute, out Uri? uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            throw new ArgumentException(
                $"API Base URL 不是合法的 HTTP/HTTPS 绝对地址：{trimmed}", nameof(apiBaseUrl));
        }
        if (uri.Query.Length > 0 || uri.Fragment.Length > 0)
        {
            throw new ArgumentException(
                "API Base URL 不支持查询参数（?）或片段（#），请只填写服务地址。", nameof(apiBaseUrl));
        }

        string url = uri.GetLeftPart(UriPartial.Path).TrimEnd('/');
        if (url.EndsWith("/chat/completions", StringComparison.OrdinalIgnoreCase))
            return url;
        if (System.Text.RegularExpressions.Regex.IsMatch(url, @"/v\d+$",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase))
            return url + "/chat/completions";
        return url + "/v1/chat/completions";
    }

    /// <summary>
    /// Sends a chat request and returns the assistant reply, or a categorized
    /// failure. Only User/Assistant/System roles are sent; local Info/Error
    /// log entries are filtered out. Caller-initiated cancellation propagates
    /// as <see cref="OperationCanceledException"/>; every other failure comes
    /// back as a <see cref="ModelChatResult"/> the UI can show safely.
    /// </summary>
    /// <param name="messages">对话消息列表。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    public async Task<ModelChatResult> GetChatCompletionAsync(
        System.Collections.Generic.IEnumerable<ChatMessage> messages,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(_apiBaseUrl) || string.IsNullOrWhiteSpace(_apiKey) ||
            string.IsNullOrWhiteSpace(_modelName))
        {
            return ModelChatResult.Fail(ModelChatError.NotConfigured,
                "模型 API 未配置：请先在“模型 API 配置”中填写 API Base URL、API Key 和模型名称。");
        }

        string fullUrl;
        try
        {
            fullUrl = NormalizeChatCompletionsUrl(_apiBaseUrl);
        }
        catch (ArgumentException ex)
        {
            return ModelChatResult.Fail(ModelChatError.InvalidBaseUrl, ex.Message);
        }

        var sendable = System.Linq.Enumerable.ToArray(
            System.Linq.Enumerable.Where(messages, m =>
                m.Role is ChatRole.User or ChatRole.Assistant or ChatRole.System));
        if (sendable.Length == 0)
        {
            return ModelChatResult.Fail(ModelChatError.EmptyMessages,
                "没有可发送的对话消息。");
        }

        var payload = new
        {
            model = _modelName,
            messages = System.Linq.Enumerable.Select(sendable, m => new
            {
                role = m.Role switch
                {
                    ChatRole.User => "user",
                    ChatRole.Assistant => "assistant",
                    _ => "system",
                },
                content = m.Text,
            }),
            stream = false,
        };
        string json = JsonSerializer.Serialize(payload);

        HttpResponseMessage response;
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(fullUrl))
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json"),
            };
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _apiKey);
            response = await _httpClient.SendAsync(request, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            return ModelChatResult.Fail(ModelChatError.NetworkError, "请求超时。");
        }
        catch (HttpRequestException ex)
        {
            return ModelChatResult.Fail(ModelChatError.NetworkError, $"网络请求失败：{ex.Message}");
        }

        using (response)
        {
            if (!response.IsSuccessStatusCode)
            {
                return MapHttpStatus(response.StatusCode);
            }

            string responseBody;
            try
            {
                responseBody = await response.Content.ReadAsStringAsync(cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                return ModelChatResult.Fail(ModelChatError.NetworkError, $"读取响应失败：{ex.Message}");
            }

            string? reply = ParseChatReply(responseBody, out string parseError);
            return reply != null
                ? ModelChatResult.Ok(reply)
                : ModelChatResult.Fail(ModelChatError.InvalidResponse, parseError);
        }
    }

    private static ModelChatResult MapHttpStatus(HttpStatusCode statusCode) => statusCode switch
    {
        HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden =>
            ModelChatResult.Fail(ModelChatError.AuthFailed,
                $"鉴权失败（HTTP {(int)statusCode}）：API Key 无效或没有访问权限，请检查密钥。"),
        HttpStatusCode.NotFound =>
            ModelChatResult.Fail(ModelChatError.InvalidBaseUrl,
                "接口不存在（HTTP 404）：请检查 API Base URL 是否正确。"),
        HttpStatusCode.BadRequest =>
            ModelChatResult.Fail(ModelChatError.InvalidRequest,
                "请求被拒绝（HTTP 400）：常见原因是模型名称不正确或该服务不支持当前请求格式。"),
        HttpStatusCode.TooManyRequests =>
            ModelChatResult.Fail(ModelChatError.RateLimited,
                "请求过于频繁或额度不足（HTTP 429），请稍后重试。"),
        var code when (int)code >= 500 =>
            ModelChatResult.Fail(ModelChatError.ServerError,
                $"模型服务错误（HTTP {(int)code}），请稍后重试。"),
        var code =>
            ModelChatResult.Fail(ModelChatError.InvalidRequest,
                $"请求被拒绝（HTTP {(int)code}）。"),
    };

    /// <summary>
    /// Extracts the reply text from a chat-completions response body.
    /// Returns null with a user-facing reason when the body is not usable.
    /// </summary>
    private static string? ParseChatReply(string responseBody, out string error)
    {
        try
        {
            using var doc = JsonDocument.Parse(responseBody);
            if (!doc.RootElement.TryGetProperty("choices", out var choices) ||
                choices.GetArrayLength() == 0)
            {
                error = "模型响应中没有 choices 内容。";
                return null;
            }
            if (!choices[0].TryGetProperty("message", out var message) ||
                !message.TryGetProperty("content", out var content))
            {
                error = "模型响应中没有 message.content。";
                return null;
            }
            if (content.ValueKind != JsonValueKind.String)
            {
                error = "模型响应内容不是文本。";
                return null;
            }
            string? text = content.GetString();
            if (string.IsNullOrWhiteSpace(text))
            {
                error = "模型响应内容为空。";
                return null;
            }
            error = "";
            return text;
        }
        catch (JsonException)
        {
            error = "模型响应不是合法 JSON。";
            return null;
        }
    }

    /// <summary>
    /// Maximum tool calls accepted in a single assistant message. Parallel tool
    /// calls are a Phase-3+ capability: one user turn may produce at most one
    /// confirmed modeling execution, so any message carrying more than one call
    /// is refused rather than partially executed.
    /// </summary>
    public const int MaxToolCallsPerMessage = 1;

    /// <summary>
    /// Chat-completions request with the tool protocol: declares the registered
    /// tools, sends the conversation including assistant tool_calls and tool
    /// results, and parses the assistant's reply — either plain text or tool
    /// calls. Plain-text conversations behave exactly like
    /// <see cref="GetChatCompletionAsync"/>.
    /// </summary>
    /// <param name="messages">Outbound conversation (system/user/assistant/tool).</param>
    /// <param name="tools">Tool declarations to advertise; null or empty sends no tools.</param>
    /// <param name="cancellationToken">取消令牌。</param>
    public async Task<ChatCompletionResult> GetChatCompletionWithToolsAsync(
        IReadOnlyList<OutboundMessage> messages,
        IReadOnlyList<ToolDefinition>? tools,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(_apiBaseUrl) || string.IsNullOrWhiteSpace(_apiKey) ||
            string.IsNullOrWhiteSpace(_modelName))
        {
            return ChatCompletionResult.Fail(ModelChatError.NotConfigured,
                "模型 API 未配置：请先在“模型 API 配置”中填写 API Base URL、API Key 和模型名称。");
        }

        string fullUrl;
        try
        {
            fullUrl = NormalizeChatCompletionsUrl(_apiBaseUrl);
        }
        catch (ArgumentException ex)
        {
            return ChatCompletionResult.Fail(ModelChatError.InvalidBaseUrl, ex.Message);
        }

        if (messages.Count == 0)
        {
            return ChatCompletionResult.Fail(ModelChatError.EmptyMessages, "没有可发送的对话消息。");
        }

        var payload = new Dictionary<string, object?>
        {
            ["model"] = _modelName,
            ["messages"] = SerializeMessages(messages),
            ["stream"] = false,
        };
        if (tools is { Count: > 0 })
        {
            var toolObjects = new List<object>(tools.Count);
            foreach (var tool in tools)
            {
                JsonElement schema;
                try
                {
                    using var schemaDoc = JsonDocument.Parse(tool.ParametersJsonSchema);
                    schema = schemaDoc.RootElement.Clone();
                }
                catch (JsonException)
                {
                    return ChatCompletionResult.Fail(ModelChatError.InvalidRequest,
                        $"工具 {tool.Name} 的 JSON Schema 不是合法 JSON（内部错误）。");
                }
                toolObjects.Add(new Dictionary<string, object?>
                {
                    ["type"] = "function",
                    ["function"] = new Dictionary<string, object?>
                    {
                        ["name"] = tool.Name,
                        ["description"] = tool.Description,
                        ["parameters"] = schema,
                    },
                });
            }
            payload["tools"] = toolObjects;
        }

        string json = JsonSerializer.Serialize(payload);

        HttpResponseMessage response;
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(fullUrl))
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json"),
            };
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _apiKey);
            response = await _httpClient.SendAsync(request, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            return ChatCompletionResult.Fail(ModelChatError.NetworkError, "请求超时。");
        }
        catch (HttpRequestException ex)
        {
            return ChatCompletionResult.Fail(ModelChatError.NetworkError, $"网络请求失败：{ex.Message}");
        }

        using (response)
        {
            if (!response.IsSuccessStatusCode)
            {
                var mapped = MapHttpStatus(response.StatusCode);
                return ChatCompletionResult.Fail(mapped.Error, mapped.ErrorMessage);
            }

            string responseBody;
            try
            {
                responseBody = await response.Content.ReadAsStringAsync(cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                return ChatCompletionResult.Fail(ModelChatError.NetworkError, $"读取响应失败：{ex.Message}");
            }

            return ParseChatCompletionResponse(responseBody);
        }
    }

    private static System.Collections.Generic.List<object> SerializeMessages(
        IReadOnlyList<OutboundMessage> messages)
    {
        var result = new List<object>(messages.Count);
        foreach (var message in messages)
        {
            if (message.Role == OutboundMessage.RoleTool)
            {
                result.Add(new Dictionary<string, object?>
                {
                    ["role"] = RoleNameTool,
                    ["tool_call_id"] = message.ToolCallId ?? "",
                    ["content"] = message.Content ?? "",
                });
                continue;
            }

            if (message.ToolCalls is { Count: > 0 })
            {
                result.Add(new Dictionary<string, object?>
                {
                    ["role"] = "assistant",
                    ["content"] = message.Content is { Length: > 0 } ? message.Content : null,
                    ["tool_calls"] = message.ToolCalls!.Select(call => new Dictionary<string, object?>
                    {
                        ["id"] = call.Id,
                        ["type"] = "function",
                        ["function"] = new Dictionary<string, object?>
                        {
                            ["name"] = call.FunctionName,
                            ["arguments"] = call.ArgumentsJson,
                        },
                    }).ToArray(),
                });
                continue;
            }

            result.Add(new Dictionary<string, object?>
            {
                ["role"] = message.Role,
                ["content"] = message.Content ?? "",
            });
        }
        return result;
    }

    /// <summary>
    /// Parses a chat-completions response body that may contain tool calls.
    /// An assistant message with empty content and valid tool_calls is valid;
    /// parallel tool calls (more than one) are rejected as unsupported.
    ///
    /// Phase 7.3: some reasoning models (observed with z-ai GLM behind
    /// OpenAI-compatible gateways) emit the tool call as XML-ish tags inside
    /// message.content instead of the tool_calls array. Such content is run
    /// through the restricted <see cref="ToolTagProtocol"/> adapter: a
    /// complete, well-formed block becomes a typed tool call (same
    /// registry/schema/authorization path as structured calls); an
    /// incomplete or malformed block becomes a protocol error so the raw
    /// control markers never reach the chat display.
    /// </summary>
    internal static ChatCompletionResult ParseChatCompletionResponse(string responseBody)
    {
        try
        {
            using var doc = JsonDocument.Parse(responseBody);
            var root = doc.RootElement;
            if (!root.TryGetProperty("choices", out var choices) || choices.GetArrayLength() == 0)
            {
                return ChatCompletionResult.Fail(ModelChatError.InvalidResponse, "模型响应中没有 choices 内容。");
            }
            if (!choices[0].TryGetProperty("message", out var message))
            {
                return ChatCompletionResult.Fail(ModelChatError.InvalidResponse, "模型响应中没有 message。");
            }

            var toolCalls = ParseToolCalls(message, out string? toolCallError);
            if (toolCallError != null)
            {
                return ChatCompletionResult.Fail(ModelChatError.InvalidResponse, toolCallError);
            }

            string? text = null;
            if (message.TryGetProperty("content", out var content) &&
                content.ValueKind == JsonValueKind.String)
            {
                text = content.GetString();
            }

            if (toolCalls.Count > 0)
            {
                if (toolCalls.Count > MaxToolCallsPerMessage)
                {
                    return ChatCompletionResult.Fail(ModelChatError.InvalidResponse,
                        $"模型一次请求了 {toolCalls.Count} 个工具调用；当前版本每条消息最多支持 {MaxToolCallsPerMessage} 个。");
                }
                return ChatCompletionResult.OkToolCalls(toolCalls);
            }

            // No structured tool_calls. If the model emitted its call as an
            // in-content tag block, run the restricted adapter; otherwise the
            // content is plain text.
            if (text != null && ToolTagProtocol.ContainsToolMarker(text))
            {
                var scan = ToolTagProtocol.Scan(text);
                if (scan.ProtocolError != null)
                {
                    return ChatCompletionResult.Fail(ModelChatError.InvalidResponse, scan.ProtocolError);
                }
                if (scan.ToolCall != null)
                {
                    return ChatCompletionResult.OkToolCalls(
                        [new ParsedToolCall(NewTagCallId(), scan.ToolCall.FunctionName, scan.ToolCall.ArgumentsJson)]);
                }
                if (string.IsNullOrWhiteSpace(text))
                {
                    return ChatCompletionResult.Fail(ModelChatError.InvalidResponse, "模型响应内容为空。");
                }
                return ChatCompletionResult.OkText(text);
            }

            if (string.IsNullOrWhiteSpace(text))
            {
                return ChatCompletionResult.Fail(ModelChatError.InvalidResponse, "模型响应内容为空。");
            }
            return ChatCompletionResult.OkText(text);
        }
        catch (JsonException)
        {
            return ChatCompletionResult.Fail(ModelChatError.InvalidResponse, "模型响应不是合法 JSON。");
        }
    }

    /// <summary>
    /// Synthesizes the tool_call id for tag-protocol calls: the model did not
    /// provide one (there is no structured tool_calls entry), but the
    /// round-trip back through the protocol (assistant tool_calls + tool
    /// result) requires a stable id. Deterministic counter — no secrets.
    /// </summary>
    private static int _tagCallIdCounter;

    internal static string NewTagCallId() => $"tagcall_{Interlocked.Increment(ref _tagCallIdCounter)}";

    private static IReadOnlyList<ParsedToolCall> ParseToolCalls(
        JsonElement message, out string? error)
    {
        error = null;
        if (!message.TryGetProperty("tool_calls", out var toolCallsElement))
            return Array.Empty<ParsedToolCall>();

        if (toolCallsElement.ValueKind != JsonValueKind.Array)
        {
            error = "模型响应的 tool_calls 不是数组。";
            return Array.Empty<ParsedToolCall>();
        }

        var calls = new List<ParsedToolCall>();
        int index = 0;
        foreach (var callElement in toolCallsElement.EnumerateArray())
        {
            index++;
            if (callElement.ValueKind != JsonValueKind.Object)
            {
                error = $"模型响应的第 {index} 个工具调用不是对象。";
                return Array.Empty<ParsedToolCall>();
            }
            if (!callElement.TryGetProperty("function", out var function))
            {
                error = $"模型响应的第 {index} 个工具调用缺少 function 字段。";
                return Array.Empty<ParsedToolCall>();
            }

            string id = callElement.TryGetProperty("id", out var idElement) &&
                idElement.ValueKind == JsonValueKind.String
                    ? idElement.GetString() ?? ""
                    : "";
            string name = function.TryGetProperty("name", out var nameElement) &&
                nameElement.ValueKind == JsonValueKind.String
                    ? nameElement.GetString() ?? ""
                    : "";
            string arguments = function.TryGetProperty("arguments", out var argsElement) &&
                argsElement.ValueKind == JsonValueKind.String
                    ? argsElement.GetString() ?? ""
                    : "";

            if (string.IsNullOrWhiteSpace(id))
            {
                error = $"模型响应的第 {index} 个工具调用缺少 id。";
                return Array.Empty<ParsedToolCall>();
            }
            if (string.IsNullOrWhiteSpace(name))
            {
                error = $"模型响应的第 {index} 个工具调用缺少 function.name。";
                return Array.Empty<ParsedToolCall>();
            }
            calls.Add(new ParsedToolCall(id, name, arguments));
        }
        return calls;
    }

    private const string RoleNameTool = "tool";

    /// <summary>
    /// Default persisted-configuration path: %APPDATA%\NX AI Assistant\model-config.json.
    /// </summary>
    public static string DefaultConfigPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "NX AI Assistant",
        "model-config.json");

    /// <summary>
    /// Persists the configuration. API Base URL and model name are stored as plain
    /// text; the API key is encrypted with DPAPI (current user scope) before writing.
    /// </summary>
    /// <param name="apiBaseUrl">模型 API 的基础 URL（明文存储）。</param>
    /// <param name="apiKey">模型 API 的密钥（DPAPI 加密后落盘）。</param>
    /// <param name="modelName">模型名称（明文存储）。</param>
    /// <param name="configPath">Overrides the destination file (tests use an isolated path).</param>
    public static void SaveConfiguration(string apiBaseUrl, string apiKey, string modelName,
        string? configPath = null)
    {
        string path = configPath ?? DefaultConfigPath;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        var config = new
        {
            apiBaseUrl,
            modelName,
            encryptedApiKey = Dpapi.Protect(apiKey),
        };
        File.WriteAllText(path, JsonSerializer.Serialize(config), Encoding.UTF8);
    }

    /// <summary>
    /// Loads the persisted configuration and decrypts the API key with DPAPI.
    /// Returns null when the file does not exist, is unreadable, has invalid JSON,
    /// or the key cannot be decrypted. A failed load never rewrites the file.
    /// </summary>
    /// <param name="configPath">Overrides the source file (tests use an isolated path).</param>
    public static (string apiBaseUrl, string apiKey, string modelName)? LoadConfiguration(string? configPath = null)
    {
        string path = configPath ?? DefaultConfigPath;
        if (!File.Exists(path))
        {
            return null;
        }

        string json;
        try
        {
            json = File.ReadAllText(path, Encoding.UTF8);
        }
        catch
        {
            return null;
        }

        ConfigData? config;
        try
        {
            config = JsonSerializer.Deserialize<ConfigData>(json);
        }
        catch (JsonException)
        {
            return null;
        }
        if (config == null)
            return null;

        string? decryptedKey = Dpapi.Unprotect(config.encryptedApiKey);
        if (decryptedKey == null || !IsReasonableApiKey(decryptedKey))
        {
            // Unknown or corrupted payload: leave the file untouched and report "not configured".
            return null;
        }

        return (config.apiBaseUrl, decryptedKey, config.modelName);
    }

    private class ConfigData
    {
        public string apiBaseUrl { get; set; } = "";
        public string modelName { get; set; } = "";
        public string encryptedApiKey { get; set; } = "";
    }

    /// <summary>
    /// Sanity check on a decrypted key: non-empty, at most 256 chars, printable
    /// ASCII only (no control characters).
    /// </summary>
    private static bool IsReasonableApiKey(string? key)
    {
        if (string.IsNullOrWhiteSpace(key) || key.Length > 256)
            return false;
        foreach (char c in key)
        {
            if (c < 0x20 || c == 0x7F)
                return false;
        }
        return true;
    }
}
