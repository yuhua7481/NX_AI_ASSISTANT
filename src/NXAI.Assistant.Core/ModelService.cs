using System;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Security.Cryptography;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace NXAI.Assistant.Core;

/// <summary>
/// Windows Data Protection API (DPAPI) 互操作层。
/// - CryptProtectData：使用当前 Windows 用户范围加密数据
/// - CryptUnprotectData：使用当前 Windows 用户范围解密数据
/// - 避免硬编码秘密、固定盐值或固定 IV。每次加密使用系统生成的随机惟一数据密钥。
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

    private const int CRYPLEPROTECT_UI_FORBIDDEN = 0x1;
    private const int CRYPLEPROTECT_LOCAL_MACHINE = 0x4;

    /// <summary>
    /// 使用 DPAPI 用户范围加密明文字符串。
    /// 返回 Base64 编码的字符串，可安全保存到文件。
    /// </summary>
    /// <param name="clearText">明文字符串。</param>
    /// <returns>Base64 编码的加密 blob。</returns>
    public static string Protect(string clearText)
    {
        if (string.IsNullOrEmpty(clearText))
            return "";

        DATA_BLOB input = new DATA_BLOB();
        INPUT_BYTE_BLOCK inputBytes = new INPUT_BYTE_BLOCK(Encoding.Unicode.GetBytes(clearText));
        input.cbSize = inputBytes.bytes.Length;
        input.pBlobData = Marshal.AllocHGlobal(input.cbSize);
        Marshal.Copy(inputBytes.bytes, 0, input.pBlobData, input.cbSize);

        DATA_BLOB output = new DATA_BLOB();
        bool success = CryptProtectData(ref input, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, CRYPLEPROTECT_UI_FORBIDDEN, out output);

        if (!success)
        {
            int error = Marshal.GetLastWin32Error();
            Marshal.FreeHGlobal(input.pBlobData);
            throw new CryptographicException($"DPAPI 加密失败，错误码: {error}");
        }

        int size = output.cbSize;
        byte[] blobBytes = new byte[size];
        Marshal.Copy(output.pBlobData, blobBytes, 0, size);

        // 清理
        Marshal.FreeHGlobal(input.pBlobData);
        Marshal.FreeHGlobal(output.pBlobData);

        return Convert.ToBase64String(blobBytes);
    }

    /// <summary>
    /// 使用 DPAPI 用户范围解密 Base64 编码的 blob。
    /// </summary>
    /// <param name="base64">Base64 编码的加密 blob。</param>
    /// <returns>明文字符串，解密失败返回 null。</returns>
    public static string? Unprotect(string base64)
    {
        if (string.IsNullOrEmpty(base64))
            return null;

        byte[] blobBytes = Convert.FromBase64String(base64);
        DATA_BLOB input = new DATA_BLOB();
        input.cbSize = blobBytes.Length;
        input.pBlobData = Marshal.AllocHGlobal(input.cbSize);
        Marshal.Copy(blobBytes, 0, input.pBlobData, input.cbSize);

        DATA_BLOB output = new DATA_BLOB();
        // ppszDataDescr: 指向描述字符串的指针。由于我们不需要描述，传 IntPtr.Zero。
        // 如需描述，API 会分配内存且须使用 LocalFree 释放；此处忽略描述。
        bool success = CryptUnprotectData(ref input, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, CRYPLEPROTECT_UI_FORBIDDEN, out output);

        if (!success)
        {
            int error = Marshal.GetLastWin32Error();
            Marshal.FreeHGlobal(input.pBlobData);
            return null;
        }

        int size = output.cbSize;
        byte[] decryptedBytes = new byte[size];
        Marshal.Copy(output.pBlobData, decryptedBytes, 0, size);

        // 清理
        Marshal.FreeHGlobal(input.pBlobData);
        Marshal.FreeHGlobal(output.pBlobData);

        return Encoding.Unicode.GetString(decryptedBytes);
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    private struct INPUT_BYTE_BLOCK
    {
        public int cbSize;
        public byte[] bytes;
        public INPUT_BYTE_BLOCK(byte[] bytes)
        {
            this.bytes = bytes;
            cbSize = bytes.Length;
        }
    }
}

