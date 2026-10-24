using System.Globalization;
using System.Text.Json;
using System.Xml;

namespace NXAI.Assistant.Core;

/// <summary>
/// The four standard-hole families the product supports, with the
/// project-formal Chinese names (fixed by the user 2026-10-23): 螺纹孔
/// (threaded), 螺纹间隙孔 (simple screw clearance), 沉头孔 (counterbored,
/// ISO 4762, cylindrical flat-bottom seat), 埋头孔 (countersunk, ISO 10642,
/// conical). Each maps to one NX hole form + one standard-table match key
/// family.
/// </summary>
public enum StandardHoleFamily
{
    Threaded,
    SimpleClearance,
    Counterbored,
    Countersunk,
}

/// <summary>
/// One matched record from an NX modeling-standards XML table (raw string
/// fields, exactly as stored — no numeric rounding at this layer).
/// </summary>
public sealed record StandardHoleRecord(IReadOnlyDictionary<string, string> Fields)
{
    public string? Field(string name) =>
        Fields.TryGetValue(name, out var v) ? v : null;
}

/// <summary>
/// Failure modes of standard-table resolution, each with a user-facing
/// message. The tool must NEVER invent values when a record cannot be
/// matched — every miss is a structured rejection.
/// </summary>
public sealed class StandardHoleLookupException : Exception
{
    public StandardHoleLookupException(string message) : base(message) { }
}

/// <summary>
/// Resolves (family, size) to the UNIQUE standard record by exact-match
/// keys, replicating the live-proven probe match rules:
///   - Threaded: NX_Thread_Standard.xml, Standard='Metric Coarse' AND
///     MajorDiameter(numeric)=nominal AND RadialEngage='0.75' — exactly one.
///   - Clearance families: NX_Screw_Clearance_Hole_Standard.xml,
///     Form + ScrewType + Size + Fit + Standard — exactly one (ISO Close).
/// Zero matches → "no such standard record"; multiple → "ambiguous".
/// Threaded holes also enforce thread-depth less-than hole-depth on the
/// record's own reference values (the probe's gate); the executor's user
/// depths are validated separately.
/// </summary>
public static class StandardHoleTable
{
    public const string DefaultFit = "Close (H12)";
    public const string DefaultStandard = "ISO";

    public const string ThreadStandardFile = "NX_Thread_Standard.xml";
    public const string ClearanceStandardFile = "NX_Screw_Clearance_Hole_Standard.xml";

    /// <summary>The nine metric coarse sizes the product must support.</summary>
    public static readonly IReadOnlyList<string> SupportedSizes = new[]
    {
        "M12", "M10", "M8", "M6", "M5", "M4", "M3", "M2", "M1.6",
    };

    /// <summary>The nine nominal diameters, index-aligned with SupportedSizes.</summary>
    public static readonly IReadOnlyList<double> SupportedNominals = new[]
    {
        12.0, 10.0, 8.0, 6.0, 5.0, 4.0, 3.0, 2.0, 1.6,
    };

    /// <summary>
    /// Match-key family per form: ScrewType pins the EXACT ISO series so a
    /// size can never silently resolve to another family's record.
    /// </summary>
    private static readonly IReadOnlyDictionary<StandardHoleFamily, (string Form, string ScrewType)>
        ClearanceMatchKeys = new Dictionary<StandardHoleFamily, (string, string)>
        {
            [StandardHoleFamily.SimpleClearance] = ("Simple", "General Screw Clearance"),
            [StandardHoleFamily.Counterbored] = ("Counterbored", "Socket Head, 4762"),
            [StandardHoleFamily.Countersunk] = ("Countersunk", "Socket Flat Head, 10642"),
        };

