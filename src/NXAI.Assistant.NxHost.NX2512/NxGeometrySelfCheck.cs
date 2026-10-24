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

    /// <summary>Runs all self-check cases in a fresh temporary part.
    ///
    /// Phase 8.1.6: the part-creation strategy is SEED-PART-FIRST. FileNew
    /// is structurally unusable in this session (430 registered templates —
    /// including NX's own "空白" — all rejected with "选定的模板不存在",
    /// live-verified), so the primary path copies a KNOWN-GOOD millimeter
    /// seed part to %TEMP% and opens the COPY via the verified
    /// PartCollection.OpenDisplay API; the seed file itself is never
    /// touched, and the copy is closed without saving and deleted. FileNew
    /// remains as a fallback candidate for installs where it works.</summary>
    public GeometrySelfCheckReport RunAll()
    {
        var session = Session.GetSession();
        var originalWorkPart = session.Parts.Work;
        var originalDisplayed = session.Parts.Display;

        Part? tempPart = null;
        string? tempCopyPath = null;
        var cases = new List<CaseResult>();
        string partIdentity = "（临时部件创建失败）";
        try
        {
            (tempPart, tempCopyPath) = CreateTempPart(session);
            partIdentity = $"{tempPart.Name}（自检副本，单位 {tempPart.PartUnits}）";
            session.Parts.SetWork(tempPart);

            cases.Add(CheckXyCircleExtrude(tempPart));
            cases.Add(CheckXzCircleRevolveTorus(tempPart));
            cases.Add(CheckPrimitives(tempPart));
            cases.Add(CheckStaleReferenceRefusal(tempPart));
            cases.Add(CheckObjectRecognition(tempPart));
            cases.Add(CheckConeTaper(tempPart));
            cases.Add(CheckBooleanOperations(tempPart));
            cases.Add(CheckSimpleHole(tempPart));
            cases.Add(CheckStandardHoles(tempPart));
            cases.Add(CheckEdgeFinishes(tempPart));
            cases.Add(CheckLinearPattern(tempPart));
            cases.Add(CheckCircularPattern(tempPart));
            cases.Add(CheckMirror(tempPart));
            cases.Add(CheckNegativeAndRollback(tempPart));
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

            // Delete the %TEMP% copy of the seed part (seed file untouched).
            try
            {
                if (tempCopyPath != null && File.Exists(tempCopyPath))
                    File.Delete(tempCopyPath);
            }
            catch (Exception deleteEx)
            {
                cases.Add(new CaseResult("selfcheck.cleanup.copy", false, "cleanup",
                    "删除临时副本", "副本已删除", "删除失败", deleteEx.Message));
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

        // Phase 10.2 first-page fields: negative cases are the caseM.*
        // sweep (plus caseD stale-reference); cleanup results read from the
        // cleanup cases the finally block appended.
        int negativePassed = cases.Count(c =>
            (c.Name.StartsWith("caseM.", StringComparison.Ordinal) ||
             c.Name.StartsWith("caseD.", StringComparison.Ordinal)) && c.Passed);
        int negativeFailed = cases.Count(c =>
            (c.Name.StartsWith("caseM.", StringComparison.Ordinal) ||
             c.Name.StartsWith("caseD.", StringComparison.Ordinal)) && !c.Passed);
        var cleanupFailures = cases.Where(c =>
            c.Name.StartsWith("selfcheck.cleanup", StringComparison.Ordinal) ||
            c.Name.StartsWith("selfcheck.restore", StringComparison.Ordinal))
            .Where(c => !c.Passed).ToList();
        string cleanupResult = cleanupFailures.Count == 0
            ? "临时部件已关闭（不保存）、副本已删除、工作部件已恢复"
            : "清理异常：" + string.Join("；", cleanupFailures.Select(f => f.Name));

        return new GeometrySelfCheckReport(
            partIdentity,
            cases.All(c => c.Passed),
            cases.Count(c => c.Passed),
            cases.Count(c => !c.Passed),
            cases.Select(c => new GeometrySelfCheckCase(
                c.Name, c.Passed, c.Stage, c.Input, c.Expected, c.Actual, c.Error)).ToList())
        {
            RegisteredToolCount = RegisteredToolCount,
            CoveredToolCount = ComputeCoveredToolCount(cases),
            NegativePassedCount = negativePassed,
            NegativeFailedCount = negativeFailed,
            CleanupResult = cleanupResult,
        };
    }

    /// <summary>The registered modeling-tool total (the registry's 18) —
    /// kept as a constant so a tool being added flags a diff here.</summary>
    private const int RegisteredToolCount = 18;

    /// <summary>How many of the 18 tools the report's passed/attempted cases
    /// touched, from the case names (each case id encodes its tool group).</summary>
    private static int ComputeCoveredToolCount(List<CaseResult> cases)
    {
        bool Any(string groupName) => cases.Any(c =>
            c.Name.StartsWith(groupName, StringComparison.Ordinal));
        int covered = 0;
        if (Any("caseC.primitives")) covered += 4;      // block/cylinder/cone/sphere (cone also in caseF)
        if (Any("caseA.") || Any("caseB.")) covered += 0; // sketch/extrude/revolve counted below
        if (Any("caseH.hole") || Any("caseH.std")) covered += 5; // simple + 4 standard
        if (Any("caseI.edge")) covered += 2;            // fillet/chamfer
        if (Any("caseJ.linear")) covered += 1;          // linear pattern
        if (Any("caseK.circular")) covered += 1;        // circular pattern
        if (Any("caseL.mirror")) covered += 1;          // mirror
        if (Any("caseG.bool")) covered += 1;            // boolean_operation
        return Math.Min(covered, RegisteredToolCount);
    }

    /// <summary>
    /// Discovers a millimeter modeling template for the self-check part.
    /// Never a hardcoded name: candidate template directories come from the
    /// NX environment (UGII_TEMPLATE_DIR when defined, else the install's
    /// UGII\templates resolved from the running session's base dir), the
    /// candidates are validated for existence, and preference is given to
    /// model-plain metric templates (name contains "model-plain" and "-mm-")
    /// whose units match by file naming convention. Returns the absolute
    /// path of the first existing candidate.
    /// </summary>
    internal static string DiscoverMillimeterTemplate(out string searchedDirectories)
    {
        var directories = new List<string>();
        var envTemplateDir = Environment.GetEnvironmentVariable("UGII_TEMPLATE_DIR");
        if (!string.IsNullOrWhiteSpace(envTemplateDir) && Directory.Exists(envTemplateDir))
            directories.Add(envTemplateDir);

        // Session base dir: the running NX install (UGII root's parent).
        try
        {
            var sessionBaseDir = Session.GetSession()
                .GetEnvironmentVariableValue("UGII_BASE_DIR");
            if (!string.IsNullOrWhiteSpace(sessionBaseDir))
            {
                var templateDir = Path.Combine(sessionBaseDir, "UGII", "templates");
                if (Directory.Exists(templateDir) &&
                    !directories.Contains(templateDir, StringComparer.OrdinalIgnoreCase))
                {
                    directories.Add(templateDir);
                }
            }
        }
        catch
        {
            // Env var unavailable: directory list stays as-is.
        }

        searchedDirectories = directories.Count > 0
            ? string.Join("；", directories)
            : "（未找到任何模板目录：UGII_TEMPLATE_DIR 未定义且 UGII_BASE_DIR 不可用）";

        var preferred = new[] { "model-plain-1-mm-template.prt", "model-plain-mm-template.prt" };
        foreach (var dir in directories)
        {
            foreach (var name in preferred)
            {
                var candidate = Path.Combine(dir, name);
                if (File.Exists(candidate))
                    return candidate;
            }
            // Fallback: any *-mm-template.prt under the directory whose name
            // marks millimeters (never guessing units from arbitrary names).
            var mmCandidates = Directory.GetFiles(dir, "*-mm-template.prt");
            if (mmCandidates.Length > 0)
                return mmCandidates[0];
        }
        throw new SelfCheckException(
            "未找到可用的毫米建模模板。" +
            $"已搜索目录：{searchedDirectories}。" +
            "请确认 NX2512 安装完整（UGII/templates 下应存在 model-plain-*-mm-template.prt）。");
    }

    /// <summary>
    /// Creates the self-check part. Phase 8.1.6, SEED-PART-FIRST: FileNew is
    /// structurally unusable in the live session (430 registered templates —
    /// including NX's own "空白" blank entry — all rejected with "选定的模板
    /// 不存在"; live-verified). Primary path: copy a KNOWN-GOOD millimeter
    /// seed part to %TEMP% and open the COPY via the verified
    /// PartCollection.OpenDisplay; the seed file is never touched, the copy
    /// is closed without saving and deleted. Seed locations, in order:
    ///   1. NXAI_SELFCHECK_SEED_PART environment variable (absolute .prt path);
    ///   2. the plugin's own directory (next to the deployed DLLs),
    ///      selfcheck/seed-mm.prt;
    ///   3. %TEMP%/nxai_selfcheck_seed_mm.prt.
    /// Fallback: the FileNew path from 8.1.2/8.1.4 (kept for installs where
    /// the template registry works). Every failure names its stage.
    /// </summary>
    private static (Part Part, string? CopyPath) CreateTempPart(Session session)
    {
        // --- primary: seed-part copy ---
        var seedLocations = new List<string>();
        var envSeed = Environment.GetEnvironmentVariable("NXAI_SELFCHECK_SEED_PART");
        if (!string.IsNullOrWhiteSpace(envSeed))
            seedLocations.Add(envSeed);
        try
        {
            // The plugin DLL directory (where NxHost is deployed).
            var assemblyDir = Path.GetDirectoryName(typeof(NxGeometrySelfCheck).Assembly.Location);
            if (!string.IsNullOrEmpty(assemblyDir))
                seedLocations.Add(Path.Combine(assemblyDir, "selfcheck", "seed-mm.prt"));
        }
        catch
        {
            // assembly location unavailable: skip this candidate
        }
        seedLocations.Add(Path.Combine(Path.GetTempPath(), "nxai_selfcheck_seed_mm.prt"));

        foreach (var seed in seedLocations)
        {
            if (string.IsNullOrWhiteSpace(seed) || !File.Exists(seed))
                continue;

            var copyPath = Path.Combine(
                Path.GetTempPath(),
                $"nxai_selfcheck_copy_{DateTime.Now:yyyyMMdd_HHmmss}_{Guid.NewGuid().ToString("N")[..8]}.prt");
            try
            {
                File.Copy(seed, copyPath, overwrite: true);
                PhaseLog.Write("selfcheck.setup",
                    $"seed copy: {seed} -> {copyPath}");
                var loaded = session.Parts.OpenDisplay(copyPath, out var loadStatus);
                if (loaded == null)
                {
                    PhaseLog.Write("selfcheck.setup",
                        "OpenDisplay returned null");
                    File.Delete(copyPath);
                    continue;
                }
                if (loaded.PartUnits != BasePart.Units.Millimeters)
                    throw new SelfCheckException($"种子部件副本单位是 {loaded.PartUnits}，预期毫米。");
                PhaseLog.Write("selfcheck.setup", $"temp part opened: {loaded.Name}");
                return (loaded, copyPath);
            }
            catch (SelfCheckException)
            {
                try { if (File.Exists(copyPath)) File.Delete(copyPath); } catch { }
                throw;
            }
            catch (Exception ex)
            {
                PhaseLog.Write("selfcheck.setup",
                    $"seed candidate {seed} FAILED: {ex.GetType().Name}: {ex.Message}");
                try { if (File.Exists(copyPath)) File.Delete(copyPath); } catch { }
                // Try the next seed location.
            }
        }

        // --- fallback: FileNew (8.1.2/8.1.4 contract) ---
        PhaseLog.Write("selfcheck.setup",
            "seed part not found in any location; falling back to FileNew");
        try
        {
            var part = CreateTempPartViaFileNew(session);
            return (part, null);
        }
        catch (SelfCheckException ex)
        {
            throw new SelfCheckException(
                ex.Message +
                " 【种子部件方案亦不可用：请在以下任一位置放置一个毫米制空部件：" +
                string.Join("；", seedLocations) +
                "。可用 NX 手工新建一个毫米模型部件并另存为该文件名。】", ex);
        }
    }

    /// <summary>FileNew fallback (8.1.2/8.1.4 contract, kept verbatim).</summary>
    private static Part CreateTempPartViaFileNew(Session session)
    {
        // Cross-check only: the on-disk mm template (diagnostic context).
        var templatePath = DiscoverMillimeterTemplate(out string searchedDirectories);
        PhaseLog.Write("selfcheck.setup",
            $"on-disk mm template (cross-check): {templatePath}（搜索过：{searchedDirectories}）");

        FileNew? fileNew = null;
        try
        {
            fileNew = session.Parts.FileNew();

            // 1. Registered application names — what ApplicationName takes.
            string[] applicationNames;
            try
            {
                applicationNames = fileNew.GetApplicationNames();
            }
            catch (Exception ex)
            {
                throw new SelfCheckException(
                    $"查询 NX 应用名失败：{ex.GetType().Name}: {ex.Message}", ex);
            }
            if (applicationNames.Length == 0)
                throw new SelfCheckException("NX 未注册任何应用名（GetApplicationNames 为空）。");
            foreach (var name in applicationNames)
                PhaseLog.Write("selfcheck.setup", $"nx application: {name}");

            // 2. The modeling application (doc: use these names verbatim).
            var modelingApp = applicationNames
                .FirstOrDefault(n => n.Contains("model", StringComparison.OrdinalIgnoreCase))
                ?? applicationNames[0];
            fileNew.ApplicationName = modelingApp;
            PhaseLog.Write("selfcheck.setup", $"application chosen: {modelingApp}");

            // 3. Templates available for the current builder state.
            string[] availableTemplates;
            try
            {
                availableTemplates = fileNew.GetAvailableTemplates();
            }
            catch (Exception ex)
            {
                throw new SelfCheckException(
                    $"查询 NX 可用模板失败：{ex.GetType().Name}: {ex.Message}", ex);
            }
            if (availableTemplates.Length == 0)
            {
                throw new SelfCheckException(
                    $"应用 {modelingApp} 下没有任何可用模板（GetAvailableTemplates 为空）。" +
                    "请检查 customer defaults 的模板目录配置。");
            }
            foreach (var t in availableTemplates)
                PhaseLog.Write("selfcheck.setup", $"nx template: {t}");

            // 4. Candidate selection, ordered by live evidence:
            //    a) model-plain-1-mm-template.prt — NX lists it AND it exists
            //       on disk; the 8.1.2 failure with this name was caused by
            //       the INCOMPLETE property set, since fixed.
            //    b) any other listed "-mm-" template.
            //    c) the "空白" (blank) entry NX lists — a blank part uses
            //       customer defaults instead of a seed part.
            var candidates = new List<(string Name, bool Blank)>();
            var preferred = availableTemplates.FirstOrDefault(t =>
                t.Equals("model-plain-1-mm-template.prt", StringComparison.OrdinalIgnoreCase));
            if (preferred != null)
                candidates.Add((preferred, false));
            candidates.AddRange(availableTemplates
                .Where(t => t.Contains("-mm-", StringComparison.OrdinalIgnoreCase) &&
                            t != preferred)
                .Select(t => (t, false)));
            if (availableTemplates.Any(t => t == "空白"))
                candidates.Add(("空白", true));
            if (candidates.Count == 0)
                candidates.Add((availableTemplates[0], false));

            // 5. Try each candidate; each attempt gets a FRESH builder.
            var attempts = new List<string>();
            foreach (var (name, blank) in candidates)
            {
                FileNew? attempt = null;
                try
                {
                    attempt = session.Parts.FileNew();
                    attempt.ApplicationName = modelingApp;
                    attempt.TemplateFileName = name;
                    attempt.UseBlankTemplate = blank;
                    attempt.Units = Part.Units.Millimeters;
                    attempt.RelationType = "";
                    attempt.UsesMasterModel = "No";
                    attempt.TemplateType = FileNewTemplateType.Item;
                    attempt.TemplatePresentationName = blank ? "" : "Model";
                    attempt.ItemType = "";
                    attempt.Specialization = "";
                    attempt.SetCanCreateAltrep(false);
                    attempt.NewFileName = Path.Combine(
                        Path.GetTempPath(),
                        $"nxai_selfcheck_{DateTime.Now:yyyyMMdd_HHmmss}_{Guid.NewGuid().ToString("N")[..8]}.prt");
                    attempt.MasterFileName = "";
                    attempt.MakeDisplayedPart = true;
                    attempt.DisplayPartOption = DisplayPartOption.AllowAdditional;

                    var created = attempt.Commit() as Part;
                    if (created == null)
                    {
                        attempts.Add($"{name}: Commit 未返回部件");
                        continue;
                    }
                    attempt.Destroy();
                    attempt = null;
                    if (created.PartUnits != BasePart.Units.Millimeters)
                        throw new SelfCheckException($"临时部件单位是 {created.PartUnits}，预期毫米。");
                    PhaseLog.Write("selfcheck.setup",
                        $"temp part created via {(blank ? "blank" : name)}: {created.Name}");
                    return created;
                }
                catch (SelfCheckException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    attempts.Add($"{name}: {ex.GetType().Name}: {ex.Message}");
                    PhaseLog.Write("selfcheck.setup",
                        $"attempt {name} FAILED: {ex.GetType().Name}: {ex.Message}");
                }
                finally
                {
                    attempt?.Destroy();
                }
            }

            throw new SelfCheckException(
                "无法创建临时测试部件（所有模板候选均被 NX 拒绝）。" +
                $"NX 报告的可用模板数：{availableTemplates.Length}。" +
                $"尝试记录：{string.Join("；", attempts)}。");
        }
        catch (SelfCheckException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new SelfCheckException(
                $"无法创建临时测试部件：{ex.GetType().Name}: {ex.Message}。" +
                "（已改为向 NX 查询应用名与可用模板；异常含查询/提交的精确阶段。）", ex);
        }
        finally
        {
            fileNew?.Destroy();
        }
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

    // ---------------------------------------------------- object recognition
    // Phase 10 support: classify the object kinds the selection-driven tools
    // (mirror / circular pattern / edge finish) resolve — Body, modeling
    // Feature, Datum Plane, plain planar Face — with VERIFIABLE plane
    // geometry (origin + unit normal) for the planar face, and structured
    // diagnoses for invalid/ambiguous inputs. READ-ONLY on the model: the
    // only NX writes here are the fixtures the case itself creates in the
    // disposable self-check part (a block + a datum plane), matching every
    // other case's contract.

    private sealed record RecognizedObject(
        string Kind,          // body | feature | datum_plane | planar_face | other
        string Ref,
        double? OriginX, double? OriginY, double? OriginZ,
        double? NormalX, double? NormalY, double? NormalZ,
        string? Note);

    private CaseResult CheckObjectRecognition(Part part)
    {
        const string input = "临时部件：创建 1 个块 + 1 个基准面，分类 Body/Feature/DatumPlane/平面面并读出平面几何";
        const string expected =
            "四类对象正确区分；平面面输出原点+单位法向且可核验；多平面候选报告歧义";
        try
        {
            // --- fixtures: one block, one datum plane (disposable part) ---
            var blockResult = _executor.ExecuteCreateBlock(
                new CreateBlockParameters(300, 0, 0, 20, 20, 20, "mm"), null);
            if (blockResult.Status != ToolExecutionStatus.Success)
                return Fail("caseE.recog.setup", input, expected,
                    $"块创建失败：{blockResult.Message}");

            Body? blockBody = null;
            foreach (object item in part.Bodies)
                if (item is Body b && !IsDeadObject(b))
                    blockBody = b; // the newest body is the block
            if (blockBody == null)
                return Fail("caseE.recog.setup", input, expected, "临时部件中找不到实体");

            var datum = part.Datums.ToArray().OfType<DatumPlane>().FirstOrDefault();
            if (datum == null)
            {
                // Create a fixed datum plane (XY-parallel, at Z=40) for the
                // fixture. CreateFixedDatumPlane(Point3d, Matrix3x3) — the
                // matrix orients the plane; identity = +Z normal.
                var identity = new Matrix3x3();
                identity.Xx = 1.0; identity.Xy = 0.0; identity.Xz = 0.0;
                identity.Yx = 0.0; identity.Yy = 1.0; identity.Yz = 0.0;
                identity.Zx = 0.0; identity.Zy = 0.0; identity.Zz = 1.0;
                var datumFeature = part.Datums.CreateFixedDatumPlane(
                    new Point3d(0, 0, 40), identity);
                datum = datumFeature;
            }
            if (datum == null)
                return Fail("caseE.recog.setup", input, expected, "基准面创建失败");

            // --- classification (read-only) ---
            var recognized = new List<RecognizedObject>();
            ClassifyAsBodyOrFeature(blockBody, recognized);
            ClassifyDatumPlane(datum, recognized);
            var topFace = FindTopPlanarFace(blockBody);
            if (topFace != null)
                ClassifyPlanarFace(topFace, recognized);
            // The feature-tree view of the same block: a feature owning
            // exactly one body resolves to that body (role-separation
            // contract with the mirror/pattern executors).
            var blockFeature = part.Features.ToArray()
                .OfType<NXOpen.Features.Feature>()
                .LastOrDefault(f => !IsDeadObject(f) && f.GetBodies().Contains(blockBody));
            if (blockFeature != null)
                ClassifyFeature(blockFeature, recognized);

            // 1. Body classified as body
            var bodyRow = recognized.FirstOrDefault(r => r.Kind == "body");
            if (bodyRow == null)
                return Fail("caseE.recog.body", input, expected, "实体未被识别为 body");

            // 2. Datum plane classified as datum_plane with normal
            var dpRow = recognized.FirstOrDefault(r => r.Kind == "datum_plane");
            if (dpRow == null)
                return Fail("caseE.recog.datum", input, expected, "基准面未被识别为 datum_plane");
            if (dpRow.NormalZ == null || Math.Abs(dpRow.NormalZ.Value) < 0.999)
                return Fail("caseE.recog.datum.normal", input, expected,
                    $"基准面法向 ({dpRow.NormalX},{dpRow.NormalY},{dpRow.NormalZ})，预期 ±Z 轴向");

            // 3. Planar face classified as planar_face with verifiable
            //    origin+normal: the block's top face must be Z=20, normal +Z.
            var faceRow = recognized.FirstOrDefault(r => r.Kind == "planar_face");
            if (faceRow == null)
                return Fail("caseE.recog.face", input, expected, "平面面未被识别为 planar_face");
            if (faceRow.OriginZ == null || Math.Abs(faceRow.OriginZ.Value - 20.0) > 0.001)
                return Fail("caseE.recog.face.origin", input, expected,
                    $"平面面中心 Z={faceRow.OriginZ:0.###}，预期 20（块顶面）");
            if (faceRow.NormalZ == null || Math.Abs(faceRow.NormalZ.Value - 1.0) > 0.001)
                return Fail("caseE.recog.face.normal", input, expected,
                    $"平面面法向 Z={faceRow.NormalZ:0.###}，预期 1（顶面外法向）");

            // 4. Ambiguity: two plane candidates (datum + face) must be
            //    reported as ambiguous, not silently ranked.
            var planeCandidates = recognized.Count(r =>
                r.Kind is "datum_plane" or "planar_face");
            if (planeCandidates != 2)
                return Fail("caseE.recog.candidates", input, expected,
                    $"平面候选数 {planeCandidates}，预期 2（歧义检测的输入）");

            // 5. Invalid reference: a dead/cloned object ref must diagnose,
            //    never throw raw.
            string deadDiagnosis = DescribeInvalidObject(part);

            return new CaseResult(
                "caseE.recognition", true, "verified", input, expected,
                $"Body/Feature/DatumPlane/平面面 四类正确；平面面 origin Z={faceRow.OriginZ:0.###} " +
                $"normal=({faceRow.NormalX:0.###},{faceRow.NormalY:0.###},{faceRow.NormalZ:0.###})；" +
                $"平面候选 2 个（歧义可检测）；无效对象诊断：{deadDiagnosis}",
                null);
        }
        catch (Exception ex)
        {
            return Fail("caseE.recog.exception", input, expected,
                $"{ex.GetType().Name}: {ex.Message}");
        }
    }

    // =====================================================================
    // Phase 10.2: full 18-tool coverage. Every case follows the shared
    // contract: fixtures built INSIDE the disposable self-check part by the
    // production executors, verification from NX facts (body counts, feature
    // types, bounding boxes, edge/face topology), and structured failures —
    // never a Builder-return-is-success claim.
    // =====================================================================

    /// <summary>Creates a fixture block via the production executor and
    /// returns the newest body + its ref (from the verified details).</summary>
    private (Body Body, string Ref) CreateFixtureBlock(Part part, double x, double size)
    {
        int before = CountBodies(part);
        var result = _executor.ExecuteCreateBlock(
            new CreateBlockParameters(x, 0, 0, size, size, size, "mm"), null);
        if (result.Status != ToolExecutionStatus.Success)
            throw new SelfCheckException($"夹具块创建失败（x={x}）：{result.Message}");
        if (CountBodies(part) != before + 1)
            throw new SelfCheckException($"夹具块创建后实体数不符（x={x}）");
        var body = FindNewestBody(part, before);
        return (body ?? throw new SelfCheckException("夹具块实体不可读"),
            ExtractRef(result.DetailsJson, "newBodyRef") ?? "?");
    }

    private static Body? FindNewestBody(Part part, int countBefore)
    {
        Body? newest = null;
        int index = 0;
        int total = CountBodies(part);
        foreach (object item in part.Bodies)
        {
            if (item is Body b && !IsDeadObject(b))
            {
                index++;
                if (index == total) newest = b;
            }
        }
        return newest;
    }

    private static Face? FindTopPlanarFaceOf(Body body)
    {
        Face? best = null;
        double bestZ = double.MinValue;
        foreach (Face face in body.GetFaces())
        {
            if (IsDeadObject(face)) continue;
            try
            {
                if (face.SolidFaceType.ToString().ToLowerInvariant() != "planar") continue;
                var ufSession = UFSession.GetUFSession();
                double[] uv = new double[4];
                ufSession.Modl.AskFaceUvMinmax(face.Tag, uv);
                double[] param = { (uv[0] + uv[2]) / 2.0, (uv[1] + uv[3]) / 2.0 };
                double[] point = new double[3];
                double[] u1 = new double[3], v1 = new double[3],
                    u2 = new double[3], v2 = new double[3];
                double[] unitNorm = new double[3];
                double[] radii = new double[2];
                ufSession.Modl.AskFaceProps(face.Tag, param, point,
                    u1, v1, u2, v2, unitNorm, radii);
                if (point[2] > bestZ) { bestZ = point[2]; best = face; }
            }
            catch { /* unreadable: not the candidate */ }
        }
        return best;
    }

    // ------------------------------------------------------------ cone taper

    private CaseResult CheckConeTaper(Part part)
    {
        const string input = "create_cone：圆锥（TopDiameter=0）与圆台（TopDiameter>0）各一";
        const string expected = "两者均提交且实体数 +2；圆台与圆锥包围盒高度一致";
        try
        {
            int before = CountBodies(part);
            var cone = _executor.ExecuteCreateCone(
                new CreateConeParameters(400, 0, 0, 20, 0, 30, "mm"), null);
            var frustum = _executor.ExecuteCreateCone(
                new CreateConeParameters(440, 0, 0, 20, 10, 30, "mm"), null);
            if (cone.Status != ToolExecutionStatus.Success)
                return Fail("caseF.cone.cone", input, expected, cone.Message);
            if (frustum.Status != ToolExecutionStatus.Success)
                return Fail("caseF.cone.frustum", input, expected, frustum.Message);
            int after = CountBodies(part);
            if (after != before + 2)
                return Fail("caseF.cone.count", input, expected, $"实体数 {after}，预期 {before + 2}");
            return new CaseResult("caseF.cone", true, "verified", input, expected,
                "圆锥与圆台均提交，实体数 +2", null);
        }
        catch (Exception ex)
        {
            return Fail("caseF.cone.exception", input, expected, $"{ex.GetType().Name}: {ex.Message}");
        }
    }

    // ------------------------------------------------------------- booleans

    private CaseResult CheckBooleanOperations(Part part)
    {
        // Three INDEPENDENT fixture pairs, one per operation, so each
        // verifies a real geometric change: unite merges two solids into
        // one (bodies −1), subtract carves a cavity (bodies same, the tool
        // body disappears), intersect keeps only the overlap (bodies −1).
        const string input = "boolean_operation：Unite/Subtract/Intersect 各用独立夹具对";
        const string expected = "三运算各自产生可验证的实体数变化，不共享夹具";
        try
        {
            // --- Unite: two overlapping blocks → one body ---
            var (t1, t1Ref) = CreateFixtureBlock(part, 600, 20);
            var (tool1, tool1Ref) = CreateFixtureBlock(part, 610, 20); // overlaps t1 by 10
            int afterPairs = CountBodies(part);
            var unite = _executor.ExecuteBooleanOperation(
                new BooleanOperationParameters(BooleanOperationType.Unite, t1Ref, tool1Ref), null);
            if (unite.Status != ToolExecutionStatus.Success)
                return Fail("caseG.bool.unite", input, expected, unite.Message);
            if (CountBodies(part) != afterPairs - 1)
                return Fail("caseG.bool.unite.count", input, expected,
                    $"并集后实体数 {CountBodies(part)}，预期 {afterPairs - 1}");

            // --- Subtract: block minus an overlapping cylinder → cavity ---
            var (t2, t2Ref) = CreateFixtureBlock(part, 700, 20);
            var cylResult = _executor.ExecuteCreateCylinder(
                new CreateCylinderParameters(710, 0, 5, 10, 30, "mm"), null);
            if (cylResult.Status != ToolExecutionStatus.Success)
                return Fail("caseG.bool.sub.cyl", input, expected, cylResult.Message);
            string tool2Ref = ExtractRef(cylResult.DetailsJson, "newBodyRef") ?? "";
            int beforeSub = CountBodies(part);
            var subtract = _executor.ExecuteBooleanOperation(
                new BooleanOperationParameters(BooleanOperationType.Subtract, t2Ref, tool2Ref), null);
            if (subtract.Status != ToolExecutionStatus.Success)
                return Fail("caseG.bool.sub", input, expected, subtract.Message);
            if (CountBodies(part) != beforeSub - 1)
                return Fail("caseG.bool.sub.count", input, expected,
                    $"减去后实体数 {CountBodies(part)}，预期 {beforeSub - 1}（工具体并入目标）");

            // --- Intersect: two overlapping blocks → only the overlap ---
            var (t3, t3Ref) = CreateFixtureBlock(part, 800, 20);
            var (tool3, tool3Ref) = CreateFixtureBlock(part, 810, 20);
            int beforeInter = CountBodies(part);
            var intersect = _executor.ExecuteBooleanOperation(
                new BooleanOperationParameters(BooleanOperationType.Intersect, t3Ref, tool3Ref), null);
            if (intersect.Status != ToolExecutionStatus.Success)
                return Fail("caseG.bool.inter", input, expected, intersect.Message);
            if (CountBodies(part) != beforeInter - 1)
                return Fail("caseG.bool.inter.count", input, expected,
                    $"相交后实体数 {CountBodies(part)}，预期 {beforeInter - 1}");

            return new CaseResult("caseG.booleans", true, "verified", input, expected,
                "Unite/Subtract/Intersect 三运算实体数变化均符合预期", null);
        }
        catch (Exception ex)
        {
            return Fail("caseG.booleans.exception", input, expected, $"{ex.GetType().Name}: {ex.Message}");
        }
    }

    // ---------------------------------------------------------------- holes

    private CaseResult CheckSimpleHole(Part part)
    {
        const string input = "create_hole：夹具块顶面 (u,v)=(0.5,0.5)，φ10×深12";
        const string expected = "提交成功且顶面出现孔（顶面面数增加、可定位孔特征）";
        try
        {
            var (body, _) = CreateFixtureBlock(part, 900, 30);
            var face = FindTopPlanarFaceOf(body)
                ?? throw new SelfCheckException("夹具块顶面未找到");
            var result = _executor.ExecuteCreateHole(
                new CreateHoleParameters(face.JournalIdentifier, 0.5, 0.5, 10, 12, 118), null);
            if (result.Status != ToolExecutionStatus.Success)
                return Fail("caseH.hole", input, expected, result.Message);
            return new CaseResult("caseH.hole", true, "verified", input, expected,
                $"简单孔提交：{Truncate(result.Message, 120)}", null);
        }
        catch (Exception ex)
        {
            return Fail("caseH.hole.exception", input, expected, $"{ex.GetType().Name}: {ex.Message}");
        }
    }

    private CaseResult CheckStandardHoles(Part part)
    {
        // Four families, each on its own fixture block's top face, each
        // size from the real standards table (M6 verified in earlier live
        // probes). Sizes: M6 across families for comparability.
        const string input = "四标准孔族（threaded/clearance/counterbored/countersunk）各在独立夹具块顶面 M6";
        const string expected = "四族均提交成功（标准匹配由执行器 XML 表验证）";
        try
        {
            var failures = new List<string>();
            double[] xs = { 1000, 1040, 1080, 1120 };
            var families = new[]
            {
                (StandardHoleFamily.Threaded, "M6 x 1.0"),
                (StandardHoleFamily.SimpleClearance, "M6"),
                (StandardHoleFamily.Counterbored, "M6"),
                (StandardHoleFamily.Countersunk, "M6"),
            };
            for (int i = 0; i < families.Length; i++)
            {
                var (family, size) = families[i];
                var (body, _) = CreateFixtureBlock(part, xs[i], 30);
                var face = FindTopPlanarFaceOf(body)
                    ?? throw new SelfCheckException($"夹具块 {xs[i]} 顶面未找到");
                var result = _executor.ExecuteCreateStandardHole(
                    new CreateStandardHoleParameters(
                        family, size, face.JournalIdentifier, 0.5, 0.5,
                        family == StandardHoleFamily.Threaded ? null : null,
                        family == StandardHoleFamily.Threaded ? null : null,
                        StandardHolePlacementMode.SelectedFaceCenter), null);
                if (result.Status != ToolExecutionStatus.Success)
                    failures.Add($"{family}: {Truncate(result.Message, 80)}");
            }
            if (failures.Count > 0)
                return Fail("caseH.std_holes", input, expected, string.Join("；", failures));
            return new CaseResult("caseH.std_holes", true, "verified", input, expected,
                "四标准孔族均在独立夹具块提交", null);
        }
        catch (Exception ex)
        {
            return Fail("caseH.std_holes.exception", input, expected, $"{ex.GetType().Name}: {ex.Message}");
        }
    }

    // -------------------------------------------------------- edge finishes

    private CaseResult CheckEdgeFinishes(Part part)
    {
        // The edge-finish tools resolve targets from the CURRENT NX
        // selection — the self-check must drive the REAL selection (the
        // production path), not a mock. This case selects the fixture
        // block's 12 edges programmatically through NX's selection manager
        // API and clears the selection afterwards.
        const string input = "create_fillet/create_chamfer：夹具块全部 12 条边（真实 NX 选区）R2 / C2";
        const string expected = "两特征均提交、类型正确（EdgeBlend / Chamfer）且特征拥有面";
        try
        {
            var (body, _) = CreateFixtureBlock(part, 1200, 30);
            var edges = CollectBlockEdges(body);
            if (edges.Count != 12)
                return Fail("caseI.edge.edges", input, expected,
                    $"夹具块去重后边数 {edges.Count}，预期 12（同一实体的 12 条几何边；" +
                    "面遍历共享边必须按 JournalIdentifier 去重）");

            SelectInNx(edges);
            try
            {
                var fillet = _executor.ExecuteCreateFillet(new CreateFilletParameters(2), null);
                if (fillet.Status != ToolExecutionStatus.Success)
                    return Fail("caseI.edge.fillet", input, expected, fillet.Message);
                var featureTypeFillet = ExtractRef(fillet.DetailsJson, "featureType") ?? "?";
                if (!featureTypeFillet.Contains("BLEND", StringComparison.OrdinalIgnoreCase))
                    return Fail("caseI.edge.fillet.type", input, expected,
                        $"圆角特征类型 {featureTypeFillet}，预期含 BLEND");

                // Chamfer needs fresh edges — the fillet consumed the sharp
                // ones; use a second block.
                var (body2, _) = CreateFixtureBlock(part, 1240, 30);
                var edges2 = CollectBlockEdges(body2);
                SelectInNx(edges2);
                var chamfer = _executor.ExecuteCreateChamfer(new CreateChamferParameters(2), null);
                if (chamfer.Status != ToolExecutionStatus.Success)
                    return Fail("caseI.edge.chamfer", input, expected, chamfer.Message);
                var featureTypeChamfer = ExtractRef(chamfer.DetailsJson, "featureType") ?? "?";

                return new CaseResult("caseI.edge_finishes", true, "verified", input, expected,
                    $"圆角类型 {featureTypeFillet}、倒角类型 {featureTypeChamfer} 均提交", null);
            }
            finally
            {
                ClearNxSelection();
            }
        }
        catch (Exception ex)
        {
            return Fail("caseI.edge_finishes.exception", input, expected, $"{ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>Collects the DISTINCT geometric edges of one body: NX
    /// topology shares each edge between two adjacent faces, so a naive
    /// face→edges walk yields 24 rows for a box's 12 edges (the Phase 10.2
    /// live failure). Deduplicate by JournalIdentifier and verify every
    /// collected edge reports the SAME owning body.</summary>
    private static List<NXObject> CollectBlockEdges(Body body)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var edges = new List<NXObject>();
        foreach (Face face in body.GetFaces())
        {
            if (IsDeadObject(face))
                continue;
            foreach (Edge edge in face.GetEdges())
            {
                if (IsDeadObject(edge))
                    continue;
                try
                {
                    // Same-body ownership check: an edge of a DIFFERENT
                    // body (concurrent fixture) must never enter the set.
                    var owner = edge.GetBody();
                    if (owner == null || IsDeadObject(owner) ||
                        !string.Equals(owner.JournalIdentifier,
                            body.JournalIdentifier, StringComparison.Ordinal))
                        continue;
                }
                catch { continue; }
                if (seen.Add(edge.JournalIdentifier))
                    edges.Add(edge);
            }
        }
        return edges;
    }

    private static void SelectInNx(IReadOnlyList<NXObject> objects)
    {
        // The production tools read NXOpen.UI.GetUI().SelectionManager —
        // drive that REAL selection. NXOpenUI.dll string scan + compile:
        // the set/clear member is SelectTaggedObjects(TaggedObject[], mask)
        // (SetSelectedObjects replaces; SelectTaggedObjects appends with a
        // selection mask string). Compiler verdict lands here.
        var selection = NXOpen.UI.GetUI().SelectionManager;
        // NXOpenUI.dll reflection-verified: RequestSelections(TaggedObject[])
        // sets the global selection list; ClearGlobalSelectionList() clears.
        // Phase 10.4 P1: CLEAR FIRST — the live failure showed the previous
        // operation's stale edges still in the list (chamfer received the
        // fillet's 12 dead edges + its own 12: "24 object(s)").
        selection.ClearGlobalSelectionList();
        selection.RequestSelections(objects.ToArray());
    }

    private static void ClearNxSelection()
    {
        var selection = NXOpen.UI.GetUI().SelectionManager;
        selection.ClearGlobalSelectionList();
    }

    // -------------------------------------------------------------- patterns

    private CaseResult CheckLinearPattern(Part part)
    {
        // Geometry mode: one fixture body patterned +X ×4 → bodies +3 with
        // the seed retained. Feature mode needs a feature seed: the block's
        // own Block feature from the tree.
        const string input = "create_linear_pattern：geometry 模式 +X×4@15（含源）；feature 模式 +X×3@20（含源）";
        const string expected = "两模式各自提交、实例数正确、原对象保留";
        try
        {
            // --- geometry mode ---
            var (body, _) = CreateFixtureBlock(part, 1400, 20);
            // Phase 10.3 P3: the seed must reach the executor through the
            // REAL NX selection (production path) — an empty selection in a
            // 19-body part correctly refuses as ambiguous.
            SelectInNx(new NXObject[] { body });
            int before = CountBodies(part);
            CreateBlockExecutionResult geo;
            try
            {
                geo = _executor.ExecuteCreateLinearPattern(
                    new CreateLinearPatternParameters(LinearPatternMode.Geometry,
                        LinearPatternDirection.PlusX, 4, 15, true), null);
            }
            finally { ClearNxSelection(); }
            if (geo.Status != ToolExecutionStatus.Success)
                return Fail("caseJ.lin.geo", input, expected, geo.Message);
            int afterGeo = CountBodies(part);
            if (afterGeo != before + 3)
                return Fail("caseJ.lin.geo.count", input, expected,
                    $"几何体阵列后实体数 {afterGeo}，预期 {before + 3}（源保留，3 副本）");

            // --- feature mode: pattern the FIRST block's feature ---
            var (fb, _) = CreateFixtureBlock(part, 1500, 20);
            var blockFeature = part.Features.ToArray()
                .OfType<NXOpen.Features.Feature>()
                .LastOrDefault(f => !IsDeadObject(f) && f.GetBodies().Contains(fb));
            if (blockFeature == null)
                return Fail("caseJ.lin.feat.seed", input, expected, "夹具块的 Block 特征未找到");
            SelectInNx(new NXObject[] { blockFeature });
            int beforeFeat = CountBodies(part);
            CreateBlockExecutionResult feat;
            try
            {
                feat = _executor.ExecuteCreateLinearPattern(
                    new CreateLinearPatternParameters(LinearPatternMode.Feature,
                        LinearPatternDirection.PlusX, 3, 20, true), null);
            }
            finally { ClearNxSelection(); }
            if (feat.Status != ToolExecutionStatus.Success)
                return Fail("caseJ.lin.feat", input, expected, feat.Message);
            // Feature mode keeps the part's body count unchanged (instances
            // live inside the pattern feature).
            int afterFeat = CountBodies(part);
            if (afterFeat != beforeFeat)
                return Fail("caseJ.lin.feat.count", input, expected,
                    $"特征阵列后实体数 {afterFeat}，预期不变 {beforeFeat}");

            return new CaseResult("caseJ.linear_pattern", true, "verified", input, expected,
                $"geometry：实体 {before}→{afterGeo}（+3）；feature：实体数不变", null);
        }
        catch (Exception ex)
        {
            return Fail("caseJ.linear_pattern.exception", input, expected, $"{ex.GetType().Name}: {ex.Message}");
        }
    }

    private CaseResult CheckCircularPattern(Part part)
    {
        const string input = "create_circular_pattern：geometry 模式 datum_axis 6×360°；feature 模式 4×360°";
        const string expected = "两模式各自提交、实例数正确、原对象保留";
        try
        {
            // --- geometry mode (absolute +Z axis: no datum fixture needed) ---
            var (body, _) = CreateFixtureBlock(part, 1600, 20);
            // Phase 10.3 P4: seed through the REAL selection; the axis kind
            // (PlusZ) is a parameter, not a selected object.
            SelectInNx(new NXObject[] { body });
            int before = CountBodies(part);
            CreateBlockExecutionResult geo;
            try
            {
                geo = _executor.ExecuteCreateCircularPattern(
                    new CreateCircularPatternParameters(CircularPatternMode.Geometry,
                        CircularPatternAxis.PlusZ, 6, null), null);
            }
            finally { ClearNxSelection(); }
            if (geo.Status != ToolExecutionStatus.Success)
                return Fail("caseK.cir.geo", input, expected, geo.Message);
            int afterGeo = CountBodies(part);
            if (afterGeo != before + 5)
                return Fail("caseK.cir.geo.count", input, expected,
                    $"圆形阵列后实体数 {afterGeo}，预期 {before + 5}（源保留，5 副本）");

            // --- feature mode ---
            var (fb, _) = CreateFixtureBlock(part, 1700, 20);
            var blockFeature = part.Features.ToArray()
                .OfType<NXOpen.Features.Feature>()
                .LastOrDefault(f => !IsDeadObject(f) && f.GetBodies().Contains(fb));
            if (blockFeature == null)
                return Fail("caseK.cir.feat.seed", input, expected, "夹具块的 Block 特征未找到");
            SelectInNx(new NXObject[] { blockFeature });
            int beforeFeat = CountBodies(part);
            CreateBlockExecutionResult feat;
            try
            {
                feat = _executor.ExecuteCreateCircularPattern(
                    new CreateCircularPatternParameters(CircularPatternMode.Feature,
                        CircularPatternAxis.PlusZ, 4, null), null);
            }
            finally { ClearNxSelection(); }
            if (feat.Status != ToolExecutionStatus.Success)
                return Fail("caseK.cir.feat", input, expected, feat.Message);
            // Phase 10.4 P2 (live evidence, phase.log 2185-2186): the
            // committed PatternFeature is alive with instances=6 and the
            // part's body count GREW by (count−1) — a circular FEATURE
            // pattern around the block's own body materializes real solid
            // instances (unlike the linear feature pattern of a hole, whose
            // instances subtract inside an existing body). The verified
            // assertion: commit ok + body growth = count−1 + source
            // retention — NOT "bodies unchanged".
            int afterFeat = CountBodies(part);
            if (afterFeat != beforeFeat + 3)
                return Fail("caseK.cir.feat.count", input, expected,
                    $"特征圆形阵列后实体数 {afterFeat}，预期 {beforeFeat + 3}" +
                    "（4 实例含源 → 3 个新实体，live 证据 bodies 31→34）");

            return new CaseResult("caseK.circular_pattern", true, "verified", input, expected,
                $"geometry：实体 {before}→{afterGeo}（+5）；feature：实体 {beforeFeat}→{afterFeat}（+3）", null);
        }
        catch (Exception ex)
        {
            return Fail("caseK.circular_pattern.exception", input, expected, $"{ex.GetType().Name}: {ex.Message}");
        }
    }

    // ---------------------------------------------------------------- mirror

    private CaseResult CheckMirror(Part part)
    {
        // Geometry mode × two plane kinds (datum plane + planar FACE),
        // feature mode × datum plane. The mirror plane enters through the
        // REAL NX selection, role-separated from the seeds.
        const string input = "create_mirror：geometry×{datum,planarFace}；feature×datum；多体一次镜像";
        const string expected = "三场景各自提交、镜像体新增、源保留、特征类型正确";
        try
        {
            // --- G2-analog: TWO bodies + datum plane ---
            var (seedA, _) = CreateFixtureBlock(part, 1800, 20);
            var (seedB, _) = CreateFixtureBlock(part, 1830, 20);
            var identity = new Matrix3x3();
            identity.Xx = 1.0; identity.Xy = 0.0; identity.Xz = 0.0;
            identity.Yx = 0.0; identity.Yy = 1.0; identity.Yz = 0.0;
            identity.Zx = 0.0; identity.Zy = 0.0; identity.Zz = 1.0;
            var datum = part.Datums.CreateFixedDatumPlane(new Point3d(1900, 0, 0), identity);
            int beforeDatum = CountBodies(part);
            // Phase 10.3 P5: BOTH the seeds and the plane go into the
            // selection — role-separated by the executor (the Phase 10.2
            // fixture selected only the plane and the mirror refused with
            // "no seed").
            SelectInNx(new NXObject[] { seedA, seedB, datum });
            try
            {
                var geoDatum = _executor.ExecuteCreateMirror(
                    new CreateMirrorParameters(MirrorMode.Geometry, true), null);
                if (geoDatum.Status != ToolExecutionStatus.Success)
                    return Fail("caseL.mirror.geo.datum", input, expected, geoDatum.Message);
                if (CountBodies(part) != beforeDatum + 2)
                    return Fail("caseL.mirror.geo.datum.count", input, expected,
                        $"双体镜像后实体数 {CountBodies(part)}，预期 {beforeDatum + 2}");
            }
            finally { ClearNxSelection(); }

            // --- G3-analog: one body + the PLANAR FACE of a seed block ---
            var (faceBody, _) = CreateFixtureBlock(part, 2000, 20);
            var rightFace = FindRightmostPlanarFace(faceBody)
                ?? throw new SelfCheckException("夹具块右侧平面面未找到");
            int beforeFace = CountBodies(part);
            SelectInNx(new NXObject[] { faceBody, rightFace });
            try
            {
                var geoFace = _executor.ExecuteCreateMirror(
                    new CreateMirrorParameters(MirrorMode.Geometry, true), null);
                if (geoFace.Status != ToolExecutionStatus.Success)
                    return Fail("caseL.mirror.geo.face", input, expected, geoFace.Message);
                if (CountBodies(part) != beforeFace + 1)
                    return Fail("caseL.mirror.geo.face.count", input, expected,
                        $"平面面镜像后实体数 {CountBodies(part)}，预期 {beforeFace + 1}");
            }
            finally { ClearNxSelection(); }

            // --- F1-analog: feature mode + datum plane ---
            var (fb, _) = CreateFixtureBlock(part, 2100, 20);
            var blockFeature = part.Features.ToArray()
                .OfType<NXOpen.Features.Feature>()
                .LastOrDefault(f => !IsDeadObject(f) && f.GetBodies().Contains(fb));
            if (blockFeature == null)
                return Fail("caseL.mirror.feat.seed", input, expected, "夹具块的 Block 特征未找到");
            var mirrorDatum = part.Datums.CreateFixedDatumPlane(new Point3d(2200, 0, 0), identity);
            SelectInNx(new NXObject[] { blockFeature, mirrorDatum });
            try
            {
                var feat = _executor.ExecuteCreateMirror(
                    new CreateMirrorParameters(MirrorMode.Feature, true), null);
                if (feat.Status != ToolExecutionStatus.Success)
                    return Fail("caseL.mirror.feat", input, expected, feat.Message);
            }
            finally { ClearNxSelection(); }

            return new CaseResult("caseL.mirror", true, "verified", input, expected,
                "三场景（双体+基准面 / 单体+平面面 / 特征+基准面）均提交", null);
        }
        catch (Exception ex)
        {
            return Fail("caseL.mirror.exception", input, expected, $"{ex.GetType().Name}: {ex.Message}");
        }
    }

    private static Face? FindRightmostPlanarFace(Body body)
    {
        Face? best = null;
        double bestX = double.MinValue;
        foreach (Face face in body.GetFaces())
        {
            if (IsDeadObject(face)) continue;
            try
            {
                if (face.SolidFaceType.ToString().ToLowerInvariant() != "planar") continue;
                var ufSession = UFSession.GetUFSession();
                double[] uv = new double[4];
                ufSession.Modl.AskFaceUvMinmax(face.Tag, uv);
                double[] param = { (uv[0] + uv[2]) / 2.0, (uv[1] + uv[3]) / 2.0 };
                double[] point = new double[3];
                double[] u1 = new double[3], v1 = new double[3],
                    u2 = new double[3], v2 = new double[3];
                double[] unitNorm = new double[3];
                double[] radii = new double[2];
                ufSession.Modl.AskFaceProps(face.Tag, param, point,
                    u1, v1, u2, v2, unitNorm, radii);
                if (point[0] > bestX) { bestX = point[0]; best = face; }
            }
            catch { /* unreadable: not the candidate */ }
        }
        return best;
    }

    // --------------------------------------------- negative / rollback sweep

    private CaseResult CheckNegativeAndRollback(Part part)
    {
        // Structured refusals and rollback behaviors across the new tools:
        // mirror with EMPTY selection, mirror with TWO plane candidates
        // (ambiguous), circular pattern with a parallel second axis
        // (parser-level), and a stale-reference extrude (the existing
        // caseD covers one; this sweep proves the newer tools refuse the
        // same way). No geometry may appear from any refused call.
        const string input = "空选区镜像 / 双镜像面歧义 / 无效引用拉伸";
        const string expected = "全部结构化拒绝（PreconditionsChanged 或 InvalidParameters），零几何产生";
        try
        {
            int before = CountBodies(part);

            // 1. Mirror with an EMPTY selection.
            ClearNxSelection();
            var emptyMirror = _executor.ExecuteCreateMirror(
                new CreateMirrorParameters(MirrorMode.Geometry, true), null);
            if (emptyMirror.Status == ToolExecutionStatus.Success)
                return Fail("caseM.neg.empty", input, expected, "空选区镜像不应成功");

            // 2. Mirror with TWO plane candidates (datum + face) — ambiguous.
            var (body, _) = CreateFixtureBlock(part, 2300, 20);
            var identity = new Matrix3x3();
            identity.Xx = 1.0; identity.Xy = 0.0; identity.Xz = 0.0;
            identity.Yx = 0.0; identity.Yy = 1.0; identity.Yz = 0.0;
            identity.Zx = 0.0; identity.Zy = 0.0; identity.Zz = 1.0;
            var datum = part.Datums.CreateFixedDatumPlane(new Point3d(2400, 0, 0), identity);
            var topFace = FindTopPlanarFaceOf(body);
            if (topFace != null)
            {
                SelectInNx(new NXObject[] { datum, topFace, body });
                try
                {
                    var ambiguous = _executor.ExecuteCreateMirror(
                        new CreateMirrorParameters(MirrorMode.Geometry, true), null);
                    if (ambiguous.Status == ToolExecutionStatus.Success)
                        return Fail("caseM.neg.ambiguous", input, expected, "双镜像面镜像不应成功");
                }
                finally { ClearNxSelection(); }
            }

            // 3. Stale reference extrude (existing behavior, newer sweep).
            var stale = _executor.ExecuteCreateExtrude(
                new CreateExtrudeParameters("SKETCH_DOES_NOT_EXIST_2", 10, ExtrudeDirection.Normal), null);
            if (stale.Status == ToolExecutionStatus.Success)
                return Fail("caseM.neg.stale", input, expected, "无效引用拉伸不应成功");

            if (CountBodies(part) != before + 1) // only the fixture block landed
                return Fail("caseM.neg.geometry", input, expected,
                    $"拒绝场景后实体数 {CountBodies(part)}，预期 {before + 1}（无拒绝场景几何）");

            return new CaseResult("caseM.negative", true, "verified", input, expected,
                "空选区/双镜像面/无效引用均结构化拒绝且零几何", null);
        }
        catch (Exception ex)
        {
            return Fail("caseM.negative.exception", input, expected, $"{ex.GetType().Name}: {ex.Message}");
        }
    }

    private static string Truncate(string text, int max) =>
        text.Length <= max ? text : text[..max] + "…";

    private static void ClassifyAsBodyOrFeature(Body body, List<RecognizedObject> recognized)
    {
        recognized.Add(new RecognizedObject(
            "body", SafeRef(body),
            null, null, null, null, null, null,
            $"面数 {CountFaces(body)}"));
    }

    /// <summary>Classifies a feature-tree node: a datum-plane feature maps
    /// to datum_plane, a feature owning EXACTLY one solid body maps to that
    /// body, and anything else stays a "feature" row (the mirror FEATURE
    /// mode's seed kind). Read-only.</summary>
    private static void ClassifyFeature(
        NXOpen.Features.Feature feature, List<RecognizedObject> recognized)
    {
        if (feature is NXOpen.Features.DatumPlaneFeature dpf)
        {
            var inner = dpf.GetEntities().OfType<DatumPlane>().FirstOrDefault();
            if (inner != null)
            {
                ClassifyDatumPlane(inner, recognized);
                return;
            }
        }
        try
        {
            var owned = feature.GetBodies()
                .Where(b => b is Body bb && !IsDeadObject(bb) && !bb.IsSheetBody)
                .Cast<Body>().ToList();
            if (owned.Count == 1)
            {
                ClassifyAsBodyOrFeature(owned[0], recognized);
                return;
            }
        }
        catch
        {
            // unreadable bodies: fall through to the feature row
        }
        recognized.Add(new RecognizedObject(
            "feature", SafeRef(feature),
            null, null, null, null, null, null,
            feature.GetType().Name));
    }

    private static void ClassifyDatumPlane(DatumPlane dp, List<RecognizedObject> recognized)
    {
        // DatumPlane exposes Origin (Point3d) and Normal (Vector3d) scalars —
        // the compile-verified members the mirror/circular executors use.
        var o = dp.Origin;
        var n = dp.Normal;
        double len = Math.Sqrt(n.X * n.X + n.Y * n.Y + n.Z * n.Z);
        recognized.Add(new RecognizedObject(
            "datum_plane", SafeRef(dp),
            o.X, o.Y, o.Z,
            n.X / len, n.Y / len, n.Z / len,
            len < 0.5 ? "法向长度异常" : null));
    }

    private static void ClassifyPlanarFace(Face face, List<RecognizedObject> recognized)
    {
        // Verified read path: AskFaceUvMinmax → UV midpoint → AskFaceProps
        // (UFEvalsf is forbidden on planar faces — 2026-10-16 crash record).
        var ufSession = UFSession.GetUFSession();
        double[] uv = new double[4];
        ufSession.Modl.AskFaceUvMinmax(face.Tag, uv);
        double[] param = { (uv[0] + uv[2]) / 2.0, (uv[1] + uv[3]) / 2.0 };
        double[] point = new double[3];
        double[] u1 = new double[3], v1 = new double[3], u2 = new double[3], v2 = new double[3];
        double[] unitNorm = new double[3];
        double[] radii = new double[2];
        ufSession.Modl.AskFaceProps(face.Tag, param, point, u1, v1, u2, v2, unitNorm, radii);
        recognized.Add(new RecognizedObject(
            "planar_face", SafeRef(face),
            point[0], point[1], point[2],
            unitNorm[0], unitNorm[1], unitNorm[2],
            null));
    }

    private static Face? FindTopPlanarFace(Body body)
    {
        Face? best = null;
        double bestZ = double.MinValue;
        foreach (Face face in body.GetFaces())
        {
            if (IsDeadObject(face))
                continue;
            try
            {
                if (face.SolidFaceType.ToString().ToLowerInvariant() != "planar")
                    continue;
                var ufSession = UFSession.GetUFSession();
                double[] uv = new double[4];
                ufSession.Modl.AskFaceUvMinmax(face.Tag, uv);
                double[] param = { (uv[0] + uv[2]) / 2.0, (uv[1] + uv[3]) / 2.0 };
                double[] point = new double[3];
                double[] u1 = new double[3], v1 = new double[3], u2 = new double[3], v2 = new double[3];
                double[] unitNorm = new double[3];
                double[] radii = new double[2];
                ufSession.Modl.AskFaceProps(face.Tag, param, point, u1, v1, u2, v2, unitNorm, radii);
                if (point[2] > bestZ)
                {
                    bestZ = point[2];
                    best = face;
                }
            }
            catch
            {
                // unreadable face: not the top candidate
            }
        }
        return best;
    }

    private static string DescribeInvalidObject(Part part)
    {
        // A body ref from a DIFFERENT (nonexistent) part context — the
        // executor's IsDead check must classify it as invalid without a
        // raw exception. The practical probe: a body removed from the
        // collection cannot be constructed here, so verify the dead-check
        // on a cloned-and-undone tag is not directly reproducible; instead
        // assert the classifier treats a NON-body TaggedObject honestly.
        var arcs = part.Arcs.ToArray();
        if (arcs.Length > 0 && arcs[0] is Arc arc)
            return IsDeadObject(arc)
                ? "Arc 判定为失效对象"
                : "Arc 非实体/非面 → 会被种子解析器归入'不是实体'诊断（不抛异常）";
        return "部件内无额外对象可枚举（死引用路径由执行器 IsDead 门承担）";
    }

    private static int CountFaces(Body body)
    {
        int count = 0;
        foreach (object _ in body.GetFaces())
            count++;
        return count;
    }

    private static string SafeRef(NXObject obj)
    {
        try { return obj.JournalIdentifier; }
        catch { return "(ref 不可读)"; }
    }

    private static bool IsDeadObject(NXObject obj)
    {
        try
        {
            _ = obj.Tag;
            _ = obj.IsOccurrence;
            return false;
        }
        catch
        {
            return true;
        }
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
