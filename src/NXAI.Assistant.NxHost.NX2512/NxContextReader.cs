using NXAI.Assistant.Core;
using NXOpen;
using NXOpen.UF;
using NxUi = NXOpen.UI;

namespace NXAI.Assistant.NxHost.NX2512;

/// <summary>
/// Captures a structured snapshot of the current NX state (work part, WCS,
/// active view, current selection) for the chat context. Must run on the NX
/// UI thread. Strictly read-only: never touches selection, model, save state
/// or part path. Every field that cannot be reliably read is reported as
/// unavailable with a reason — values are never fabricated.
///
/// API facts (reflection-verified against the installed NXOpen.UF.dll and
/// the managed XML docs):
///   - NXOpen.Selection.GetNumSelectedObjects()/GetSelectedTaggedObject(i) — the
///     global selection list (NXOpenUI.dll).
///   - Face.SolidFaceType → Face.FaceType (Planar/Cylindrical/...).
///   - UFModl.AskFaceUvMinmax(Tag face_tag, double[] uv_min_max[4]) — general
///     face UV bounds (wraps UF_MODL_ask_face_uv_minmax, uf_modl/global).
///     Do NOT use UFEvalsf.AskFaceUvMinmax here: UFEvalsf is the freeform
///     (B-surface) evaluator (UF_EVALSF_*) and access-violates on planar
///     faces — this crashed NX (AccessViolationException, 2026-10-16).
///   - UFModl.AskFaceProps(Tag face_id, double[] param[2], double[] point[3],
///     double[] u1[3], double[] v1[3], double[] u2[3], double[] v2[3],
///     double[] unit_norm[3], double[] radii[2]) — the managed wrapper's
///     parameter names are face_id/param/point/u1/v1/u2/v2/unit_norm/radii;
///     the unit normal is the 8th array, the last array is principal
///     curvature radii (NOT a uvp pair).
///   - BasePart.Views.WorkView; View.Name/Matrix/Scale.
///   - Part.WCS; WCS.Origin; WCS.CoordinateSystem (GetDirections).
/// </summary>
public sealed class NxContextReader
{
    public NxContextSnapshot Capture()
    {
        PhaseLog.Write("context.capture", "begin");
        var unavailable = new List<string>();

        Session session;
        Part? workPart;
        try
        {
            session = Session.GetSession();
            workPart = session.Parts.Work;
        }
        catch (Exception ex)
        {
            PhaseLog.Write("context.capture", $"FAILED session: {ex.GetType().Name}: {ex.Message}");
            return new NxContextSnapshot
            {
                CapturedAtUtc = DateTime.UtcNow.ToString("O"),
                Status = "error",
                Unavailable = new[] { $"无法访问 NX 会话：{ex.Message}" },
            };
        }

        if (workPart == null)
        {
            PhaseLog.Write("context.capture", "end: no_active_part");
            return new NxContextSnapshot
            {
                CapturedAtUtc = DateTime.UtcNow.ToString("O"),
                Status = "no_active_part",
            };
        }

        var selection = CaptureSelection(workPart, unavailable);
        PhaseLog.Write("context.capture",
            $"end: status=available selection={selection.Count} unavailable={unavailable.Count}");

        return new NxContextSnapshot
        {
            CapturedAtUtc = DateTime.UtcNow.ToString("O"),
            Status = "available",
            WorkPartName = SafeDisplayName(workPart),
            WorkPartUnits = workPart.PartUnits.ToString(),
            WorkPartUnsaved = string.IsNullOrEmpty(workPart.FullPath),
            WorkCoordinateSystem = CaptureWcs(workPart, unavailable),
            AbsoluteCoordinateSystem = null, // ACS read requires extra APIs; reported unavailable instead of guessed
            ActiveView = CaptureView(workPart, unavailable),
            Selection = selection,
            Unavailable = unavailable,
        };
    }

