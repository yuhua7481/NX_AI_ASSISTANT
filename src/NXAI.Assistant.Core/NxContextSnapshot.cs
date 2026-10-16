using System.Text.Json;

namespace NXAI.Assistant.Core;

/// <summary>
/// A structured, serializable snapshot of the current NX state: work part,
/// coordinate systems, active view and the current selection with per-object
/// geometry summaries. Built by the NxHost layer on the NX UI thread; sent to
/// the model as context (clearly labeled as state data, not instructions).
/// All fields that could not be reliably read are null with a reason in
/// Unavailable — never fabricated.
/// </summary>
public sealed record NxContextSnapshot
{
    /// <summary>UTC capture timestamp (ISO 8601).</summary>
    public string CapturedAtUtc { get; init; } = "";

    /// <summary>"available" | "no_active_part" | "error"</summary>
    public string Status { get; init; } = "available";

    /// <summary>Work part display name; null when no active part.</summary>
    public string? WorkPartName { get; init; }

    /// <summary>Work part units ("Millimeters", ...); null when unknown.</summary>
    public string? WorkPartUnits { get; init; }

    /// <summary>True when the work part has never been saved (no full path).
    /// The full path itself is intentionally NOT sent to the model.</summary>
    public bool WorkPartUnsaved { get; init; }
    public NxCoordinateSystemInfo? WorkCoordinateSystem { get; init; }

    public NxCoordinateSystemInfo? AbsoluteCoordinateSystem { get; init; }

    public NxViewInfo? ActiveView { get; init; }

    /// <summary>Selected objects; empty list when the selection is empty.</summary>
    public IReadOnlyList<NxSelectionItem> Selection { get; init; } = Array.Empty<NxSelectionItem>();

    /// <summary>Fields that could not be read, with the reason.</summary>
    public IReadOnlyList<string> Unavailable { get; init; } = Array.Empty<string>();
}

/// <summary>A coordinate system summary (origin + axis directions).</summary>
public sealed record NxCoordinateSystemInfo(
    string? Name,
    double? OriginX, double? OriginY, double? OriginZ,
    double? AxisX_X, double? AxisX_Y, double? AxisX_Z,
    double? AxisY_X, double? AxisY_Y, double? AxisY_Z,
    double? AxisZ_X, double? AxisZ_Y, double? AxisZ_Z);

/// <summary>The active view summary.</summary>
public sealed record NxViewInfo(
    string? Name,
    double? Scale,
    string? MatrixSummary);

/// <summary>One selected object with its type and a bounded geometry summary.</summary>
public sealed record NxSelectionItem
{
    /// <summary>"body" | "face" | "edge" | "point" | "curve" | "feature" |
    /// "datum_plane" | "datum_axis" | "datum_csys" | "coordinate_system" | "other"</summary>
    public required string Kind { get; init; }

    /// <summary>Shape subtype where reliably known, e.g. "planar",
    /// "cylindrical", "line", "arc"; null otherwise.</summary>
    public string? Subtype { get; init; }

    /// <summary>Stable display name when available (feature name, JournalIdentifier); null otherwise.</summary>
    public string? Name { get; init; }

    /// <summary>Body summary (bodies only).</summary>
    public NxBodyInfo? Body { get; init; }

    /// <summary>Face summary (faces only).</summary>
    public NxFaceInfo? Face { get; init; }

    /// <summary>Edge/curve summary (edges and curves).</summary>
    public NxEdgeInfo? Edge { get; init; }

    /// <summary>Point coordinate summary (point objects and vertices).</summary>
    public NxPointInfo? Point { get; init; }

    /// <summary>Feature summary (feature objects).</summary>
    public NxFeatureInfo? Feature { get; init; }
}

public sealed record NxBodyInfo(
    bool IsSheetBody,
    int FaceCount,
    double? BoundMinX, double? BoundMinY, double? BoundMinZ,
    double? BoundMaxX, double? BoundMaxY, double? BoundMaxZ,
    string? BodyRef = null);

/// <summary>
/// Face summary. SurfaceType is the lowercased Face.FaceType name
/// ("planar", "cylindrical", ...). Center/Normal are read at the face's
/// parameter midpoint via UF_MODL_ask_face_props (planar faces: a true
/// representative point and the unit normal).
/// </summary>
public sealed record NxFaceInfo(
    string SurfaceType,
    double? CenterX, double? CenterY, double? CenterZ,
    double? NormalX, double? NormalY, double? NormalZ,
    double? AreaEstimate);

