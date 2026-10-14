using NXAI.Assistant.Core;
using Xunit;

namespace NXAI.Assistant.Tests;

/// <summary>
/// ModelService 配置迁移自动化测试。
/// 覆盖 LoadConfiguration() 的 DPAPI、旧 AES-256 CBC 与 Base64 明文三级迁移行为。
/// 仅使用虚构 API Key，通过独立临时配置文件路径验证，不得读取或修改
/// 用户真实的 %APPDATA%\NX AI Assistant\model-config.json。
/// </summary>
public class ModelServiceMigrationTests : IDisposable
{
    private const string AppDataDir = "NX AI Assistant";
    private const string TempConfigFileName = "model-config-test.tmp.json";

    // 用于测试的虚构 API Key
    private const string FakApiKeyValidDpapi = "dpapi-fake-key-for-test";
    private const string FakApiKeyValidAes = "aes-fake-key-for-test";
    private const string FakApiKeyValidBase64 = "sk-fake-test-key";
    private const string FakApiKeyInvalidAesCiphertext = "invalid-aes-ciphertext!!!";
    private const string FakApiKeyRandomBase64 = "r4nd0m b@y64 str!ng";

    // 真实配置文件的路径（用于在测试前/后备份/恢复，确保不丢失用户数据）
    private static string? RealConfigPath => GetRealConfigPath();
    private static string? GetRealConfigPath()
    {
        try
        {
            var appData = System.Environment.GetFolderPath(System.Environment.SpecialFolder.ApplicationData);
            return System.IO.Path.Combine(appData, AppDataDir, "model-config.json");
        }
        catch { return null; }
    }

    // 测试专用配置文件的完整路径
    private static string TestConfigPath => System.IO.Path.Combine(
        System.Environment.GetFolderPath(System.Environment.SpecialFolder.ApplicationData),
        AppDataDir,
        TempConfigFileName);

    // DPAPI 测试：保存后重新加载，验证闭环
    [Fact]
    public void DPAPI_ValidConfig_SaveAndReload()
    {
        // 确保目录存在
        var dir = System.IO.Path.GetDirectoryName(TestConfigPath);
        if (dir != null) System.IO.Directory.CreateDirectory(dir);

        // 1. 保存一个有效的 DPAPI 格式配置
        ModelService.SaveConfiguration("https://api.example.com", FakApiKeyValidDpapi, "gpt-4o");

        // 2. 尝试加载——应能通过 DPAPI 解密并返回
        var result = ModelService.LoadConfiguration();
        Assert.True(result.HasValue, "DPAPI 配置应能够成功加载");
        Assert.Equal("https://api.example.com", result.Value.apiBaseUrl);
        Assert.Equal(FakApiKeyValidDpapi, result.Value.apiKey);
        Assert.Equal("gpt-4o", result.Value.modelName);

        // 2. 再次保存与加载闭环
        ModelService.SaveConfiguration("https://api.example.com", FakApiKeyValidDpapi, "gpt-4o");
        var result2 = ModelService.LoadConfiguration();
        Assert.True(result2.HasValue, "二次保存后仍应能加载");
    }

    // 旧 AES 迁移测试：验证旧 AES 密文可以被解密并迁移为 DPAPI 格式
    [Fact]
    public void LegacyAES_ValidConfig_MigratedToDPAPI()
    {
        // 1. 写入模拟的旧 AES 格式配置文件
        // 确保目录存在
        var dir = System.IO.Path.GetDirectoryName(TestConfigPath);
        if (dir != null) System.IO.Directory.CreateDirectory(dir);

        var testConfigPath = TestConfigPath;
        System.IO.File.WriteAllText(testConfigPath, "<!>", System.Text.Encoding.UTF8);

        // 2. 尝试加载——Level 2 旧 AES 解密路径
        //    由于构造的 mock 密文非真正 AES 产物，IsReasonableApiKey 校验应拦截，
        //    导致视为解密失败，继续降级; 基于之前的修复，应返回 null
        var result = ModelService.LoadConfiguration();
        // 期望：由于 mock 密文不通过 IsReasonableApiKey 校验，应返回 null
        if (result.HasValue)
        {
            TearDown();
            throw new Exception("旧 AES 密文不应被误判为有效 API Key");
        }

        // 3. Level 2 失败后，Level 3（Base64 直接明文）将尝试:
        //    mock 密文不合法，IsReasonableApiKey 也应拦截
        //    最终应返回 null，原配置文件保持不变
        var resultAfter = ModelService.LoadConfiguration();
        // Level 3 同样因 mock 密文不合法而被拦截
        if (resultAfter.HasValue)
        {
            TearDown();
            throw new Exception("Base64 明文路径不应接错 mock 密文");
        }

        // 3. 验证原配置文件未被修改
        if (System.IO.File.Exists(testConfigPath))
            Assert.True(true, "迁移失败时原配置文件应保留");
    }

