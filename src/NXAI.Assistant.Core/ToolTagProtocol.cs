using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace NXAI.Assistant.Core;

/// <summary>
/// Restricted adapter for model-specific tool-call tags arriving in
/// message.content (observed with reasoning models behind OpenAI-compatible
/// gateways, which sometimes emit tool calls as XML-ish tags in the text
/// instead of the standard tool_calls array). The adapter converts a
/// COMPLETE, well-formed invoke block into the same typed shape the
/// structured path produces — nothing else.
///
/// Rules:
///   - the block is recognized by its generic shape: an opening tag
///     "&lt;invoke&gt;"-style element and per-parameter child elements with a
///     name attribute and text value. The literal marker strings live in
///     <see cref="Markers"/> so no layer of this codebase hardcodes them;
///   - every open tag must close; a stray opener is a protocol error, never
///     treated as assistant content;
///   - at most one invoke block per message (the controller's round limit
///     already enforces at most one execution per turn);
///   - the function name must match ^[A-Za-z0-9_.-]+$ — registry, schema
///     and authorization checks happen downstream exactly as for structured
///     tool_calls;
///   - parameters are a strict name→value map: duplicate keys, empty names,
///     missing '=' or unclosed tags reject the whole block;
///   - the adapter never executes anything, grants no permissions, and
///     never treats plain assistant text as a modeling command.
///
/// Parsing failure returns a reason so the caller can show a concise
/// compatibility notice instead of rendering raw control markers.
/// </summary>
internal static partial class ToolTagProtocol
{
    /// <summary>
    /// The model-specific markers, assembled from fragments so that no
    /// literal control sequence appears in this source file (a raw marker
    /// here would leak into this assistant's own tool-calling stream and be
    /// stripped before it reaches disk).
    /// </summary>
    internal static class Markers
    {
        // Built from fragments: "an"+"tml"+":" gives the tag namespace, so
        // the source never contains the complete literal marker.
        private const string P = "an" + "tm" + "l:";
        private const string Invoke = "inv" + "oke";
        private const string Parameter = "par" + "ameter";

        /// <summary>Opening invoke tag (no closing '>' — name may carry attributes).</summary>
        internal static readonly string InvokeOpen = "<" + P + Invoke;
        /// <summary>Closing invoke tag.</summary>
        internal static readonly string InvokeClose = "</" + P + Invoke + ">";
        /// <summary>Parameter open tag start: "&lt;prefix:parameter ". The name
        /// attribute follows.</summary>
        internal static readonly string ParamOpen = "<" + P + Parameter + " ";
        /// <summary>Parameter attribute marker: name="</summary>
        internal const string NameAttr = "name=\"";
        /// <summary>Closing parameter tag.</summary>
        internal static readonly string ParamClose = "</" + P + Parameter + ">";
    }

    [GeneratedRegex("^[A-Za-z0-9_.-]+$")]
    private static partial Regex NamePattern();

    public sealed record TagToolCall(string FunctionName, string ArgumentsJson);

    /// <summary>
    /// Result of scanning assistant content: either clean text (no complete
    /// or partial invoke blocks), or one parsed tool call, or a protocol
    /// error describing why the block cannot be trusted.
    /// </summary>
    public sealed record ScanResult(
        string? CleanText, TagToolCall? ToolCall, string? ProtocolError);

    /// <summary>True when the content contains any tool-tag marker at all.</summary>
    public static bool ContainsToolMarker(string content) =>
        content.Contains(Markers.InvokeOpen, StringComparison.Ordinal);

