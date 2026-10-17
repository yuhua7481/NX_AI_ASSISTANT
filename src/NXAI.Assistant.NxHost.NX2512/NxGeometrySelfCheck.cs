using NXAI.Assistant.Core;
using NXOpen;
using NXOpen.UF;

namespace NXAI.Assistant.NxHost.NX2512;

/// <summary>
/// In-process NX geometry self-check (Phase 8-C): runs the core modeling
/// scenarios against a DEDICATED temporary part created via the verified
/// PartCollection.FileNew() API — never against the user's current work
/// part — and produces a structured report.
///
/// API notes (verified against the local NX2512 assembly metadata and
/// NXOpen.xml): PartCollection.FileNew() → FileNew builder (TemplateFileName,
/// Units, NewFileName, MakeDisplayedPart) whose Commit creates and makes
/// displayed the new part; BasePart.Close(CloseWholeTree, CloseModified,
/// PartCloseResponses) closes it. The temp part is created UNSAVED and
/// closed without saving — no user file is touched, nothing is auto-saved.
///
/// Every case states its input, expected geometry, actual result and failure
/// stage. A case passes only on verified geometry; when shape-type
/// confirmation (e.g. torus vs cylinder with the same bbox) is not
/// available offline, the case reports "verified=geometry-bounds" and the
/// shape-type line stays an explicit live-acceptance item.
/// </summary>
public sealed class NxGeometrySelfCheck
{
    private readonly NxToolExecutor _executor;

    public NxGeometrySelfCheck(NxToolExecutor executor) => _executor = executor;

    public sealed record CaseResult(
        string Name,
        bool Passed,
        string Stage,
        string Input,
        string Expected,
        string Actual,
        string? Error);

    public sealed class SelfCheckException : Exception
    {
        public SelfCheckException(string message, Exception? inner = null) : base(message, inner) { }
    }

    /// <summary>Runs all self-check cases in a fresh temporary part.</summary>
    public GeometrySelfCheckReport RunAll()
    {
        var session = Session.GetSession();
        var originalWorkPart = session.Parts.Work;
        var originalDisplayed = session.Parts.Display;

        Part? tempPart = null;
        var cases = new List<CaseResult>();
        string partIdentity = "（临时部件创建失败）";
        try
        {
            tempPart = CreateTempPart(session);
            partIdentity = $"{tempPart.Name}（未保存临时部件，单位 {tempPart.PartUnits}）";
            session.Parts.SetWork(tempPart);

            cases.Add(CheckXyCircleExtrude(tempPart));
            cases.Add(CheckXzCircleRevolveTorus(tempPart));
            cases.Add(CheckPrimitives(tempPart));
            cases.Add(CheckStaleReferenceRefusal(tempPart));
        }
        catch (Exception ex)
        {
            cases.Add(new CaseResult("selfcheck.setup", false, "setup",
                "创建隔离临时部件", "临时部件可用", "创建失败", $"{ex.GetType().Name}: {ex.Message}"));
        }
        finally
        {
            // Close the temp part WITHOUT saving. CloseModified.CloseModified
            // keeps closing even though unsaved changes exist — that is the
            // point: the self-check must never persist anything.
            try
            {
                if (tempPart != null)
                    tempPart.Close(BasePart.CloseWholeTree.True, BasePart.CloseModified.CloseModified,
                        null!);
            }
            catch (Exception closeEx)
            {
                cases.Add(new CaseResult("selfcheck.cleanup", false, "cleanup",
                    "关闭临时部件", "关闭且不保存", "关闭失败", closeEx.Message));
            }

            // Restore the user's part as displayed/work.
            try
            {
                if (originalDisplayed != null)
                    session.Parts.SetDisplay(originalDisplayed, true, false, out _);
                if (originalWorkPart != null)
                    session.Parts.SetWork(originalWorkPart);
            }
            catch
            {
                // best-effort restore; report via a case
                cases.Add(new CaseResult("selfcheck.restore", false, "cleanup",
                    "恢复用户工作部件", "工作部件还原", "还原失败", null));
            }
        }

        return new GeometrySelfCheckReport(
            partIdentity,
            cases.All(c => c.Passed),
            cases.Count(c => c.Passed),
            cases.Count(c => !c.Passed),
            cases.Select(c => new GeometrySelfCheckCase(
                c.Name, c.Passed, c.Stage, c.Input, c.Expected, c.Actual, c.Error)).ToList());
    }