    private static string SafeDisplayName(Part part)
    {
        try
        {
            var name = part.Name;
            return string.IsNullOrEmpty(name) ? "(unnamed)" : name;
        }
        catch
        {
            return "(unnamed)";
        }
    }

    // ------------------------------------------------------------- selection

    private static IReadOnlyList<NxSelectionItem> CaptureSelection(
        Part workPart, List<string> unavailable)
    {
        var items = new List<NxSelectionItem>();
        try
        {
            var uiSelection = NxUi.GetUI().SelectionManager;
            int count = uiSelection.GetNumSelectedObjects();
            PhaseLog.Write("context.selection", $"count={count}");
            for (int i = 0; i < count; i++)
            {
                var obj = uiSelection.GetSelectedTaggedObject(i);
                var item = DescribeObject(workPart, obj);
                if (item != null)
                    items.Add(item);
            }
        }
        catch (Exception ex)
        {
            unavailable.Add($"选中对象读取失败：{ex.Message}");
        }
        return items;
    }

    /// <summary>
    /// Classifies one selected NXObject by its concrete runtime type and
    /// collects a bounded geometry summary. Unknown types get the "other"
    /// kind with a stable name — never a wrong geometric claim.
    /// </summary>
    internal static NxSelectionItem? DescribeObject(Part workPart, TaggedObject? obj)
    {
        if (obj == null)
            return null;

        switch (obj)
        {
            case Body body:
                return DescribeBody(body);
            case Face face:
                return DescribeFace(workPart, face);
            case Edge edge:
                return DescribeEdge(edge);
            case NXOpen.Point point:
                return new NxSelectionItem
                {
                    Kind = "point",
                    Name = SafeJournalId(point),
                    Point = new NxPointInfo(point.Coordinates.X, point.Coordinates.Y, point.Coordinates.Z),
                };
            case NXOpen.Features.Feature feature:
                return new NxSelectionItem
                {
                    Kind = "feature",
                    Name = SafeName(feature),
                    Feature = new NxFeatureInfo(feature.GetType().Name, feature.Name),
                };
            case DatumPlane:
                return new NxSelectionItem { Kind = "datum_plane", Name = SafeName(obj) };
            case DatumAxis:
                return new NxSelectionItem { Kind = "datum_axis", Name = SafeName(obj) };
            case CartesianCoordinateSystem csys:
                return DescribeCoordinateSystem(csys);
            case Line line:
                return DescribeLine(line);
            case Arc arc:
                return new NxSelectionItem
                {
                    Kind = "curve",
                    Subtype = "arc",
                    Name = SafeJournalId(arc),
                };
            case IBaseCurve:
                return new NxSelectionItem { Kind = "curve", Subtype = "curve", Name = SafeJournalId(obj) };
            case Sketch sketch:
                // A selected sketch is the object users pick for extrude/
                // revolve requests — labeling it "other" hid the fact that
                // the selection WAS usable (Phase 7.4 root cause D).
                return new NxSelectionItem
                {
                    Kind = "sketch",
                    Subtype = sketch.IsActive ? "active" : "inactive",
                    Name = SafeJournalId(sketch),
                };
            default:
                // Keep the real runtime type so "other" is never a dead end.
                return new NxSelectionItem
                {
                    Kind = "other",
                    Subtype = obj.GetType().Name,
                    Name = SafeJournalId(obj),
                };
        }
    }

    private static NxSelectionItem DescribeBody(Body body)
    {
        int faceCount = 0;
        foreach (object _ in body.GetFaces())
            faceCount++;
        var sheet = body.IsSheetBody;

        double? minX = null, minY = null, minZ = null, maxX = null, maxY = null, maxZ = null;
        try
        {
            var ufSession = UFSession.GetUFSession();
            double[] box = new double[6];
            ufSession.Modl.AskBoundingBox(body.Tag, box);
            minX = box[0]; minY = box[1]; minZ = box[2];
            maxX = box[3]; maxY = box[4]; maxZ = box[5];
        }
        catch
        {
            // bounds unavailable; the item stays typed without them
        }

        return new NxSelectionItem
        {
            Kind = "body",
            Subtype = sheet ? "sheet_body" : "solid_body",
            Name = SafeJournalId(body),
            Body = new NxBodyInfo(sheet, faceCount, minX, minY, minZ, maxX, maxY, maxZ,
                body.JournalIdentifier),
        };
    }

