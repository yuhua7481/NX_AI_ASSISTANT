using NXAI.Assistant.Core;
using Xunit;

namespace NXAI.Assistant.Tests;

/// <summary>
/// Configuration-persistence tests for ModelService (DPAPI-encrypted API key).
/// Every test uses an isolated temp file via the configPath parameter and must
/// never read or write the real %APPDATA%\NX AI Assistant\model-config.json.
/// </summary>
public class ModelServiceConfigurationTests : IDisposable
{
    private string ConfigPath { get; }

    public ModelServiceConfigurationTests()
    {
        ConfigPath = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".json");
    }

    public void Dispose()
    {
        if (File.Exists(ConfigPath))
            File.Delete(ConfigPath);
    }

    [Fact]
    public void SaveThenLoad_RoundTripsThroughDpapi()
    {
        ModelService.SaveConfiguration(
            "https://api.example.com", "sk-fake-test-key", "gpt-4o", ConfigPath);

        var result = ModelService.LoadConfiguration(ConfigPath);

        Assert.True(result.HasValue);
        Assert.Equal("https://api.example.com", result.Value.apiBaseUrl);
        Assert.Equal("sk-fake-test-key", result.Value.apiKey);
        Assert.Equal("gpt-4o", result.Value.modelName);
    }

    [Fact]
    public void Load_MissingFile_ReturnsNull()
    {
        Assert.False(ModelService.LoadConfiguration(ConfigPath).HasValue);
    }

    [Fact]
    public void Load_InvalidJson_ReturnsNullAndPreservesFile()
    {
        File.WriteAllText(ConfigPath, "<!>");

        var result = ModelService.LoadConfiguration(ConfigPath);

        Assert.False(result.HasValue);
        Assert.Equal("<!>", File.ReadAllText(ConfigPath));
    }

    [Fact]
    public void Load_CorruptedKey_ReturnsNullAndPreservesFile()
    {
        string tampered =
            "{\"apiBaseUrl\":\"https://api.example.com\",\"modelName\":\"gpt-4o\"," +
            "\"encryptedApiKey\":\"r4nd0mb@s64str!ng\"}";
        File.WriteAllText(ConfigPath, tampered);

        var result = ModelService.LoadConfiguration(ConfigPath);

        Assert.False(result.HasValue);
        Assert.Equal(tampered, File.ReadAllText(ConfigPath));
    }

    [Fact]
    public void Load_KeyWithControlChars_ReturnsNull()
    {
        // Round-trips through the public API: the guard must reject a decrypted
        // plaintext containing control characters. (\u0001, not \x01 — the \x
        // escape greedily consumes up to 4 hex digits.)
        ModelService.SaveConfiguration(
            "https://api.example.com", "\u0001bad-key", "gpt-4o", ConfigPath);

        Assert.False(ModelService.LoadConfiguration(ConfigPath).HasValue);
    }

    [Fact]
    public void Load_ValidBase64ButNotDpapi_ReturnsNullAndPreservesFile()
    {
        // 旧版本/未知加密格式：字节是合法 Base64，但不是 DPAPI blob。
        string legacyLooking =
            "{\"apiBaseUrl\":\"https://api.example.com\",\"modelName\":\"gpt-4o\"," +
            $"\"encryptedApiKey\":\"{Convert.ToBase64String("not-a-dpapi-blob"u8)}\"}}";
        File.WriteAllText(ConfigPath, legacyLooking);

        var result = ModelService.LoadConfiguration(ConfigPath);

        Assert.False(result.HasValue, "未知格式的密文不应被解释为有效配置");
        Assert.Equal(legacyLooking, File.ReadAllText(ConfigPath));
    }

    [Fact]
    public void Load_EmptySavedKey_TreatedAsNotConfigured()
    {
        // 保存空 Key（用户未设置）后，Load 应视为未配置而不是返回空密钥。
        ModelService.SaveConfiguration("https://api.example.com", "", "gpt-4o", ConfigPath);

        Assert.False(ModelService.LoadConfiguration(ConfigPath).HasValue);
    }

    [Fact]
    public void Save_FailingDestination_ThrowsInsteadOfSilent()
    {
        // 目标路径是目录 → 保存失败必须以异常暴露（UI 层捕获并向用户反馈），
        // 而不是静默成功或吞掉。
        string dirPath = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(dirPath);
        try
        {
            Assert.ThrowsAny<Exception>(
                () => ModelService.SaveConfiguration("https://api.example.com", "k", "m", dirPath));
        }
        finally
        {
            Directory.Delete(dirPath);
        }
    }

    [Fact]
    public void Save_OverwritesPreviousConfig()
    {
        ModelService.SaveConfiguration("https://old.example.com", "old-key", "old-model", ConfigPath);
        ModelService.SaveConfiguration("https://new.example.com", "new-key", "new-model", ConfigPath);

        var result = ModelService.LoadConfiguration(ConfigPath);

        Assert.True(result.HasValue);
        Assert.Equal("https://new.example.com", result.Value.apiBaseUrl);
        Assert.Equal("new-key", result.Value.apiKey);
        Assert.Equal("new-model", result.Value.modelName);
    }
}