    /// <summary>
    /// Resolves the unique standard record. Throws
    /// <see cref="StandardHoleLookupException"/> with a user-facing Chinese
    /// message when the table file is unreadable, the record is missing, or
    /// the match is ambiguous. Pure XML-text logic — unit-testable offline
    /// with injected table text.
    /// </summary>
    public static StandardHoleRecord Resolve(
        StandardHoleFamily family, string size,
        Func<StandardHoleFamily, string> loadTableXml,
        string fit = DefaultFit, string standard = DefaultStandard)
    {
        if (string.IsNullOrWhiteSpace(size))
            throw new StandardHoleLookupException("孔规格不能为空。");
        string normSize = NormalizeSize(size);
        if (!SupportedSizes.Contains(normSize))
            throw new StandardHoleLookupException(
                $"不支持的孔规格：{size}。当前支持的规格：" + string.Join("、", SupportedSizes) + "。");

        // 2026-10-24 Phase 10.3 P1: a fully-qualified size "M6x1.0" is
        // accepted ONLY when its pitch matches the standard table's record
        // for the nominal (the real table: Size="M6 x 1.0" Pitch="1"). A
        // WRONG pitch ("M6x1.25") still refuses — never silently map to
        // the nominal. Threaded-table only: clearance families have no
        // pitch, so "M6x1.0" there stays unsupported.
        string? requestedPitch = ExtractRequestedPitch(size);
        if (requestedPitch != null)
        {
            if (family != StandardHoleFamily.Threaded)
                throw new StandardHoleLookupException(
                    $"孔规格 {size} 带螺距标注，但 {family} 孔不使用螺距。请直接使用 {normSize}。");
            double requested = double.Parse(requestedPitch, NumberStyles.Float,
                CultureInfo.InvariantCulture);
            double standardPitch = GetThreadedPitch(loadTableXml, normSize);
            if (standardPitch <= 0 || Math.Abs(standardPitch - requested) > 1e-9)
                throw new StandardHoleLookupException(
                    $"孔规格 {size} 与标准不符（{normSize} 的标准螺距为 " +
                    (standardPitch > 0 ? standardPitch.ToString("0.0###") : "未知") +
                    $"）。请使用 {normSize} 或 {normSize}x{{标准螺距}}。");
        }

        string xml;
        try
        {
            xml = loadTableXml(family);
        }
        catch (StandardHoleLookupException) { throw; }
        catch (Exception ex)
        {
            throw new StandardHoleLookupException($"读取孔标准表失败：{ex.GetType().Name}：{ex.Message}");
        }

        if (family == StandardHoleFamily.Threaded)
        {
            int idx = -1;
            for (int i = 0; i < SupportedSizes.Count; i++)
            {
                if (SupportedSizes[i] == normSize) { idx = i; break; }
            }
            if (idx < 0)
                throw new StandardHoleLookupException($"不支持的孔规格：{normSize}。");
            return ResolveThreaded(xml, normSize, SupportedNominals[idx]);
        }
        return ResolveClearance(xml, family, normSize, fit, standard);
    }

    /// <summary>Canonical size token: "m6"→"M6", "M6.0"→"M6", "M1.60"→"M1.6".
    /// Phase 10.3 P1: "M6x1.0" normalizes to "M6" — the pitch suffix is
    /// split off here; its correctness is verified against the standard
    /// table in Lookup (a wrong pitch still refuses there).</summary>
    public static string NormalizeSize(string size)
    {
        string s = size.Trim().ToUpperInvariant();
        if (!s.StartsWith("M"))
            return s;
        // Split "M6 x 1.0" / "M6x1.0" → nominal "M6" + pitch "1.0".
        int x = s.IndexOf('X');
        if (x > 1)
            s = s[..x].Trim();
        string num = s[1..];
        if (double.TryParse(num, NumberStyles.Float, CultureInfo.InvariantCulture, out double v) &&
            v > 0)
        {
            // Deterministic: integer values → no decimal part; fractional
            // values → the shortest exact decimal ("1.6" stays "1.6",
            // "1.60"→"1.6"). InvariantCulture; never locale-dependent.
            if (v == Math.Floor(v) && v < 1e15)
                return "M" + ((long)v).ToString(CultureInfo.InvariantCulture);
            return "M" + v.ToString("0.0###############", CultureInfo.InvariantCulture)
                .TrimEnd('0').TrimEnd('.');
        }
        return s;
    }

    /// <summary>Extracts the pitch suffix from "M6x1.0" / "M6 x 1.0" (the
    /// part after the X, as written). Null when the size carries no pitch
    /// annotation.</summary>
    internal static string? ExtractRequestedPitch(string size)
    {
        string s = size.Trim().ToUpperInvariant();
        if (!s.StartsWith("M"))
            return null;
        int x = s.IndexOf('X');
        if (x <= 1)
            return null;
        string pitch = s[(x + 1)..].Trim();
        return pitch.Length > 0 &&
            double.TryParse(pitch, NumberStyles.Float, CultureInfo.InvariantCulture, out _) &&
            double.Parse(pitch, NumberStyles.Float, CultureInfo.InvariantCulture) > 0
            ? pitch
            : null;
    }