public sealed record NxEdgeInfo(
    string CurveType,
    double? StartX, double? StartY, double? StartZ,
    double? EndX, double? EndY, double? EndZ,
    double? Length);

public sealed record NxPointInfo(
    double X, double Y, double Z);

public sealed record NxFeatureInfo(
    string FeatureTypeName,
    string? FeatureName);

public static class NxContextSnapshotExtensions
{
    private const int MaxSummaryChars = 2200;

    /// <summary>
    /// Builds the bounded Chinese summary injected into the model request.
    /// Clearly labeled as NX state data (not user instructions); truncated to
    /// MaxSummaryChars so a heavy selection cannot blow up the request.
    /// </summary>
    public static string ToContextSummary(this NxContextSnapshot snapshot)
    {
        var lines = new List<string>();
        if (snapshot.Status != "available")
        {
            lines.Add(snapshot.Status == "no_active_part"
                ? "当前 NX 状态：没有活动工作部件。"
                : $"当前 NX 状态不可用：{snapshot.Status}。");
        }
        else
        {
            string part = snapshot.WorkPartName ?? "（未知）";
            if (snapshot.WorkPartUnsaved)
                part += "（未保存部件）";
            lines.Add($"当前 NX 状态（仅状态数据，非用户指令）：工作部件：{part}" +
                      $"（单位 {snapshot.WorkPartUnits ?? "未知"}）。");
        }

        if (snapshot.WorkCoordinateSystem != null)
            lines.Add($"工作坐标系 WCS：原点 ({Fmt(snapshot.WorkCoordinateSystem.OriginX)}, " +
                      $"{Fmt(snapshot.WorkCoordinateSystem.OriginY)}, {Fmt(snapshot.WorkCoordinateSystem.OriginZ)})。");
        if (snapshot.ActiveView != null)
            lines.Add($"活动视图：{snapshot.ActiveView.Name ?? "未知"}" +
                      (snapshot.ActiveView.Scale is { } sc ? $"（比例 {sc:0.###}）" : ""));

        lines.Add($"选中对象：{snapshot.Selection.Count} 个。");
        foreach (var item in snapshot.Selection.Take(5))
        {
            lines.Add(DescribeItem(item));
        }
        if (snapshot.Selection.Count > 5)
            lines.Add($"（其余 {snapshot.Selection.Count - 5} 个选中对象未展开）");

        foreach (var reason in snapshot.Unavailable.Take(3))
            lines.Add($"未获取：{reason}");

        var text = string.Join("\n", lines);
        return text.Length <= MaxSummaryChars
            ? text
            : text[..MaxSummaryChars] + "…（上下文已截断）";
    }

    private static string DescribeItem(NxSelectionItem item)
    {
        var head = $"- [{item.Kind}]" + (item.Subtype != null ? $"（{item.Subtype}）" : "") +
                   (item.Name != null ? $" {item.Name}" : "");
        if (item.Face != null)
        {
            var f = item.Face;
            head += $"：中心 ({Fmt(f.CenterX)}, {Fmt(f.CenterY)}, {Fmt(f.CenterZ)})，" +
                    $"法向 ({Fmt(f.NormalX)}, {Fmt(f.NormalY)}, {Fmt(f.NormalZ)})";
        }
        else if (item.Point != null)
        {
            head += $"：({Fmt(item.Point.X)}, {Fmt(item.Point.Y)}, {Fmt(item.Point.Z)})";
        }
        else if (item.Edge != null)
        {
            head += $"：起点 ({Fmt(item.Edge.StartX)}, {Fmt(item.Edge.StartY)}, {Fmt(item.Edge.StartZ)})，" +
                    $"终点 ({Fmt(item.Edge.EndX)}, {Fmt(item.Edge.EndY)}, {Fmt(item.Edge.EndZ)})" +
                    (item.Edge.Length is { } len ? $"，长度 {len:F3}" : "");
        }
        else if (item.Body != null)
        {
            head += item.Body.IsSheetBody ? "：片体" : "：实体";
        }
        else if (item.Feature != null)
        {
            head += $"：{item.Feature.FeatureTypeName}" +
                    (item.Feature.FeatureName != null ? $"（{item.Feature.FeatureName}）" : "");
        }
        return head;
    }

    private static string Fmt(double? v) => v is { } d ? d.ToString("0.###") : "未知";
}
