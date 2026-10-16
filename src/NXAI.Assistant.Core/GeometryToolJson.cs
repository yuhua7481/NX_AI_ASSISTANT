using System.Globalization;
using System.Text.Json;

namespace NXAI.Assistant.Core;

/// <summary>
/// Shared JSON-parsing helpers for geometry tool argument parsers: unit
/// handling, finite-number reads and unknown-field rejection. All linear
/// values are normalized to millimeters.
/// </summary>
internal static class GeometryToolJson
{
    public static readonly string[] SupportedUnits = { "mm", "cm", "m", "inch", "in" };

    public static double UnitToMillimeters(string unit) => unit switch
    {
        "mm" => 1.0,
        "cm" => 10.0,
        "m" => 1000.0,
        "inch" or "in" => 25.4,
        _ => 1.0,
    };

    /// <summary>Parses and validates the unit field; must be present.</summary>
    public static string? ReadUnit(JsonElement root, out string unit, out double factor)
    {
        factor = 1.0;
        if (!root.TryGetProperty("unit", out var unitElement))
        {
            unit = "";
            return "缺少必填参数：unit。";
        }
        if (unitElement.ValueKind != JsonValueKind.String)
        {
            unit = "";
            return "unit 必须是字符串。";
        }
        unit = unitElement.GetString()!.Trim().ToLowerInvariant();
        if (Array.IndexOf(SupportedUnits, unit) < 0)
        {
            return $"不支持的单位：{unit}。支持 mm、cm、m、inch。";
        }
        factor = UnitToMillimeters(unit);
        return null;
    }

    /// <summary>Rejects fields not in the tool's known set.</summary>
    public static string? RejectUnknownFields(
        JsonElement root, string toolName, HashSet<string> knownFields)
    {
        foreach (var property in root.EnumerateObject())
        {
            if (!knownFields.Contains(property.Name))
            {
                return $"{toolName} 不支持参数字段：{property.Name}。";
            }
        }
        return null;
    }

    public static bool TryReadFiniteNumber(
        JsonElement root, string name, out double value, out string? error)
    {
        error = null;
        value = 0;
        if (!root.TryGetProperty(name, out var element))
        {
            error = $"缺少必填参数：{name}。";
            return false;
        }
        if (element.ValueKind == JsonValueKind.Number)
        {
            double number = element.GetDouble();
            if (!double.IsFinite(number))
            {
                error = $"参数 {name} 必须是有限数字。";
                return false;
            }
            value = number;
            return true;
        }
        // String-encoded numbers are tolerated (common model behaviour) but
        // must still parse to a finite double.
        if (element.ValueKind == JsonValueKind.String)
        {
            if (double.TryParse(element.GetString(), NumberStyles.Float,
                    CultureInfo.InvariantCulture, out double parsed))
            {
                value = parsed;
                return true;
            }
            error = $"参数 {name} 不是可解析的数字。";
            return false;
        }
        error = $"参数 {name} 必须是数字。";
        return false;
    }
}
