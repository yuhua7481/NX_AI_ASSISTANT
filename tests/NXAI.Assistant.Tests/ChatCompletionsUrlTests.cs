using NXAI.Assistant.Core;
using Xunit;

namespace NXAI.Assistant.Tests;

/// <summary>
/// URL-normalization tests for ModelService.NormalizeChatCompletionsUrl.
/// Pure function tests — no network access.
/// </summary>
public class ChatCompletionsUrlTests
{
    [Theory]
    [InlineData("https://api.example.com",
        "https://api.example.com/v1/chat/completions")]
    [InlineData("https://api.example.com/",
        "https://api.example.com/v1/chat/completions")]
    [InlineData("https://api.example.com/v1",
        "https://api.example.com/v1/chat/completions")]
    [InlineData("https://api.example.com/v1/",
        "https://api.example.com/v1/chat/completions")]
    [InlineData("https://api.example.com/v1/chat/completions",
        "https://api.example.com/v1/chat/completions")]
    [InlineData("https://api.example.com/v1/chat/completions/",
        "https://api.example.com/v1/chat/completions")]
    [InlineData("https://api.example.com:8443",
        "https://api.example.com:8443/v1/chat/completions")]
    [InlineData("http://localhost:11434",
        "http://localhost:11434/v1/chat/completions")]
    [InlineData("https://open.bigmodel.cn/api/paas/v4",
        "https://open.bigmodel.cn/api/paas/v4/chat/completions")]
    [InlineData("https://proxy.internal/openai",
        "https://proxy.internal/openai/v1/chat/completions")]
    [InlineData("  https://api.example.com  ",
        "https://api.example.com/v1/chat/completions")]
    public void Normalize_ValidInputs_ProducesExpectedEndpoint(string input, string expected)
    {
        Assert.Equal(expected, ModelService.NormalizeChatCompletionsUrl(input));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Normalize_EmptyInput_ThrowsWithClearMessage(string? input)
    {
        var ex = Assert.Throws<ArgumentException>(
            () => ModelService.NormalizeChatCompletionsUrl(input));
        Assert.Contains("未配置", ex.Message);
    }

    [Theory]
    [InlineData("api.example.com")]          // no scheme, relative
    [InlineData("/v1/chat/completions")]     // relative path
    [InlineData("ftp://api.example.com")]    // unsupported scheme
    [InlineData("file://C:/config.json")]     // unsupported scheme
    public void Normalize_NotAbsoluteHttp_ThrowsWithClearMessage(string input)
    {
        var ex = Assert.Throws<ArgumentException>(
            () => ModelService.NormalizeChatCompletionsUrl(input));
        Assert.Contains("HTTP/HTTPS", ex.Message);
    }

    [Theory]
    [InlineData("https://api.example.com/?key=abc")]
    [InlineData("https://api.example.com#frag")]
    public void Normalize_QueryOrFragment_ThrowsWithClearMessage(string input)
    {
        var ex = Assert.Throws<ArgumentException>(
            () => ModelService.NormalizeChatCompletionsUrl(input));
        Assert.Contains("查询参数", ex.Message);
    }

    [Fact]
    public void Normalize_HostOnlyHttps_UpperCaseHost_IsAccepted()
    {
        // Uri normalizes host casing; the endpoint must still be well formed.
        string result = ModelService.NormalizeChatCompletionsUrl("HTTPS://API.EXAMPLE.COM/V1");
        Assert.StartsWith("https://api.example.com", result, StringComparison.OrdinalIgnoreCase);
        Assert.EndsWith("/v1/chat/completions", result, StringComparison.OrdinalIgnoreCase);
    }
}