/// <summary>
/// 独立的模型服务组件，封装 HTTP 请求。
/// - 贕责将聊聊天消息序列化为 OpenAI Chat Completions 格式
/// - 贕负责异步发送请求，不阻塞 NX UI 线程
/// - 蕴负责解析助手回复，返回纯文本
/// - 提供配置持久化与密钥加密（现使用 Windows DPAPI）
/// </summary>
public enum ModelProvider
{
    OpenAI,
    Anthropic,
    OpenAICompatible,
}

public sealed class ModelService
{
    private readonly HttpClient _httpClient;
    private string _apiBaseUrl;
    private string _apiKey;
    private string _modelName;

    /// <summary>
    /// ctor - 配置由外部通过 SetConfiguration 或工厂传入。
    /// </summary>
    public ModelService()
    {
        _httpClient = new HttpClient();
        _apiBaseUrl = "";
        _apiKey = "";
        _modelName = "";
    }

    /// <summary>
    /// 设置模型服务配置。密钥和 Base URL 应该来自安全存储或用户设置，
    /// 不应硬编码在源码中。
    /// </summary>
    public void SetConfiguration(string apiBaseUrl, string apiKey, string modelName)
    {
        _apiBaseUrl = apiBaseUrl;
        _apiKey = apiKey;
        _modelName = modelName;
    }

    /// <summary>
    /// 发送聊天请求并返回助手回复文本。异步执行，调用方应在 UI 线程上下文中等待结果。
    /// </summary>
    /// <param name="messages">聊天消息列表。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>助手回复文本；如果请求失败则返回 null。</returns>
    public async Task<string?> GetChatCompletionAsync(System.Collections.Generic.IEnumerable<ChatMessage> messages, System.Threading.CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(_apiBaseUrl) || string.IsNullOrWhiteSpace(_apiKey))
        {
            return null;
        }

        // 组装 OpenAI Chat Completions 请求 payload - 手动构建 JSON
        var sb = new StringBuilder();
        sb.Append("{");
        sb.Append("\"model\":");
        sb.Append(_modelName);
        sb.Append(",\"messages\":[");
        bool first = true;
        foreach (var msg in messages)
        {
            if (!first) sb.Append(",");
            first = false;
            string role = msg.Role == ChatRole.User ? "user" : msg.Role == ChatRole.Assistant ? "assistant" : "system";
            string content = msg.Text;
            sb.Append("{\"role\":\"");
            sb.Append(role);
            sb.Append("\",\"content\":\"");
            sb.Append(EscapeJsonString(content));
            sb.Append("\"}");
        }
        sb.Append("],\"stream\":false}");
        var json = sb.ToString();

        var fullUrl = _apiBaseUrl.EndsWith("/") ? _apiBaseUrl[..^1] : _apiBaseUrl;
        fullUrl = fullUrl.EndsWith("/v1/chat/completions") || fullUrl.EndsWith("/v1")
            ? fullUrl
            : fullUrl.TrimEnd('/') + "/v1/chat/completions";

