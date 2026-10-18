using NXAI.Assistant.Core;
using Xunit;

namespace NXAI.Assistant.Tests;

/// <summary>
/// Offline tests for the NX context snapshot DTO, its bounded Chinese summary,
/// and the create_cylinder placement parameter (global_origin compatibility +
/// selected_face_center parsing and rejection rules). No NXOpen involved.
/// </summary>
public class NxContextTests
{
    // ------------------------------------------------------- DTO + summary

    private static NxContextSnapshot SampleSnapshot(int selectionCount = 1) => new()
    {
        CapturedAtUtc = "2026-10-16T12:00:00.0000000Z",
        Status = "available",
        WorkPartName = "test_part",
        WorkPartUnits = "Millimeters",
        WorkPartUnsaved = false,
        WorkCoordinateSystem = new NxCoordinateSystemInfo("WCS",
            0, 0, 0, 1, 0, 0, 0, 1, 0, null, null, null),
        ActiveView = new NxViewInfo("TFR-TRI", 1.0, null),
        Selection = Enumerable.Range(0, selectionCount).Select(i => new NxSelectionItem
        {
            Kind = "face",
            Subtype = "planar",
            Name = $"FACE({i})",
            Face = new NxFaceInfo("planar", 10 + i, 20, 30, 0, 0, 1, null),
        }).ToList(),
        Unavailable = new[] { "绝对坐标系未获取（未实现）。" },
    };

    [Fact]
    public void Summary_ContainsWorkPartSelectionAndDisclaimer()
    {
        var text = SampleSnapshot().ToContextSummary();
        Assert.Contains("工作部件：test_part", text);
        Assert.Contains("选中对象：1 个", text);
        Assert.Contains("[face]（planar）", text);
        Assert.Contains("非用户指令", text);   // state data, not instructions
        Assert.Contains("中心 (10, 20, 30)", text);
        Assert.Contains("法向 (0, 0, 1)", text);
    }

    [Fact]
    public void Summary_EmptySelection_StatesZero()
    {
        var text = SampleSnapshot(selectionCount: 0).ToContextSummary();
        Assert.Contains("选中对象：0 个", text);
    }

    [Fact]
    public void Summary_NoActivePart_StatesUnavailable()
    {
        var snapshot = new NxContextSnapshot { Status = "no_active_part" };
        var text = snapshot.ToContextSummary();
        Assert.Contains("没有活动工作部件", text);
        Assert.DoesNotContain("工作部件：", text);
    }

    [Fact]
    public void Summary_MoreThanFiveSelections_TruncatedList()
    {
        var text = SampleSnapshot(selectionCount: 8).ToContextSummary();
        Assert.Contains("其余 3 个选中对象未展开", text);
        Assert.Contains("- [face]", text);
    }

    [Fact]
    public void Summary_HugeSelection_RespectsLengthCap()
    {
        var snapshot = SampleSnapshot(selectionCount: 200);
        var text = snapshot.ToContextSummary();
        Assert.True(text.Length <= 2400, $"summary too long: {text.Length}");
    }

    [Fact]
    public void Summary_UnavailableReasons_Listed()
    {
        var text = SampleSnapshot().ToContextSummary();
        Assert.Contains("未获取：绝对坐标系未获取", text);
    }

    [Fact]
    public void Summary_UnsavedPart_MarkedWithoutPath()
    {
        var snapshot = SampleSnapshot() with { WorkPartUnsaved = true };
        var text = snapshot.ToContextSummary();
        Assert.Contains("未保存部件", text);
        // Full paths are never included (they aren't in the DTO at all).
        Assert.DoesNotContain("D:\\", text);
    }

    // --------------------------------------------- cylinder placement parsing

    private static CreateCylinderParameters? ParseCylinder(string json, out string? error) =>
        new CreateCylinderRequestParser().ParseParameters(json, out error);

    [Fact]
    public void Cylinder_WithoutPlacement_LegacyGlobalOrigin()
    {
        var p = ParseCylinder("""{"diameter":20,"height":30,"unit":"mm"}""", out var error);
        Assert.Null(error);
        Assert.NotNull(p);
        Assert.Equal(CylinderPlacement.GlobalOrigin, p!.Placement);
        Assert.Equal(0, p.OriginX);
    }

    [Fact]
    public void Cylinder_ExplicitGlobalOrigin_SameBehavior()
    {
        var p = ParseCylinder(
            """{"diameter":20,"height":30,"unit":"mm","placement":"global_origin","origin_x":5}""",
            out _);
        Assert.NotNull(p);
        Assert.Equal(CylinderPlacement.GlobalOrigin, p!.Placement);
        Assert.Equal(5, p.OriginX);
    }

    [Fact]
    public void Cylinder_SelectedFaceCenter_Parses()
    {
        var p = ParseCylinder(
            """{"diameter":20,"height":30,"unit":"mm","placement":"selected_face_center"}""",
            out var error);
        Assert.Null(error);
        Assert.NotNull(p);
        Assert.Equal(CylinderPlacement.SelectedFaceCenter, p!.Placement);
    }

    [Fact]
    public void Cylinder_UnknownPlacement_Rejected()
    {
        Assert.Null(ParseCylinder(
            """{"diameter":20,"height":30,"unit":"mm","placement":"face_normal"}""",
            out var e));
        Assert.Contains("不支持的放置模式", e);
    }

    [Fact]
    public void Cylinder_NonStringPlacement_Rejected()
    {
        Assert.Null(ParseCylinder(
            """{"diameter":20,"height":30,"unit":"mm","placement":42}""", out _));
    }

    [Fact]
    public void Cylinder_SelectedFaceCenter_StillValidatesDimensions()
    {
        Assert.Null(ParseCylinder(
            """{"diameter":-20,"height":30,"unit":"mm","placement":"selected_face_center"}""",
            out _));
        Assert.Null(ParseCylinder(
            """{"diameter":20,"height":"Infinity","unit":"mm","placement":"selected_face_center"}""",
            out _));
    }

    [Fact]
    public void Cylinder_UnknownField_StillRejected()
    {
        Assert.Null(ParseCylinder(
            """{"diameter":20,"height":30,"unit":"mm","save_path":"D:\\x.prt"}""",
            out var e));
        Assert.Contains("save_path", e);
    }

    // ---------------------------------------------------- item description

    [Fact]
    public void Summary_BodyItem_DescribesSheetOrSolid()
    {
        var snapshot = new NxContextSnapshot
        {
            Status = "available",
            WorkPartName = "p",
            Selection = new[]
            {
                new NxSelectionItem
                {
                    Kind = "body", Subtype = "solid_body", Name = "BLOCK(1)",
                    Body = new NxBodyInfo(false, 6, 0, 0, 0, 10, 10, 10),
                },
            },
        };
        var text = snapshot.ToContextSummary();
        Assert.Contains("[body]（solid_body）", text);
        Assert.Contains("实体", text);
    }

    [Fact]
    public void Summary_OtherItem_TypedWithoutGeometryClaims()
    {
        var snapshot = new NxContextSnapshot
        {
            Status = "available",
            WorkPartName = "p",
            Selection = new[] { new NxSelectionItem { Kind = "other", Name = "OBJ(9)" } },
        };
        var text = snapshot.ToContextSummary();
        Assert.Contains("[other] OBJ(9)", text);
        Assert.DoesNotContain("法向", text);
    }
}