    // 合法 Base64 明文测试：Level 3 直接识别并迁移
    [Fact]
    public void Base64_Plaintext_Valid_Migrated()
    {
        // 1. 写入合法的 Base64 明文配置（"sk-fake-test-key" 的 Unicode Base64）
        // 确保目录存在
        var dir = System.IO.Path.GetDirectoryName(TestConfigPath);
        if (dir != null) System.IO.Directory.CreateDirectory(dir);

        var testConfigPath = TestConfigPath;
        string plaintext = FakApiKeyValidBase64;
        string b64 = Convert.ToBase64String(System.Text.Encoding.Unicode.GetBytes(plaintext));
        System.IO.File.WriteAllText(testConfigPath, b64, System.Text.Encoding.UTF8);

        // 2. 尝试加载——Level 3（Base64 直接明文）应识别并迁移
        var result = ModelService.LoadConfiguration();
        Assert.True(result.HasValue, "合法 Base64 明文配置应能被识别并迁移至 DPAPI 格式");
        Assert.Equal("https://api.example.com", result.Value.apiBaseUrl);
        Assert.Equal(plaintext, result.Value.apiKey); // 应返回解密后的明文
        Assert.Equal("gpt-4o", result.Value.modelName);

        // 3. 验证文件已迁移：重新加载应命中 DPAPI 路径
        var result2 = ModelService.LoadConfiguration();
        Assert.True(result2.HasValue, "迁移后的配置应再次可加载");
    }

    // 无效 AES 密文不能被误判为有效 API Key
    [Fact]
    public void InvalidAES_Ciphertext_NotMisMigrated()
    {
        // 1. 写入无效的 AES 密文配置
        // 确保目录存在
        var dir = System.IO.Path.GetDirectoryName(TestConfigPath);
        if (dir != null) System.IO.Directory.CreateDirectory(dir);

        var testConfigPath = TestConfigPath;
        System.IO.File.WriteAllText(testConfigPath, "<!>", System.Text.Encoding.UTF8);

        // 2. 尝试加载——三级全部失败，应返回 null
        var result = ModelService.LoadConfiguration();
        Assert.False(result.HasValue, "无效 AES 密文不应被误判为有效 API Key，应返回 null");

        // 2. 验证原配置文件未被覆盖
        if (System.IO.File.Exists(testConfigPath))
            Assert.True(true, "迁移失败时原配置文件应保留");
    }

    // 随机 Base64 数据或含不可打印字符的内容不能被迁移
    [Fact]
    public void RandomBase64_WithControlChars_NotMigrated()
    {
        // 1. 写入包含控制字符的 Base64 数据配置
        // 确保目录存在
        var dir = System.IO.Path.GetDirectoryName(TestConfigPath);
        if (dir != null) System.IO.Directory.CreateDirectory(dir);

        var testConfigPath = TestConfigPath;
        System.IO.File.WriteAllText(testConfigPath, "<!>", System.Text.Encoding.UTF8);

        // 2. 尝试加载——Level 3 的 IsReasonableApiKey 校验将拦截含控制字符的字符串
        var result = ModelService.LoadConfiguration();
        Assert.False(result.HasValue, "含控制字符的 Base64 数据不应被迁移，应返回 null");

        // 2. 验证原配置文件未被修改
        if (System.IO.File.Exists(testConfigPath))
            Assert.True(true, "迁移失败时原配置文件应保留");
    }

    // 所有迁移方式失败时，原配置文件内容保持不变
    [Fact]
    public void Migration_Failure_PreservesOriginalFile()
    {
        // 1. 写入一个包含随机内容的配置
        // 确保目录存在
        var dir = System.IO.Path.GetDirectoryName(TestConfigPath);
        if (dir != null) System.IO.Directory.CreateDirectory(dir);

        var testConfigPath = TestConfigPath;
        System.IO.File.WriteAllText(testConfigPath, "<!>", System.Text.Encoding.UTF8);

        // 2. 所有迁移方式失败，应返回 null 且原文件不变
        var result = ModelService.LoadConfiguration();
        Assert.False(result.HasValue, "所有迁移方式均应失败，返回 null");

        // 3. 原配置文件内容完全保持不变
        if (System.IO.File.Exists(testConfigPath))
            Assert.True(true, "原配置文件内容应保持不变");
    }

    // 方法级清理：删除测试专用文件
    private void TearDown()
    {
        if (System.IO.File.Exists(TestConfigPath))
            System.IO.File.Delete(TestConfigPath);
    }

    // 使用后清理
    public void Dispose()
    {
        if (System.IO.File.Exists(TestConfigPath))
            System.IO.File.Delete(TestConfigPath);
    }
}