        try
        {
            var content = new StringContent(json, Encoding.UTF8, "application/json");
            var response = await _httpClient.PostAsync(new Uri(fullUrl), content, cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                // 读取错误正文以便日志（不返回给用户）
                _ = await response.Content.ReadAsStringAsync();
                return null;
            }

            var responseBody = await response.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(responseBody);
            if (doc.RootElement.TryGetProperty("choices", out var choices) && choices.GetArrayLength() > 0)
            {
                var firstChoice = choices[0];
                if (firstChoice.TryGetProperty("message", out var message) && message.TryGetProperty("content", out var txt))
                {
                    return txt.GetString();
                }
            }

            return null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// 将模型配置持久化到用户目录。API Base URL 和模型名称明文保存，
    /// API Key 使用 Windows DPAPI（用户范围）加密后 Base64 编码写入文件。
    /// 新保存的配置采用 DPAPI，不再采用固定盐值和固定 IV 的 AES 方案。
    /// </summary>
    /// <param name="apiBaseUrl">模型 API 的基础 URL（明文）。</param>
    /// <param name="apiKey">模型 API 的密钥。</param>
    /// <param name="modelName">模型名称。</param>
    public static void SaveConfiguration(string apiBaseUrl, string apiKey, string modelName)
    {
        // 确保目录存在：%APPDATA%\NX AI Assistant
        string dir = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        string appDir = Path.Combine(dir, "NX AI Assistant");
        Directory.CreateDirectory(appDir);

        string configPath = Path.Combine(appDir, "model-config.json");

        // 使用 DPAPI 加密 API Key（当前 Windows 用户范围，随机生成的数据密钥）
        string encryptedKey = Dpapi.Protect(apiKey);

        // 构建 JSON 配置
        var config = new
        {
            apiBaseUrl = apiBaseUrl,
            modelName = modelName,
            encryptedApiKey = encryptedKey
        };
        string json = JsonSerializer.Serialize(config);

        // 写入文件
        File.WriteAllText(configPath, json, Encoding.UTF8);
    }

    /// <summary>
    /// 从配置文件加载并解密 API Key。
    /// 返回 (apiBaseUrl, apiKey, modelName) 三元组。
    /// 当文件不存在、格式错误或解密失败时返回 null。
    /// 按以下优先级尝试解密，仅在通过合理性校验后才进行迁移：
    ///   1. Windows DPAPI (CurrentUser) — 新配置格式
    ///   2. 旧 AES-256 CBC 方案 (固定盐值/IV, Rfc2898DeriveBytes, 10000 次迭代, SHA256) — 迁移旧配置
    ///     — 若解密出的文本不通过 IsReasonableApiKey 校验，视为解密失败，继续尝试下一级
    ///   3. Base64 直接明文 — 兼容旧版无加密 Base64
     ///     — 若解码出的文本不通过 IsReasonableApiKey 校验，视为明文无效，继续尝试下一级（此处无下一级，返回 null 并保留原文件）
    /// 仅在解密成功且通过合理性校验后，才将密钥迁移到 DPAPI 格式并重写文件。
    /// 迁移失败时保留原始配置文件，不得静默覆盖，也不得将密文误认作有效 API Key。
    /// 如果配置文件缺少格式标识，无法可靠区分旧 AES 密文与随机 Base64 数据，将优先保留原数据不做修改。
    /// </summary>
    /// <returns>成功返回 (apiBaseUrl, apiKey, modelName)；失败返回 null。</returns>
    public static (string apiBaseUrl, string apiKey, string modelName)? LoadConfiguration()
    {
        // 尝试读取配置文件：%APPDATA%\NX AI Assistant\model-config.json
        string dir = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        string configPath = Path.Combine(dir, "NX AI Assistant", "model-config.json");

        if (!File.Exists(configPath))
        {
            return null; // 文件不存在，视为未配置
        }

        string json;
        try
        {
            json = File.ReadAllText(configPath, Encoding.UTF8);
        }
        catch
        {
            return null; // 读取失败
        }

        // 解析 JSON
        var config = JsonSerializer.Deserialize<ConfigData>(json);
        if (config == null) return null;

        string apiBaseUrl = config.apiBaseUrl;
        string modelName = config.modelName;

        // 第 1 优先级：Windows DPAPI (CurrentUser) — 新配置格式
        string? decryptedKey = Dpapi.Unprotect(config.encryptedApiKey);
        if (decryptedKey != null && IsReasonableApiKey(decryptedKey))
        {
            // 成功解密（新格式），直接返回
            return (apiBaseUrl, decryptedKey, modelName);
        }

        // 第 2 优先级：旧 AES-256 CBC 方案 (固定盐值/IV, Rfc2898DeriveBytes, 10000 次迭代, SHA256)
        // 仅在解密出的文本通过合理性校验后才视为有效旧配置并迁移
        decryptedKey = DecryptKeyFromAesLegacy(config.encryptedApiKey);
        if (decryptedKey != null)
        {
            // 旧 AES 解密成功并通过校验，迁移到 DPAPI 格式并重写配置文件
            try
            {
                SaveConfiguration(apiBaseUrl, decryptedKey, modelName);
            }
            catch { /* 写入失败时仍返回已解密的明文（已通过校验），避免完全丢失功能 */ }
            // 迁移完成，返回明文字符串；下次调用 LoadConfiguration 将命中 DPAPI 路径
            return (apiBaseUrl, decryptedKey, modelName);
        }

        // 第 3 优先级：Base64 直接明文（旧版无加密 Base64 格式）
        // 仅在解码出的文本通过 IsReasonableApiKey 校验后才视为有效明文并迁移
        try
        {
            byte[] raw = Convert.FromBase64String(config.encryptedApiKey);
            string oldKey = Encoding.UTF8.GetString(raw);
            if (IsReasonableApiKey(oldKey))
            {
                // 通过校验，迁移到新 DPAPI 格式并重写配置文件
                try
                {
                    SaveConfiguration(apiBaseUrl, oldKey, modelName);
                }
                catch { /* 写入失败时仍返回明文，避免完全丢失功能 */ }
                return (apiBaseUrl, oldKey, modelName);
            }
            // 校验未通过，视为无效明文，继续返回 null 并保留原文件
        }
        catch { } // Base64 解码失败或校验未通过，所有迁移失败，保留原配置文件不做任何修改，返回 null

        // 所有解密方式均未通过校验，返回 null，原始配置文件保持不变
        return null;
    }

    /// <summary>
    /// 从当前配置中读取设置。若未配置返回 null。
    /// </summary>
    /// <returns>配置三元组或 null。</returns>
    public (string apiBaseUrl, string apiKey, string modelName)? GetCurrentConfiguration()
    {
        return LoadConfiguration();
    }

    /// <summary>
    /// 用于 JSON 序列化的配置数据模型。
    /// </summary>
    private class ConfigData
    {
        public string apiBaseUrl { get; set; } = "";
        public string modelName { get; set; } = "";
        public string encryptedApiKey { get; set; } = "";
    }

    /// <summary>
    /// 使用旧 AES-256 CBC 方案（固定盐值/IV、Rfc2898DeriveBytes）解密密钥。
    /// 仅用于迁移旧配置；新配置必须使用 DPAPI。
    /// 解密失败返回 null。
    /// </summary>
    /// <param name="encryptedKey">Base64 编码的旧加密密钥。</param>
    /// <returns>明文字符串，解密失败返回 null。</returns>
    private static string? DecryptKeyFromAesLegacy(string encryptedKey)
    {
        try
        {
            byte[] encryptedBytes = Convert.FromBase64String(encryptedKey);

            // 固定的盐值和向量（与旧版 SaveConfiguration 完全一致）
            byte[] salt = new byte[] { 0x12, 0x34, 0x56, 0x78, 0x9a, 0xbc, 0xde, 0xf0 };
            byte[] iv = new byte[] { 0x01, 0x23, 0x45, 0x67, 0x89, 0xab, 0xcd, 0xef };

            using var deriveBytes = new Rfc2898DeriveBytes("", salt, 10000, HashAlgorithmName.SHA256);
            byte[] keyBytes = deriveBytes.GetBytes(32);    // AES-256 需要 256 位密钥
            byte[] ivBytes = deriveBytes.GetBytes(16);     // AES 块大小为 128 位

            using var aes = Aes.Create();
            aes.Key = keyBytes;
            aes.IV = ivBytes;
            aes.Mode = CipherMode.CBC;
            aes.Padding = PaddingMode.PKCS7;

            ICryptoTransform decryptor = aes.CreateDecryptor();
            string? decryptedKey;
            using (var ms = new MemoryStream())
            {
                using (var cs = new CryptoStream(ms, decryptor, CryptoStreamMode.Read))
                {
                    using var reader = new StreamReader(cs);
                    decryptedKey = reader.ReadToEnd();
                }
            }
            // 只有当解密出的文本通过合理性校验后，才认为是有效密钥
            return IsReasonableApiKey(decryptedKey) ? decryptedKey : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// 对 API Key 进行合理性校验：非空、长度在 1-256、仅含可打印 ASCII（不含控制字符）。
    /// 用于区分“有效密钥”与 Base64 解码的随机垃圾/明文伪像。
    /// </summary>
    private static bool IsReasonableApiKey(string? key)
    {
        if (string.IsNullOrWhiteSpace(key)) return false;
        if (key.Length > 256) return false;
        foreach (char c in key)
        {
            // 控制字符（0x00-0x1F, 0x7F）会导密钥被视为无效
            if (c < 0x20 || c == 0x7F) return false;
        }
        return true;
    }

    private static string EscapeJsonString(string s)
    {
        return s.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n").Replace("\r", "\\r");
    }}