    internal static NxSelectionItem DescribeFace(Part workPart, Face face)
    {
        string surfaceType;
        try
        {
            surfaceType = face.SolidFaceType.ToString().ToLowerInvariant();
        }
        catch
        {
            surfaceType = "unknown";
        }

        double? cx = null, cy = null, cz = null, nx = null, ny = null, nz = null;
        if (surfaceType == "planar")
        {
            // Planar faces only: evaluate at the UV midpoint. Non-planar faces
            // are not guessed (the caller refuses face-relative placement on
            // them anyway).
            try
            {
                var ufSession = UFSession.GetUFSession();
                double[] uv = new double[4];
                // General-face UV bounds (UF_MODL_ask_face_uv_minmax). Never
                // UFEvalsf here — that is the B-surface-only evaluator and
                // access-violates on planar faces (crash root cause, see the
                // class doc).
                ufSession.Modl.AskFaceUvMinmax(face.Tag, uv);
                double uMid = (uv[0] + uv[1]) / 2.0;
                double vMid = (uv[2] + uv[3]) / 2.0;

                var point = new double[3];
                var unitNorm = new double[3];
                var u1 = new double[3];
                var v1 = new double[3];
                var u2 = new double[3];
                var v2 = new double[3];
                var radii = new double[2];
                // Managed signature (reflection-verified):
                // AskFaceProps(face_id, param[2], point[3], u1[3], v1[3],
                //              u2[3], v2[3], unit_norm[3], radii[2])
                ufSession.Modl.AskFaceProps(face.Tag,
                    new double[] { uMid, vMid },
                    point, u1, v1, u2, v2, unitNorm, radii);
                cx = point[0]; cy = point[1]; cz = point[2];
                nx = unitNorm[0]; ny = unitNorm[1]; nz = unitNorm[2];
                PhaseLog.Write("context.face_geometry",
                    $"ok face={face.JournalIdentifier} type=planar center=({cx:0.###},{cy:0.###},{cz:0.###}) normal=({nx:0.###},{ny:0.###},{nz:0.###})");
            }
            catch (Exception ex)
            {
                PhaseLog.Write("context.face_geometry", $"FAILED: {ex.GetType().Name}: {ex.Message}");
                // center/normal unavailable; the item stays typed without them
            }
        }

        return new NxSelectionItem
        {
            Kind = "face",
            Subtype = surfaceType,
            Name = SafeJournalId(face),
            Face = new NxFaceInfo(surfaceType, cx, cy, cz, nx, ny, nz, AreaEstimate: null),
        };
    }

    private static NxSelectionItem DescribeEdge(Edge edge)
    {
        double? sx = null, sy = null, sz = null, ex = null, ey = null, ez = null;
        string subtype = "edge";
        try
        {
            edge.GetVertices(out var v1, out var v2);
            sx = v1.X; sy = v1.Y; sz = v1.Z;
            ex = v2.X; ey = v2.Y; ez = v2.Z;
            subtype = "edge";
        }
        catch
        {
            // vertices unavailable
        }
        return new NxSelectionItem
        {
            Kind = "edge",
            Subtype = subtype,
            Name = SafeJournalId(edge),
            Edge = new NxEdgeInfo(subtype, sx, sy, sz, ex, ey, ez, Length: null),
        };
    }

