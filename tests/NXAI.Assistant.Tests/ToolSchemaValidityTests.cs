using System.Text.Json;
using NXAI.Assistant.Core;
using Xunit;

namespace NXAI.Assistant.Tests;

/// <summary>
/// Phase 9.2.1 schema-validity regression tests: EVERY registered tool's
/// ParametersJsonSchema must parse with System.Text.Json — the exact parser
/// ModelService uses when building the outbound "tools" payload (a
/// JsonException there surfaces to the user as "工具 X 的 JSON Schema 不是
/// 合法 JSON（内部错误）" and blocks the WHOLE request, even for unrelated
/// tools like create_block). The 2026-10-23 live failure: the standard-hole
/// template wrapped __REQ__/__SIZES__ placeholders in quotes while the
/// substituted fragments carried their own quotes, producing
/// [""size", …] — invalid for all FOUR standard-hole tools.
/// </summary>
public class ToolSchemaValidityTests
{
    /// <summary>All tools the registry advertises, from the real registry —
    /// a new tool must be added there first, which makes this test fail if
    /// its schema is malformed (fail-closed registration coverage).</summary>
    private static IReadOnlyList<ToolDefinition> AllRegisteredTools()
    {
        var registry = CreateBlockTool.CreateRegistry();
        Assert.Equal(18, registry.All.Count);
        return registry.All;
    }

    [Fact]
    public void EveryRegisteredToolSchema_ParsesWithModelServiceParser()
    {
        var failures = new List<string>();
        foreach (var tool in AllRegisteredTools())
        {
            try
            {
                // Same parse path as ModelService.SendAsync's tool loop.
                using var schemaDoc = JsonDocument.Parse(tool.ParametersJsonSchema);
                if (schemaDoc.RootElement.ValueKind != JsonValueKind.Object)
                    failures.Add($"{tool.Name}: schema root is {schemaDoc.RootElement.ValueKind}, expected object");
            }
            catch (JsonException ex)
            {
                failures.Add($"{tool.Name}: JsonException {ex.Message}");
            }
        }
        Assert.True(failures.Count == 0,
            "Invalid tool schemas:\n" + string.Join("\n", failures));
    }

    [Fact]
    public void EveryRegisteredToolSchema_RoundTripsPreservingStructure()
    {
        foreach (var tool in AllRegisteredTools())
        {
            using var doc = JsonDocument.Parse(tool.ParametersJsonSchema);
            var root = doc.RootElement;

            Assert.True(root.TryGetProperty("type", out var typeEl) &&
                typeEl.GetString() == "object", $"{tool.Name}: missing type=object");
            Assert.True(root.TryGetProperty("properties", out var props) &&
                props.ValueKind == JsonValueKind.Object, $"{tool.Name}: missing properties object");
            Assert.True(root.TryGetProperty("required", out var required) &&
                required.ValueKind == JsonValueKind.Array, $"{tool.Name}: missing required array");

            // Every required name must exist as a property with a type.
            foreach (var req in required.EnumerateArray())
            {
                string name = req.GetString()!;
                Assert.True(props.TryGetProperty(name, out var prop),
                    $"{tool.Name}: required '{name}' not in properties");
                Assert.True(prop.TryGetProperty("type", out _),
                    $"{tool.Name}: property '{name}' has no type");
            }

            // additionalProperties=false everywhere (the parser's
            // RejectUnknownFields mirrors this contract).
            Assert.True(root.TryGetProperty("additionalProperties", out var ap) &&
                ap.GetBoolean() == false, $"{tool.Name}: additionalProperties must be false");
        }
    }

    [Fact]
    public void StandardHoleSchemas_EnumAndDepthFields_CorrectAfterParse()
    {
        var expectedEnum = new[]
        {
            "M12", "M10", "M8", "M6", "M5", "M4", "M3", "M2", "M1.6",
        };
        var cases = new (string Schema, string Name, string[] DepthFields)[]
        {
            (StandardHoleToolContract.ThreadedSchema, StandardHoleToolContract.ThreadedName,
                new[] { "hole_depth", "thread_depth" }),
            (StandardHoleToolContract.ClearanceSchema, StandardHoleToolContract.ClearanceName,
                new[] { "hole_depth" }),
            (StandardHoleToolContract.CounterboredSchema, StandardHoleToolContract.CounterboredName,
                Array.Empty<string>()),
            (StandardHoleToolContract.CountersunkSchema, StandardHoleToolContract.CountersunkName,
                Array.Empty<string>()),
        };
        foreach (var (schema, name, depthFields) in cases)
        {
            using var doc = JsonDocument.Parse(schema);
            var props = doc.RootElement.GetProperty("properties");

            var sizeEnum = props.GetProperty("size").GetProperty("enum")
                .EnumerateArray().Select(e => e.GetString()).ToArray();
            Assert.Equal(expectedEnum, sizeEnum);

            foreach (var field in depthFields)
                Assert.True(props.TryGetProperty(field, out _),
                    $"{name}: expected property {field}");
            // Depth fields must appear EXACTLY as listed: no thread_depth on
            // the clearance family, no depth overrides on head families.
            foreach (var prop in props.EnumerateObject())
            {
                if (prop.Name is "hole_depth" or "thread_depth")
                    Assert.Contains(prop.Name, depthFields);
            }

            var required = doc.RootElement.GetProperty("required")
                .EnumerateArray().Select(e => e.GetString()!).ToArray();
            // Phase 9.3: placement_face_ref is conditional (required only in
            // face-center mode) — no longer in the schema's required list.
            Assert.Equal(new[] { "size", "unit" }, required);
            Assert.True(doc.RootElement.GetProperty("properties").TryGetProperty(
                "placement_mode", out _));
        }
    }

    [Fact]
    public void ThreadedSchema_NoLongerProducesDoubleQuoteError()
    {
        // The exact live failure signature: JsonDocument.Parse must succeed
        // where the 2026-10-23 build threw "Expecting ',' delimiter" from
        // [""size", …].
        var exception = Record.Exception(() =>
            JsonDocument.Parse(StandardHoleToolContract.ThreadedSchema));
        Assert.Null(exception);
        Assert.DoesNotContain("\"\"", StandardHoleToolContract.ThreadedSchema);
        Assert.DoesNotContain("[\"\",", StandardHoleToolContract.ThreadedSchema);
    }

    [Fact]
    public void CreateBlockSchema_UnchangedAndValid()
    {
        // The block contract the user's live prompt targets must stay intact.
        using var doc = JsonDocument.Parse(CreateBlockTool.ParametersJsonSchema);
        var required = doc.RootElement.GetProperty("required")
            .EnumerateArray().Select(e => e.GetString()!).ToArray();
        Assert.Contains("length", required);
        Assert.Contains("width", required);
        Assert.Contains("height", required);
        Assert.Contains("unit", required);
    }
}