    /// <summary>
    /// Normalizes one raw call for plan construction: canonical tool name
    /// (closed alias table), parameter-name aliases, direction-value aliases.
    /// Used by ModelingPlanFactory so structured and tag-protocol calls take
    /// the identical normalization path before Schema parsing.
    /// </summary>
    public static (string CanonicalName, string ArgumentsJson) NormalizeCall(
        string functionName, string argumentsJson, out string? error)
    {
        error = null;
        try
        {
            using var doc = JsonDocument.Parse(argumentsJson);
            var root = doc.RootElement.Clone();
            if (root.ValueKind != JsonValueKind.Object)
            {
                error = $"工具调用参数必须是 JSON 对象（{functionName}）。";
                return (functionName, argumentsJson);
            }

            var parameters = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var prop in root.EnumerateObject())
            {
                parameters[prop.Name] = prop.Value.ValueKind == JsonValueKind.String
                    ? prop.Value.GetString() ?? ""
                    : prop.Value.GetRawText();
            }

            var (canonical, normalized, normalizeError) = NormalizeArguments(functionName, parameters);
            if (normalizeError != null)
            {
                error = normalizeError;
                return (canonical, argumentsJson);
            }

            var json = new StringBuilder("{");
            bool first = true;
            foreach (var (key, value) in normalized)
            {
                if (!first)
                    json.Append(',');
                first = false;
                json.Append(JsonSerializer.Serialize(key)).Append(':');
                json.Append(JsonSerializer.Serialize(value));
            }
            json.Append('}');
            return (canonical, json.ToString());
        }
        catch (JsonException)
        {
            // Malformed JSON: pass through; the typed parser reports it.
            return (functionName, argumentsJson);
        }
    }

    /// <summary>
    /// Scans assistant content for exactly one complete invoke block.
    /// Multiple blocks, a stray open tag, malformed parameters or an unknown
    /// function-name shape are all protocol errors. Text outside the block
    /// (trimmed; null when empty) is returned as CleanText.
    /// </summary>
    public static ScanResult Scan(string content)
    {
        int open = content.IndexOf(Markers.InvokeOpen, StringComparison.Ordinal);
        if (open < 0)
            return new ScanResult(content, null, null);

        string before = content[..open];
        int afterOpen = open + Markers.InvokeOpen.Length;
        int close = content.IndexOf(Markers.InvokeClose, afterOpen, StringComparison.Ordinal);
        if (close < 0)
        {
            return new ScanResult(null, null,
                "模型返回了无法识别的工具调用格式（工具调用块未正常闭合），未执行建模操作。" +
                "请检查当前模型的工具调用兼容性，或重新发送请求。");
        }

        if (content.IndexOf(Markers.InvokeOpen, close + Markers.InvokeClose.Length, StringComparison.Ordinal) >= 0)
        {
            return new ScanResult(null, null,
                "模型在一次回复中返回了多个工具调用块；当前版本每条消息只支持一个，未执行建模操作。");
        }

        var parse = ParseInvokeBlock(content[afterOpen..close]);
        if (parse.Error != null)
            return new ScanResult(null, null, parse.Error);

        string trailing = content[(close + Markers.InvokeClose.Length)..];
        string cleanText = (before + trailing).Trim();
        return new ScanResult(
            cleanText.Length > 0 ? cleanText : null,
            new TagToolCall(parse.FunctionName!, parse.ArgumentsJson!),
            null);
    }

    private static (string? FunctionName, string? ArgumentsJson, string? Error) ParseInvokeBlock(
        string inner)
    {
        var parameters = new Dictionary<string, string>(StringComparer.Ordinal);
        int cursor = 0;
        string? functionName = null;

        int firstParam = inner.IndexOf(Markers.ParamOpen, StringComparison.Ordinal);
        if (firstParam < 0)
        {
            return (null, null,
                "模型返回了无法识别的工具调用格式（工具调用块中没有参数），未执行建模操作。");
        }
        functionName = inner[..firstParam].Trim();
        if (functionName.Length == 0)
        {
            return (null, null,
                "模型返回了无法识别的工具调用格式（缺少工具名），未执行建模操作。");
        }
        if (!NamePattern().IsMatch(functionName))
        {
            return (null, null,
                "模型返回了无法识别的工具调用格式（工具名不合法），未执行建模操作。");
        }
        cursor = firstParam;

        while (cursor < inner.Length)
        {
            if (!inner.AsSpan(cursor).StartsWith(Markers.ParamOpen))
            {
                string junk = inner[cursor..].Trim();
                if (junk.Length > 0)
                {
                    return (null, null,
                        "模型返回了无法识别的工具调用格式（参数区存在无法解析的文本），未执行建模操作。");
                }
                break;
            }

            // "<prefix:parameter name=\"key\">value</prefix:parameter>"
            int attrStart = inner.IndexOf(Markers.NameAttr, cursor, StringComparison.Ordinal);
            if (attrStart < 0 || attrStart > cursor + Markers.ParamOpen.Length + 64)
            {
                return (null, null,
                    "模型返回了无法识别的工具调用格式（参数缺少 name 属性），未执行建模操作。");
            }
            int keyStart = attrStart + Markers.NameAttr.Length;
            int keyEnd = inner.IndexOf('"', keyStart);
            int tagValueStart = keyEnd < 0 ? -1 : inner.IndexOf('>', keyEnd);
            if (keyEnd < 0 || tagValueStart < 0)
            {
                return (null, null,
                    "模型返回了无法识别的工具调用格式（参数属性不完整），未执行建模操作。");
            }
            string key = inner[keyStart..keyEnd].Trim();
            if (key.Length == 0)
            {
                return (null, null,
                    "模型返回了无法识别的工具调用格式（参数名为空），未执行建模操作。");
            }
            if (parameters.ContainsKey(key))
            {
                return (null, null,
                    $"模型返回了无法识别的工具调用格式（参数 {key} 重复），未执行建模操作。");
            }

            int valueEnd = inner.IndexOf(Markers.ParamClose, tagValueStart + 1, StringComparison.Ordinal);
            if (valueEnd < 0)
            {
                return (null, null,
                    "模型返回了无法识别的工具调用格式（参数未正常闭合），未执行建模操作。");
            }
            parameters[key] = inner[(tagValueStart + 1)..valueEnd];
            cursor = valueEnd + Markers.ParamClose.Length;
        }

        // Convert to a JSON object string so the typed parsers downstream
        // see the same shape as a structured tool call's arguments. Tool
        // aliases, parameter-name aliases and known enum-value spellings
        // are normalized here, at this single explicit point — registry,
        // schema and authorization checks stay downstream, unchanged.
        var (canonicalName, normalized, normalizeError) =
            NormalizeArguments(functionName, parameters);
        if (normalizeError != null)
            return (null, null, normalizeError);

        var json = new StringBuilder("{");
        bool first = true;
        foreach (var (key, value) in normalized)
        {
            if (!first)
                json.Append(',');
            first = false;
            json.Append(JsonSerializer.Serialize(key)).Append(':');
            json.Append(JsonSerializer.Serialize(value));
        }
        json.Append('}');
        return (canonicalName, json.ToString(), null);
    }

    // ------------------------------------------------------------ normalization

    /// <summary>
    /// Explicit, closed alias table for tool names observed from real model
    /// output. An entry is added only after its semantics were verified
    /// against the registered tool's schema — never by fuzzy matching.
    /// </summary>
    private static readonly Dictionary<string, string> ToolAliases =
        new(StringComparer.Ordinal)
        {
            // Live model output (Phase 7.3.1 screenshot) calls the registered
            // create_extrude tool by the short name "extrude".
            ["extrude"] = CreateExtrudeTool.Name,
        };

    /// <summary>
    /// Explicit direction-value spellings accepted from tag-protocol
    /// arguments, mapped to the create_extrude schema's values
    /// (normal|reverse). Anything else passes through untouched and is
    /// rejected by the typed parser — the model cannot invent axes here;
    /// the schema enum and parser remain the authority.
    /// </summary>
    private static readonly Dictionary<string, string> ExtrudeDirectionAliases =
        new(StringComparer.Ordinal)
        {
            // "positive_z": the schema's "normal" direction IS the sketch
            // plane normal (+Z for the XY-plane sketches this tool creates);
            // semantics verified against CreateExtrudeTool's documented
            // contract before admission.
            ["positive_z"] = "normal",
        };

    /// <summary>
    /// Explicit parameter-name aliases per canonical tool, admitted only
    /// after checking the registered tool's schema. One alias maps to
    /// exactly one schema field; conflicting aliases for the same field
    /// within one call are rejected downstream as duplicate keys after
    /// normalization. Never a fuzzy or positional rewrite.
    /// </summary>
    private static readonly Dictionary<string, Dictionary<string, string>> ParameterAliases =
        new(StringComparer.Ordinal)
        {
            // Live model output (Phase 7.3.2) sends the sketch reference as
            // "sketch" — the create_extrude schema field is "sketch_ref".
            [CreateExtrudeTool.Name] = new(StringComparer.Ordinal)
            {
                ["sketch"] = "sketch_ref",
            },
        };

    private static (string CanonicalName, List<KeyValuePair<string, string>> Arguments, string? Error) NormalizeArguments(
        string functionName,
        Dictionary<string, string> parameters)
    {
        var canonical = ToolAliases.TryGetValue(functionName, out var mapped)
            ? mapped
            : functionName;

        var paramAliases = ParameterAliases.TryGetValue(canonical, out var table)
            ? table
            : null;

        var result = new List<KeyValuePair<string, string>>(parameters.Count);
        foreach (var (key, value) in parameters)
        {
            if (canonical == CreateExtrudeTool.Name &&
                key == "direction" &&
                ExtrudeDirectionAliases.TryGetValue(value.Trim(), out var direction))
            {
                result.Add(new(key, direction));
                continue;
            }

            var normalizedKey = key;
            if (paramAliases != null && paramAliases.TryGetValue(key, out var schemaKey))
            {
                // One alias → one schema field. If the call ALSO carries the
                // canonical name, do not silently pick one — reject.
                if (parameters.ContainsKey(schemaKey))
                {
                    return (canonical, result,
                        $"模型返回了无法识别的工具调用格式（参数 {key} 与 {schemaKey} 冲突），未执行建模操作。");
                }
                normalizedKey = schemaKey;
            }
            result.Add(new(normalizedKey, value));
        }
        return (canonical, result, null);
    }
}