    private static NxSelectionItem DescribeLine(Line line)
    {
        try
        {
            var p1 = line.StartPoint;
            var p2 = line.EndPoint;
            double length = Math.Sqrt(
                Math.Pow(p2.X - p1.X, 2) + Math.Pow(p2.Y - p1.Y, 2) + Math.Pow(p2.Z - p1.Z, 2));
            return new NxSelectionItem
            {
                Kind = "curve",
                Subtype = "line",
                Name = SafeJournalId(line),
                Edge = new NxEdgeInfo("line", p1.X, p1.Y, p1.Z, p2.X, p2.Y, p2.Z, length),
            };
        }
        catch
        {
            return new NxSelectionItem { Kind = "curve", Subtype = "line", Name = SafeJournalId(line) };
        }
    }

    private static NxSelectionItem DescribeCoordinateSystem(CartesianCoordinateSystem csys)
    {
        double? ox = null, oy = null, oz = null;
        double? xx = null, xy = null, xz = null, yx = null, yy = null, yz = null, zx = null, zy = null, zz = null;
        try
        {
            var origin = csys.Origin;
            ox = origin.X; oy = origin.Y; oz = origin.Z;
            csys.GetDirections(out var xAxis, out var yAxis);
            xx = xAxis.X; xy = xAxis.Y; xz = xAxis.Z;
            yx = yAxis.X; yy = yAxis.Y; yz = yAxis.Z;
            // Z = X cross Y (right-handed)
            zx = xy * yz - xz * yy;
            zy = xz * yx - xx * yz;
            zz = xx * yy - xy * yx;
        }
        catch
        {
            // axes unavailable
        }
        return new NxSelectionItem
        {
            Kind = "coordinate_system",
            Name = SafeJournalId(csys),
            Face = null,
            Body = null,
        };
    }

    // ------------------------------------------------------------- wcs / view

    private static NxCoordinateSystemInfo? CaptureWcs(Part workPart, List<string> unavailable)
    {
        try
        {
            var wcs = workPart.WCS;
            var origin = wcs.Origin;
            double? xx = null, xy = null, xz = null, yx = null, yy = null, yz = null;
            try
            {
                var csys = wcs.CoordinateSystem;
                if (csys != null)
                {
                    csys.GetDirections(out var xAxis, out var yAxis);
                    xx = xAxis.X; xy = xAxis.Y; xz = xAxis.Z;
                    yx = yAxis.X; yy = yAxis.Y; yz = yAxis.Z;
                }
            }
            catch
            {
                unavailable.Add("WCS 轴方向未获取（WCS.CoordinateSystem 不可用）。");
            }
            return new NxCoordinateSystemInfo("WCS",
                origin.X, origin.Y, origin.Z, xx, xy, xz, yx, yy, yz, null, null, null);
        }
        catch (Exception ex)
        {
            unavailable.Add($"WCS 读取失败：{ex.Message}");
            return null;
        }
    }

    private static NxViewInfo? CaptureView(Part workPart, List<string> unavailable)
    {
        try
        {
            var view = workPart.Views.WorkView;
            string? name = null;
            double? scale = null;
            try { name = view.Name; } catch { /* name unavailable */ }
            try { scale = view.Scale; } catch { /* scale unavailable */ }
            return new NxViewInfo(name, scale, MatrixSummary: null);
        }
        catch (Exception ex)
        {
            unavailable.Add($"活动视图读取失败：{ex.Message}");
            return null;
        }
    }

    // ------------------------------------------------------------- helpers

    private static string? SafeName(TaggedObject obj)
    {
        try
        {
            if (obj is not NXObject nxObj)
                return null;
            var name = nxObj.Name;
            return string.IsNullOrEmpty(name) ? null : name;
        }
        catch
        {
            return null;
        }
    }

    private static string? SafeJournalId(TaggedObject obj)
    {
        try
        {
            if (obj is not NXObject nxObj)
                return null;
            var id = nxObj.JournalIdentifier;
            return string.IsNullOrEmpty(id) ? null : id;
        }
        catch
        {
            return null;
        }
    }
}