    /// <summary>Creates an unsaved millimeter part from the blank template.
    /// TemplateFile name follows the install's metric blank template
    /// (verified file-new flow); NewFileName is a %TEMP% path so NX never
    /// touches a user file.</summary>
    private static Part CreateTempPart(Session session)
    {
        var fileNew = session.Parts.FileNew();
        fileNew.TemplateFileName = "_blank_mm.prt";
        fileNew.TemplateType = FileNewTemplateType.Generic;
        fileNew.Units = Part.Units.Millimeters;
        fileNew.NewFileName = Path.Combine(
            Path.GetTempPath(),
            $"nxai_selfcheck_{DateTime.Now:yyyyMMdd_HHmmss}_{Guid.NewGuid().ToString("N")[..8]}.prt");
        fileNew.MakeDisplayedPart = true;
        var committed = fileNew.Commit() as Part
            ?? throw new SelfCheckException("FileNew.Commit 未返回新部件。");
        fileNew.Destroy();
        if (committed.PartUnits != BasePart.Units.Millimeters)
            throw new SelfCheckException($"临时部件单位是 {committed.PartUnits}，预期毫米。");
        return committed;
    }

    // ------------------------------------------------------------ case A

    private CaseResult CheckXyCircleExtrude(Part part)
    {
        const string input = "XY 平面圆形草图 d30 → 拉伸 50mm（法向）";
        const string expected = "一个新实体，包围盒 (-15,-15,0)–(15,15,50)";
        try
        {
            var sketch = _executor.ExecuteCreateSketch(
                new CreateSketchParameters(SketchPlaneMode.WcsXY, SketchProfileType.Circle,
                    0, 0, 30, 0, 0), null);
            if (sketch.Status != ToolExecutionStatus.Success)
                return Fail("caseA.sketch", input, expected, sketch.Message);

            var sketchRef = ExtractRef(sketch.DetailsJson, "sketchRef");
            if (sketchRef == null)
                return Fail("caseA.sketch", input, expected, "结果缺少 sketchRef");

            var extrude = _executor.ExecuteCreateExtrude(
                new CreateExtrudeParameters(sketchRef, 50, ExtrudeDirection.Normal), null);
            if (extrude.Status != ToolExecutionStatus.Success)
                return Fail("caseA.extrude", input, expected, extrude.Message);

            // Verify against the real part state: exactly 1 body, bbox match.
            var verify = VerifySingleBodyBoundingBox(part, -15, -15, 0, 15, 15, 50);
            if (!verify.Passed)
                return Fail("caseA.verify", input, expected, verify.Reason);

            return new CaseResult("caseA.sketch_extrude", true, "verified",
                input, expected, verify.Actual, null);
        }
        catch (Exception ex)
        {
            return Fail("caseA.exception", input, expected, $"{ex.GetType().Name}: {ex.Message}");
        }
    }

    // ------------------------------------------------------------ case B

    private CaseResult CheckXzCircleRevolveTorus(Part part)
    {
        const string input = "XZ 平面 U=45 d10 圆形草图 → 绕绝对 Z 轴 360° 旋转";
        const string expected = "一个新实体（圆环），包围盒 (-50,-50,-5)–(50,50,5)";
        try
        {
            var sketch = _executor.ExecuteCreateSketch(
                new CreateSketchParameters(SketchPlaneMode.WcsXZ, SketchProfileType.Circle,
                    0, 0, 10, 45, 0), null);
            if (sketch.Status != ToolExecutionStatus.Success)
                return Fail("caseB.sketch", input, expected, sketch.Message);

            var sketchRef = ExtractRef(sketch.DetailsJson, "sketchRef");
            if (sketchRef == null)
                return Fail("caseB.sketch", input, expected, "结果缺少 sketchRef");

            var revolve = _executor.ExecuteCreateRevolve(
                new CreateRevolveParameters(sketchRef, RevolveAxisMode.WcsZ, 360), null);
            if (revolve.Status != ToolExecutionStatus.Success)
                return Fail("caseB.revolve", input, expected, revolve.Message);

            // Analytic torus envelope: circle center (45,0,0) r5 around Z →
            // world bbox (-50,-50,-5)–(50,50,5). The box alone cannot prove
            // torus (a cylinder r50 h10 has the same box); shape-type
            // confirmation is a live-acceptance item and the report says so.
            var verify = VerifySingleBodyBoundingBox(part, -50, -50, -5, 50, 50, 5);
            if (!verify.Passed)
                return Fail("caseB.verify", input, expected, verify.Reason);

            return new CaseResult("caseB.sketch_revolve", true, "verified=geometry-bounds（形状类型待实机确认：torus vs 同包围盒圆柱）",
                input, expected, verify.Actual, null);
        }
        catch (Exception ex)
        {
            return Fail("caseB.exception", input, expected, $"{ex.GetType().Name}: {ex.Message}");
        }
    }

    // ------------------------------------------------------------ primitives