    /// <summary>Reads the standard threaded-table Pitch for the canonical
    /// size (the real NX_Thread_Standard.xml row: Size="M6 x 1.0",
    /// RadialEngage="0.75"). 0 when unreadable/absent — the caller refuses
    /// rather than guessing.</summary>
    private static double GetThreadedPitch(
        Func<StandardHoleFamily, string> loadTableXml, string normSize)
    {
        try
        {
            string xml = loadTableXml(StandardHoleFamily.Threaded);
            foreach (var row in ParseRows(xml, "ThreadedHole"))
            {
                if (!string.Equals(row.Field("Standard"), "Metric Coarse", StringComparison.Ordinal) ||
                    !string.Equals(row.Field("RadialEngage"), "0.75", StringComparison.Ordinal))
                    continue;
                string? sizeRaw = row.Field("Size");
                if (sizeRaw == null)
                    continue;
                string rowNominal = NormalizeSize(sizeRaw.Split('X')[0]);
                if (string.Equals(rowNominal, normSize, StringComparison.Ordinal))
                {
                    string? pitchRaw = row.Field("Pitch");
                    if (pitchRaw != null &&
                        double.TryParse(pitchRaw, NumberStyles.Float, CultureInfo.InvariantCulture, out double p))
                        return p;
                }
            }
        }
        catch
        {
            // unreadable table → 0, caller refuses with the standard pitch unknown
        }
        return 0;
    }

    private static StandardHoleRecord ResolveThreaded(string xml, string size, double nominal)
    {
        var rows = ParseRows(xml, "ThreadedHole");
        var matches = new List<StandardHoleRecord>();
        foreach (var row in rows)
        {
            if (!string.Equals(row.Field("Standard"), "Metric Coarse", StringComparison.Ordinal))
                continue;
            string? majorRaw = row.Field("MajorDiameter");
            if (majorRaw == null ||
                !double.TryParse(majorRaw, NumberStyles.Float, CultureInfo.InvariantCulture, out double major) ||
                Math.Abs(major - nominal) > 1e-9)
                continue;
            if (!string.Equals(row.Field("RadialEngage"), "0.75", StringComparison.Ordinal))
                continue;
            matches.Add(row);
        }
        return RequireUnique(matches,
            $"螺纹标准表（{ThreadStandardFile}）中规格 {size}" +
            $"（Metric Coarse + 径向旋合率 0.75）", rows.Count);
    }

    private static StandardHoleRecord ResolveClearance(
        string xml, StandardHoleFamily family, string size, string fit, string standard)
    {
        var (form, screwType) = ClearanceMatchKeys[family];
        var rows = ParseRows(xml, "StandardHole");
        var matches = new List<StandardHoleRecord>();
        foreach (var row in rows)
        {
            if (!string.Equals(row.Field("Form"), form, StringComparison.Ordinal) ||
                !string.Equals(row.Field("ScrewType"), screwType, StringComparison.Ordinal) ||
                !string.Equals(row.Field("Size"), size, StringComparison.Ordinal) ||
                !string.Equals(row.Field("Fit"), fit, StringComparison.Ordinal) ||
                !string.Equals(row.Field("Standard"), standard, StringComparison.Ordinal))
                continue;
            matches.Add(row);
        }
        string familyName = family switch
        {
            StandardHoleFamily.SimpleClearance => "螺纹间隙孔",
            StandardHoleFamily.Counterbored => "沉头孔",
            _ => "埋头孔",
        };
        return RequireUnique(matches,
            $"螺钉间隙孔标准表（{ClearanceStandardFile}）中{familyName}规格 {size}" +
            $"（{standard} + {screwType} + {fit}）", rows.Count);
    }

    private static StandardHoleRecord RequireUnique(
        List<StandardHoleRecord> matches, string where, int totalRows)
    {
        if (matches.Count == 1)
            return matches[0];
        if (matches.Count == 0)
        {
            // The distinguishing case: the size simply is not in the standard
            // (the ISO countersunk series starts at M3). State it plainly —
            // no substitution, no guessing.
            throw new StandardHoleLookupException(
                $"{where}没有匹配的标准记录（该标准系列不含此规格）。" +
                "为避免使用错误数值，本次操作已拒绝；请改用该标准支持的规格。");
        }
        throw new StandardHoleLookupException(
            $"{where}匹配到 {matches.Count} 条记录，无法唯一确定。本次操作已拒绝。");
    }

    /// <summary>Parses all child elements of the root with the given tag into raw field dictionaries.</summary>
    private static List<StandardHoleRecord> ParseRows(string xml, string tagName)
    {
        var doc = new XmlDocument();
        try
        {
            doc.LoadXml(xml);
        }
        catch (XmlException ex)
        {
            throw new StandardHoleLookupException($"孔标准表 XML 解析失败：{ex.Message}");
        }
        var rows = new List<StandardHoleRecord>();
        if (doc.DocumentElement == null)
            throw new StandardHoleLookupException("孔标准表 XML 为空或缺少根元素。");
        foreach (XmlNode node in doc.DocumentElement.SelectNodes("//" + tagName)!)
        {
            var fields = new Dictionary<string, string>(StringComparer.Ordinal);
            if (node.Attributes != null)
            {
                foreach (XmlAttribute a in node.Attributes)
                    fields[a.Name] = a.Value;
            }
            rows.Add(new StandardHoleRecord(fields));
        }
        return rows;
    }
}