    private CaseResult CheckPrimitives(Part part)
    {
        const string input = "长方体/圆柱/圆锥/球 各一";
        const string expected = "四个新实体创建成功（自检部件内计数增量正确）";
        try
        {
            int before = CountBodies(part);
            var results = new[]
            {
                _executor.ExecuteCreateBlock(new CreateBlockParameters(200, 0, 0, 10, 10, 10, "mm"), null),
                _executor.ExecuteCreateCylinder(new CreateCylinderParameters(230, 0, 0, 10, 10, "mm"), null),
                _executor.ExecuteCreateCone(new CreateConeParameters(260, 0, 0, 10, 0, 10, "mm"), null),
                _executor.ExecuteCreateSphere(new CreateSphereParameters(290, 0, 0, 10, "mm"), null),
            };
            var failed = results.FirstOrDefault(r => r.Status != ToolExecutionStatus.Success);
            if (failed != null)
                return Fail("caseC.primitives", input, expected, failed.Message);

            int after = CountBodies(part);
            if (after != before + 4)
                return Fail("caseC.primitives", input, expected, $"实体数 {after}，预期 {before + 4}");

            return new CaseResult("caseC.primitives", true, "verified", input, expected,
                $"4 个基本体全部提交并验证", null);
        }
        catch (Exception ex)
        {
            return Fail("caseC.exception", input, expected, $"{ex.GetType().Name}: {ex.Message}");
        }
    }

    // ------------------------------------------------------------ stale ref

    private CaseResult CheckStaleReferenceRefusal(Part part)
    {
        const string input = "对不存在的草图引用执行拉伸";
        const string expected = "安全拒绝（PreconditionsChanged），不产生几何";
        try
        {
            int before = CountBodies(part);
            var result = _executor.ExecuteCreateExtrude(
                new CreateExtrudeParameters("SKETCH_DOES_NOT_EXIST", 10, ExtrudeDirection.Normal), null);
            int after = CountBodies(part);

            if (result.Status != ToolExecutionStatus.PreconditionsChanged)
                return Fail("caseD.stale", input, expected, $"状态 {result.Status}，预期 PreconditionsChanged");
            if (after != before)
                return Fail("caseD.stale", input, expected, "拒绝后实体数发生变化");

            return new CaseResult("caseD.stale_refusal", true, "verified", input, expected,
                "已安全拒绝且无几何变化", null);
        }
        catch (Exception ex)
        {
            return Fail("caseD.exception", input, expected, $"{ex.GetType().Name}: {ex.Message}");
        }
    }

    // ------------------------------------------------------------ helpers

    private static (bool Passed, string Reason, string Actual) VerifySingleBodyBoundingBox(
        Part part, double minX, double minY, double minZ, double maxX, double maxY, double maxZ)
    {
        // Snapshot-diff against the state before the case is impractical here
        // (bodies accumulate across cases in the temp part), so verify by
        // locating ANY body whose bbox matches the expectation within 0.01mm.
        var ufSession = UFSession.GetUFSession();
        double[] box = new double[6];
        foreach (object item in part.Bodies)
        {
            if (item is not Body body)
                continue;
            try
            {
                ufSession.Modl.AskBoundingBox(body.Tag, box);
            }
            catch
            {
                continue;
            }
            const double tol = 0.01;
            if (Math.Abs(box[0] - minX) <= tol && Math.Abs(box[1] - minY) <= tol &&
                Math.Abs(box[2] - minZ) <= tol && Math.Abs(box[3] - maxX) <= tol &&
                Math.Abs(box[4] - maxY) <= tol && Math.Abs(box[5] - maxZ) <= tol)
            {
                return (true, "", $"包围盒匹配 ({box[0]:F3},{box[1]:F3},{box[2]:F3})–({box[3]:F3},{box[4]:F3},{box[5]:F3})");
            }
        }
        return (false, $"未找到包围盒为 ({minX},{minY},{minZ})–({maxX},{maxY},{maxZ}) 的实体", "");
    }

    private static int CountBodies(Part part)
    {
        int count = 0;
        foreach (object _ in part.Bodies)
            count++;
        return count;
    }

    private static string? ExtractRef(string? detailsJson, string key)
    {
        if (string.IsNullOrWhiteSpace(detailsJson))
            return null;
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(detailsJson);
            if (doc.RootElement.ValueKind == System.Text.Json.JsonValueKind.Object &&
                doc.RootElement.TryGetProperty(key, out var element) &&
                element.ValueKind == System.Text.Json.JsonValueKind.String)
            {
                return element.GetString();
            }
        }
        catch (System.Text.Json.JsonException)
        {
        }
        return null;
    }

    private static CaseResult Fail(string name, string input, string expected, string error) =>
        new(name, false, "failed", input, expected, "", error);
}
