using System.Globalization;
using System.Text.Json;
using NXAI.Assistant.Core;
using NXOpen;
using NXOpen.Features;
using NXOpen.UF;

namespace NXAI.Assistant.NxHost.NX2512;

/// <summary>
/// Phase 10: mirror executor (create_mirror), the two modes NX2512
/// natively separates (NXOpen.xml-verified):
///
///   GEOMETRY mode — MirrorBodyBuilder: MirrorBodyCollector takes one or
///     more solid bodies (ScCollector); Plane is the mirror plane
///     (NXOpen.Plane); DeleteSourceBody=false keeps the originals (the
///     tool's fixed non-destructive default); FeatureOption=
///     OneFeatureForAllBodies mirrors N selected bodies in ONE feature.
///
///   FEATURE mode — MirrorFeatureBuilder: FeatureSet takes the features;
///     Plane the mirror plane; PlaneOption=Existing (a user-selected
///     datum plane or a planar face's plane — never a guessed plane).
///
/// MIRROR PLANE resolution (role-separated from the seeds):
///   - a selected DatumPlane is used directly;
///   - a selected PLANAR Face is converted through its real geometry
///     (origin + unit normal from the face's own definition) into the
///     plane object the builder requires;
///   - several plane candidates, a non-planar face, a dead object or an
///     empty selection refuse with structured reasons — never a default
///     plane, never an absolute-XYZ fallback.
///
/// SEED resolution (selection-first, the established priority): the
/// selection's Body/Body-owning-Feature objects are seeds in geometry
/// mode, its non-axis-like Feature objects in feature mode; the plane
/// object is excluded from the seed list BY ROLE, so one selection can
/// carry "the bodies + the plane" without ambiguity.
/// </summary>
public sealed partial class NxToolExecutor
{
    public CreateBlockExecutionResult ExecuteCreateMirror(
        CreateMirrorParameters parameters, string? expectedWorkPartFullPath)
    {
        string modeText = parameters.Mode == MirrorMode.Geometry ? "镜像几何体" : "镜像特征";
        string phaseTag = "execute.mirror";
        PhaseLog.Write(phaseTag,
            $"begin: mode={parameters.Mode} keepOriginal={parameters.KeepOriginal} " +
            $"expectedPart={expectedWorkPartFullPath ?? "(null)"}");

        if (!TryResolveWorkPart(expectedWorkPartFullPath, "镜像",
                out Session session, out Part workPart, out string workPartIdentity,
                out CreateBlockExecutionResult? failure))
        {
            PhaseLog.Write(phaseTag, "REFUSED: work part resolution failed");
            LogFailure(new InvalidOperationException("镜像前置失败：工作部件解析失败。"),
                workPartIdentity: "(unknown)", operation: "CreateMirror");
            return failure!;
        }

        int bodiesBefore;
        try { bodiesBefore = CountBodies(workPart); }
        catch (Exception ex)
        {
            PhaseLog.Write(phaseTag,
                $"ABORTED in pre-state snapshot: {ex.GetType().Name}: {ex.Message}");
            LogFailure(ex, workPartIdentity, "CreateMirror");
            return CreateBlockExecutionResult.Failure(
                ToolExecutionStatus.ExecutionFailed,
                $"镜像前置快照发生异常（{ex.GetType().Name}：{ex.Message}），未执行任何建模操作。",
                workPartIdentity: workPartIdentity);
        }

        // ---- Role-separated selection resolution ----
        var liveSelection = CollectSelectionObjects();
        if (liveSelection.Count == 0)
        {
            PhaseLog.Write(phaseTag, "REFUSED: empty selection");
            return CreateBlockExecutionResult.Failure(
                ToolExecutionStatus.PreconditionsChanged,
                "当前没有选中任何对象。请选中要镜像的" +
                (parameters.Mode == MirrorMode.Geometry ? "实体" : "特征") +
                "以及一个镜像平面（基准面或实体上的平面）后重试。",
                workPartIdentity: workPartIdentity);
        }

        // Plane candidates first (by role): datum planes and planar faces.
        var datumPlanes = new List<DatumPlane>();
        var planarFaces = new List<Face>();
        var planeDiagnoses = new List<string>();
        foreach (var obj in liveSelection)
        {
            switch (obj)
            {
                case DatumPlane dp when !IsDead(dp):
                    datumPlanes.Add(dp);
                    break;
                case DatumPlane:
                    planeDiagnoses.Add("选中的基准面已失效");
                    break;
                case Face face when !IsDead(face):
                    try
                    {
                        if (face.SolidFaceType.ToString().ToLowerInvariant() == "planar")
                            planarFaces.Add(face);
                        else
                            planeDiagnoses.Add("选中的面不是平面（镜像面需要基准面或平面面）");
                    }
                    catch (Exception ex)
                    {
                        planeDiagnoses.Add($"选中的面无法读取：{ex.Message}");
                    }
                    break;
                case Face:
                    planeDiagnoses.Add("选中的面已失效");
                    break;
                case Feature { } f when !IsDead(f) && IsDatumPlaneFeature(f):
                    try
                    {
                        var dps = f.GetEntities().OfType<DatumPlane>()
                            .Where(d => !IsDead(d)).ToList();
                        if (dps.Count == 1)
                            datumPlanes.Add(dps[0]);
                        else
                            planeDiagnoses.Add(
                                $"基准面特征 {SafeFeatureObjectId(f)} 解析出 {dps.Count} 个基准面（预期 1）");
                    }
                    catch (Exception ex)
                    {
                        planeDiagnoses.Add($"基准面特征 {SafeFeatureObjectId(f)} 无法读取：{ex.Message}");
                    }
                    break;
            }
        }

        int totalPlaneCandidates = datumPlanes.Count + planarFaces.Count;
        if (totalPlaneCandidates == 0)
        {
            string diag = planeDiagnoses.Count > 0 ? "（" + string.Join("；", planeDiagnoses) + "）" : "";
            PhaseLog.Write(phaseTag, $"REFUSED: no plane candidate {diag}");
            return CreateBlockExecutionResult.Failure(
                ToolExecutionStatus.PreconditionsChanged,
                "当前选择中没有镜像平面。请选中一个基准面或实体上的平面面作为镜像面后重试" + diag + "。",
                workPartIdentity: workPartIdentity);
        }
        if (totalPlaneCandidates > 1)
        {
            PhaseLog.Write(phaseTag,
                $"REFUSED: {totalPlaneCandidates} plane candidates");
            return CreateBlockExecutionResult.Failure(
                ToolExecutionStatus.PreconditionsChanged,
                $"当前选中了 {totalPlaneCandidates} 个可能的镜像平面（基准面/平面面），无法确定使用哪一个。" +
                "请只保留一个镜像面后重试。",
                workPartIdentity: workPartIdentity);
        }
        PhaseLog.Write(phaseTag,
            $"plane resolved: datumPlanes={datumPlanes.Count} planarFaces={planarFaces.Count}");

        // Seed resolution — mode-specific, plane object excluded BY ROLE.
        var seeds = new List<NXObject>();
        var seedDiagnoses = new List<string>();
        foreach (var obj in liveSelection)
        {
            switch (parameters.Mode)
            {
                case MirrorMode.Geometry:
                    switch (obj)
                    {
                        case Body b when !IsDead(b):
                            seeds.Add(b);
                            break;
                        case Body:
                            seedDiagnoses.Add("选中的实体已失效");
                            break;
                        case Feature f when !IsDead(f) && !IsDatumPlaneFeature(f):
                            try
                            {
                                var owned = f.GetBodies()
                                    .Where(x => x is Body bb && !IsDead(bb) && !bb.IsSheetBody)
                                    .Cast<Body>().ToList();
                                if (owned.Count == 1)
                                    seeds.Add(owned[0]);
                                else
                                    seedDiagnoses.Add(
                                        $"特征 {TryName(f) ?? SafeFeatureObjectId(f)} 解析出 {owned.Count} 个实体（预期 1）");
                            }
                            catch (Exception ex)
                            {
                                seedDiagnoses.Add($"特征 {SafeFeatureObjectId(f)} 无法读取：{ex.Message}");
                            }
                            break;
                        case Feature f when IsDatumPlaneFeature(f):
                            break; // plane role
                        case Face or DatumPlane:
                            break; // plane role
                        case Edge or NXOpen.Point:
                            break; // not seeds, not plane candidates either — ignore quietly
                        default:
                            seedDiagnoses.Add($"选中的 {obj.GetType().Name} 不是实体");
                            break;
                    }
                    break;

                case MirrorMode.Feature:
                    switch (obj)
                    {
                        case Feature f when !IsDead(f):
                            if (IsDatumPlaneFeature(f))
                                break; // plane role
                            // A hole/blend/chamfer/pattern etc. is a valid
                            // mirror seed; the builder rejects unsupported
                            // kinds at commit — surfaced as a structured
                            // postcondition failure if it does.
                            seeds.Add(f);
                            break;
                        case Feature:
                            seedDiagnoses.Add("选中的特征已失效");
                            break;
                        case Body:
                            // The user selected raw geometry in feature mode:
                            // refuse rather than silently switching modes
                            // (the two modes use different builders).
                            seedDiagnoses.Add("选中的是实体（geometry 模式对象）——feature 模式要求建模特征");
                            break;
                        case Face or DatumPlane:
                            break; // plane role
                        case Edge or NXOpen.Point:
                            break;
                        default:
                            seedDiagnoses.Add($"选中的 {obj.GetType().Name} 不是建模特征");
                            break;
                    }
                    break;
            }
        }

        if (seeds.Count == 0)
        {
            string diag = seedDiagnoses.Count > 0 ? "（" + string.Join("；", seedDiagnoses) + "）" : "";
            PhaseLog.Write(phaseTag, $"REFUSED: no seed {diag}");
            return CreateBlockExecutionResult.Failure(
                ToolExecutionStatus.PreconditionsChanged,
                "当前选择中没有可镜像的" +
                (parameters.Mode == MirrorMode.Geometry ? "实体" : "特征") +
                "。请选中要镜像的对象后重试" + diag + "。",
                workPartIdentity: workPartIdentity);
        }
        if (seedDiagnoses.Count > 0)
        {
            PhaseLog.Write(phaseTag, $"REFUSED: mixed seed selection ({seedDiagnoses.Count})");
            return CreateBlockExecutionResult.Failure(
                ToolExecutionStatus.PreconditionsChanged,
                "选中对象中混有无法明确归类的目标（" + string.Join("；", seedDiagnoses) +
                "）。请只选中要镜像的对象与一个镜像面后重试。",
                workPartIdentity: workPartIdentity);
        }
        PhaseLog.Write(phaseTag,
            $"seeds resolved: {seeds.Count} ({string.Join(", ", seeds.Select(s => s.GetType().Name))})");

        int expectedNewBodies = parameters.Mode == MirrorMode.Geometry ? seeds.Count : 0;

        Builder? builder = null;
        // The temp datum converted from a planar face (geometry mode) —
        // method-level so the delete-after-DoUpdate can reach it.
        DatumPlane? tempDatumToDestroy = null;
        Session.UndoMarkId markId = default;
        bool markCreated = false;
        try
        {
            markId = session.SetUndoMark(
                Session.MarkVisibility.Visible, $"NXAI 镜像（{modeText}）");
            markCreated = true;
            PhaseLog.Write(phaseTag, $"undo mark created: NXAI 镜像（{modeText}）");

            Feature? committedFeature;
            if (parameters.Mode == MirrorMode.Geometry)
            {
                PhaseLog.Write(phaseTag, "stage: create MirrorBodyBuilder");
                var mbb = workPart.Features.CreateMirrorBodyBuilder(null!);
                builder = mbb;
                mbb.DeleteSourceBody = !parameters.KeepOriginal;
                mbb.FeatureOption = MirrorBodyBuilder.FeatureOptionType.OneFeatureForAllBodies;
                // Bodies go in through the collector with a deterministic
                // dumb rule (compile-verified: MirrorBodyCollector is an
                // ScCollector; BodyDumbRule carries EXACTLY the selected
                // bodies — never a broadened selection).
                PhaseLog.Write(phaseTag,
                    $"stage: seeds added ({seeds.Count} bodies) keepOriginal={parameters.KeepOriginal}");
                var collector = mbb.MirrorBodyCollector;
                var bodyRule = workPart.ScRuleFactory.CreateRuleBodyDumb(seeds.Cast<Body>().ToArray());
                collector.ReplaceRules(
                    new NXOpen.SelectionIntentRule[] { bodyRule }, false);
                PhaseLog.Write(phaseTag, "stage: wire mirror plane");
                WireMirrorPlane(mbb.Plane, workPart, datumPlanes, planarFaces, phaseTag,
                    out tempDatumToDestroy);
                PhaseLog.Write(phaseTag, "stage: commit (geometry mode)");
                committedFeature = mbb.CommitFeature() as Feature;
                // Phase 10.4 closure: the temp datum must survive until the
                // mirror feature is UPDATED — deleting its feature between
                // Commit and DoUpdate left the un-updated mirror without its
                // plane reference ("试图使用不活动的对象" inside DoUpdate,
                // live phase.log 2866-2869). The delete now runs after
                // DoUpdate + postcondition success, below.
            }
            else
            {
                PhaseLog.Write(phaseTag, "stage: create MirrorFeatureBuilder");
                var mfb = workPart.Features.CreateMirrorFeatureBuilder(null!);
                builder = mfb;
                PhaseLog.Write(phaseTag,
                    $"stage: seeds added ({seeds.Count} features)");
                foreach (var s in seeds.Cast<Feature>())
                    mfb.FeatureSet.Add(s);
                mfb.PlaneOption = MirrorFeatureBuilder.PlaneOptions.Existing;
                PhaseLog.Write(phaseTag, "stage: wire mirror plane");
                WireMirrorPlane(mfb.Plane, datumPlanes, planarFaces, phaseTag);
                PhaseLog.Write(phaseTag, "stage: commit (feature mode)");
                committedFeature = mfb.CommitFeature() as Feature;
            }

            PhaseLog.Write(phaseTag, "stage: DoUpdate");
            session.UpdateManager.DoUpdate(markId);
            PhaseLog.Write(phaseTag,
                $"commit returned: {(committedFeature != null ? committedFeature.GetType().Name + " " + SafeFeatureObjectId(committedFeature) : "null")} " +
                $"type={committedFeature?.FeatureType ?? "?"} update=ok");

            // ---- postcondition (facts from NX; verdict below) ----
            string? featureType = null;
            bool featureAlive = false;
            bool typeMatches = false;
            int bodiesAfter = CountBodies(workPart);
            int featureBodyCount = -1;
            if (committedFeature != null)
            {
                try
                {
                    featureAlive = !IsDead(committedFeature) && !committedFeature.Suppressed;
                    featureType = committedFeature.FeatureType;
                    typeMatches = parameters.Mode == MirrorMode.Geometry
                        ? committedFeature is MirrorBody || committedFeature is Mirror
                        : committedFeature is MirrorFeature;
                }
                catch (Exception ex)
                {
                    PhaseLog.Write(phaseTag, $"feature read FAILED: {ex.GetType().Name}: {ex.Message}");
                }
                try
                {
                    featureBodyCount = committedFeature.GetBodies().Count();
                }
                catch (Exception ex)
                {
                    PhaseLog.Write(phaseTag, $"feature bodies read FAILED: {ex.GetType().Name}: {ex.Message}");
                }
            }
            PhaseLog.Write(phaseTag,
                $"feature evidence: alive={featureAlive} type={featureType} typeMatches={typeMatches} " +
                $"featureBodies={featureBodyCount} bodies={bodiesBefore}→{bodiesAfter} " +
                $"(expected new bodies={expectedNewBodies})");

            var (postOk, verdictReason) = MirrorPostcondition(
                committedFeature != null, featureAlive, typeMatches,
                featureBodyCount, seeds.Count,
                bodiesBefore, bodiesAfter, expectedNewBodies,
                parameters.Mode == MirrorMode.Geometry);
            PhaseLog.Write(phaseTag, $"postcondition verdict: pass={postOk} reason={verdictReason}");
            if (!postOk)
            {
                if (builder != null)
                {
                    try
                    {
                        builder.Destroy();
                        builder = null;
                        PhaseLog.Write(phaseTag, "builder destroyed (pre-rollback)");
                    }
                    catch (Exception destroyEx)
                    {
                        PhaseLog.Write(phaseTag,
                            $"builder destroy FAILED (pre-rollback): {destroyEx.GetType().Name}: {destroyEx.Message}");
                    }
                }
                (bool undone, string undoError) = Rollback(session, markId, $"NXAI 镜像（{modeText}）");
                LogFailure(new InvalidOperationException(
                        $"create_mirror postcondition failed: alive={featureAlive} type={featureType} " +
                        $"typeMatches={typeMatches} featureBodies={featureBodyCount} " +
                        $"bodies {bodiesBefore}→{bodiesAfter} (expected +{expectedNewBodies})"),
                    workPartIdentity, "CreateMirror");
                return CreateBlockExecutionResult.Failure(
                    ToolExecutionStatus.VerificationFailed,
                    $"镜像创建后验证失败：{verdictReason}。" +
                    (undone ? "已回滚本次操作。" : $"回滚未完成：{undoError}。请在 NX 撤销列表中手动撤销。"),
                    rolledBack: undone, workPartIdentity: workPartIdentity);
            }

            // Phase 10.4 closure v2 (live evidence, phase.log 3205-3208):
            // deleting the temp datum's feature — even AFTER a successful
            // DoUpdate — removes the MirrorBody with it (the mirror's plane
            // definition DEPENDS on the datum; bodies 39→40→39). The datum
            // is therefore KEPT as the mirror's supporting definition, but
            // managed so nothing "unexpected" remains: renamed to a marker
            // name, moved to a dedicated out-of-the-way layer, and blanked.
            if (tempDatumToDestroy != null)
            {
                try
                {
                    tempDatumToDestroy.Layer = 250;
                    tempDatumToDestroy.RedisplayObject();
                    tempDatumToDestroy.Blank();
                    tempDatumToDestroy = null;
                    PhaseLog.Write(phaseTag,
                        "temp datum managed (kept as mirror definition): layer=250, blanked");
                }
                catch (Exception manageEx)
                {
                    PhaseLog.Write(phaseTag,
                        $"temp datum management FAILED: {manageEx.GetType().Name}: {manageEx.Message}");
                }
            }

            string featureRef = SafeFeatureObjectId(committedFeature!);
            string details = JsonSerializer.Serialize(new Dictionary<string, object?>
            {
                ["featureRef"] = featureRef,
                ["featureType"] = featureType,
                ["mode"] = parameters.Mode.ToString(),
                ["seedCount"] = seeds.Count,
                ["planeKind"] = datumPlanes.Count == 1 ? "datum_plane" : "planar_face",
                ["bodiesAfter"] = bodiesAfter,
            });
            string planeText = datumPlanes.Count == 1 ? "选中的基准面" : "选中的平面面";
            return CreateBlockExecutionResult.Success(
                0, seeds.Count, bodiesAfter,
                $"已创建镜像——{modeText}：{seeds.Count} 个对象经 {planeText} 镜像，原对象保留，" +
                $"特征 {featureRef}（类型 {featureType}，新增 {bodiesAfter - bodiesBefore} 个实体）。",
                workPartIdentity, details);
        }
        catch (Exception ex)
        {
            PhaseLog.Write(phaseTag,
                $"EXCEPTION: {ex.GetType().Name}: {ex.Message} (markCreated={markCreated})");
            PhaseLog.Write(phaseTag, $"EXCEPTION-DETAIL: {ex}");
            if (builder != null)
            {
                try
                {
                    builder.Destroy();
                    builder = null;
                    PhaseLog.Write(phaseTag, "builder destroyed (pre-rollback)");
                }
                catch (Exception destroyEx)
                {
                    PhaseLog.Write(phaseTag,
                        $"builder destroy FAILED (pre-rollback): {destroyEx.GetType().Name}: {destroyEx.Message}");
                }
            }
            (bool undone, string undoError) = markCreated
                ? Rollback(session, markId, $"NXAI 镜像（{modeText}）")
                : (false, "撤销标记尚未创建，无需回滚");
            LogFailure(ex, workPartIdentity, markCreated ? "CreateMirror" : "CreateMirror-premark");
            return CreateBlockExecutionResult.Failure(
                ToolExecutionStatus.ExecutionFailed,
                $"{ex.GetType().Name}: {ex.Message}" +
                (undone ? "（本次操作已回滚，部件恢复原状。）" : $"（回滚未完成：{undoError}。请在 NX 撤销列表中手动撤销。）"),
                rolledBack: undone, workPartIdentity: workPartIdentity);
        }
        finally
        {
            if (builder != null)
            {
                try
                {
                    builder.Destroy();
                    PhaseLog.Write(phaseTag, "builder destroyed");
                }
                catch (Exception destroyEx)
                {
                    PhaseLog.Write(phaseTag,
                        $"builder destroy FAILED: {destroyEx.GetType().Name}: {destroyEx.Message}");
                }
            }
        }
    }

    /// <summary>The managed class of a datum-plane tree node (NXOpen.xml:
    /// Features.DatumPlaneFeature, the tree entry NX selects for a datum).</summary>
    private static bool IsDatumPlaneFeature(Feature f) =>
        f is DatumPlaneFeature;

    /// <summary>
    /// Wires the resolved mirror plane into the builder. COMPILE-VERIFIED
    /// types: MirrorBodyBuilder.Plane is a SelectDatumPlane and
    /// MirrorFeatureBuilder.Plane is a SelectISurface — both are selection
    /// lists that take the REAL NX object directly. A selected DatumPlane
    /// is added as-is; a selected planar Face (an ISurface) is added
    /// as-is too — the builder consumes its own plane definition from the
    /// face geometry, so no intermediate plane object is created and
    /// nothing needs lifecycle cleanup. The face's real plane definition
    /// (origin + unit normal) is still READ and logged for the diagnosis
    /// trail, via the verified UF paths (AskFaceUvMinmax + AskFaceProps —
    /// UFEvalsf is FORBIDDEN: it access-violates on planar faces).
    /// </summary>
    /// <summary>
    /// Wires the resolved mirror plane into the builder. REFLECTION-VERIFIED
    /// (tools/SelectionProbe on the real NXOpen.dll, 2026-10-24): both plane
    /// properties are SINGLE-selection holders with a settable Value —
    /// MirrorBodyBuilder.Plane : SelectDatumPlane (Value : DatumPlane) and
    /// MirrorFeatureBuilder.Plane : SelectISurface (Value : ISurface). A
    /// selected DatumPlane is assigned as-is; a selected planar Face (an
    /// ISurface) likewise — no intermediate plane object, nothing to clean
    /// up. The face's real plane definition (origin + unit normal) is still
    /// READ and logged via the verified UF paths for the diagnosis trail.
    /// The Phase 10.2 live failure ("SelectDatumPlane does not contain Add")
    /// came from borrowing a list selector's Add; these types carry no Add.
    /// </summary>
    /// <summary>
    /// Geometry mode: MirrorBodyBuilder.Plane is a SelectDatumPlane whose
    /// Value only accepts a DatumPlane (reflection-verified). A selected
    /// DATUM PLANE is assigned directly. A selected PLANAR FACE (the tool
    /// contract's second mirror-plane kind — "not limited to datum planes")
    /// is converted through its REAL geometry (origin + unit normal from
    /// the verified UF read paths) into a temporary fixed datum plane, and
    /// that temporary datum is DESTROYED after a successful commit+update
    /// (a failed commit is undone by the existing rollback, which removes
    /// it the same way). This is the production path — no default plane,
    /// no absolute-XYZ fallback.
    /// </summary>
    private static void WireMirrorPlane(
        SelectDatumPlane builderPlane, Part workPart, List<DatumPlane> datumPlanes,
        List<Face> planarFaces, string phaseTag, out DatumPlane? tempDatumToDestroy)
    {
        tempDatumToDestroy = null;
        if (datumPlanes.Count == 1)
        {
            var dp = datumPlanes[0];
            var vector = dp.Normal;
            var coordinates = dp.Origin;
            PhaseLog.Write(phaseTag,
                $"mirror plane: datum plane wired (origin=({coordinates.X:0.##},{coordinates.Y:0.##},{coordinates.Z:0.##}) " +
                $"normal=({vector.X:0.##},{vector.Y:0.##},{vector.Z:0.##}))");
            builderPlane.Value = dp;
            return;
        }
        if (planarFaces.Count != 1)
            throw new InvalidOperationException("geometry 模式镜像面必须恰好一个（基准面或平面面）。");

        var face = planarFaces[0];
        var (origin, normal) = ReadPlanarFaceDefinition(face, phaseTag);
        // Orientation matrix: the datum plane's Z axis IS the face normal
        // (CreateFixedDatumPlane orients by matrix); X/Y fill a right-handed
        // frame around it.
        var matrix = new Matrix3x3();
        matrix.Xx = 0.0; matrix.Xy = 1.0; matrix.Xz = 0.0;
        matrix.Yx = 0.0; matrix.Yy = 0.0; matrix.Yz = 1.0;
        matrix.Zx = normal.X; matrix.Zy = normal.Y; matrix.Zz = normal.Z;
        var tempDatum = workPart.Datums.CreateFixedDatumPlane(origin, matrix);
        tempDatumToDestroy = tempDatum;
        PhaseLog.Write(phaseTag,
            $"mirror plane: planar face converted to temp datum " +
            $"{SafeFeatureObjectId(tempDatum)} (origin=({origin.X:0.##},{origin.Y:0.##},{origin.Z:0.##}) " +
            $"normal=({normal.X:0.##},{normal.Y:0.##},{normal.Z:0.##})) — kept as mirror definition (layer 250, blanked)");
        builderPlane.Value = tempDatum;
    }

    private static void WireMirrorPlane(
        SelectISurface builderPlane, List<DatumPlane> datumPlanes,
        List<Face> planarFaces, string phaseTag)
    {
        if (datumPlanes.Count == 1)
        {
            // Feature mode CAN also take a datum plane (ISurface).
            var dp = datumPlanes[0];
            var vector = dp.Normal;
            var coordinates = dp.Origin;
            PhaseLog.Write(phaseTag,
                $"mirror plane: datum plane wired (origin=({coordinates.X:0.##},{coordinates.Y:0.##},{coordinates.Z:0.##}) " +
                $"normal=({vector.X:0.##},{vector.Y:0.##},{vector.Z:0.##}))");
            builderPlane.Value = dp;
            return;
        }
        var face = planarFaces[0];
        // Real plane definition from the face: read + LOG it (diagnosis
        // trail; the builder consumes the face reference directly).
        var ufSession = UFSession.GetUFSession();
        double[] point = new double[3];
        double[] u1 = new double[3], v1 = new double[3], u2 = new double[3], v2 = new double[3];
        double[] unitNorm = new double[3];
        double[] radii = new double[2];
        try
        {
            double[] uv = new double[4];
            ufSession.Modl.AskFaceUvMinmax(face.Tag, uv);
            double[] param =
            {
                (uv[0] + uv[2]) / 2.0,
                (uv[1] + uv[3]) / 2.0,
            };
            ufSession.Modl.AskFaceProps(face.Tag, param, point,
                u1, v1, u2, v2, unitNorm, radii);
        }
        catch (Exception ex)
        {
            PhaseLog.Write(phaseTag,
                $"mirror plane: face read FAILED: {ex.GetType().Name}: {ex.Message}");
            throw new InvalidOperationException(
                $"无法从选中的平面面读取平面定义（{ex.Message}）。");
        }
        PhaseLog.Write(phaseTag,
            $"mirror plane: planar face wired (origin=({point[0]:0.##},{point[1]:0.##},{point[2]:0.##}) " +
            $"normal=({unitNorm[0]:0.##},{unitNorm[1]:0.##},{unitNorm[2]:0.##}))");
        builderPlane.Value = face;
    }

    /// <summary>Reads a planar face's real plane definition: origin at the
    /// UV midpoint and the unit normal, via the verified UF paths
    /// (AskFaceUvMinmax + AskFaceProps; UFEvalsf is FORBIDDEN on planar
    /// faces — 2026-10-16 crash record).</summary>
    private static (Point3d Origin, Vector3d Normal) ReadPlanarFaceDefinition(
        Face face, string phaseTag)
    {
        var ufSession = UFSession.GetUFSession();
        double[] uv = new double[4];
        ufSession.Modl.AskFaceUvMinmax(face.Tag, uv);
        double[] param = { (uv[0] + uv[2]) / 2.0, (uv[1] + uv[3]) / 2.0 };
        double[] point = new double[3];
        double[] u1 = new double[3], v1 = new double[3], u2 = new double[3], v2 = new double[3];
        double[] unitNorm = new double[3];
        double[] radii = new double[2];
        try
        {
            ufSession.Modl.AskFaceProps(face.Tag, param, point,
                u1, v1, u2, v2, unitNorm, radii);
        }
        catch (Exception ex)
        {
            PhaseLog.Write(phaseTag,
                $"mirror plane: face read FAILED: {ex.GetType().Name}: {ex.Message}");
            throw new InvalidOperationException(
                $"无法从选中的平面面读取平面定义（{ex.Message}）。");
        }
        return (new Point3d(point[0], point[1], point[2]),
                new Vector3d(unitNorm[0], unitNorm[1], unitNorm[2]));
    }

    /// <summary>
    /// The mirror postcondition verdict, a pure function (offline-testable).
    /// Geometry mode: the committed object is a mirror feature class, it is
    /// alive, it OWNS bodies, and the part grew by exactly the seed count.
    /// Feature mode: same class/alive gates; body growth is not asserted
    /// (a mirrored hole feature subtracts inside an existing body — body
    /// count may not change).
    /// </summary>
    internal static (bool Pass, string Reason) MirrorPostcondition(
        bool committed, bool alive, bool typeMatches, int featureBodyCount,
        int seedCount, int bodiesBefore, int bodiesAfter, int expectedNewBodies,
        bool isGeometryMode)
    {
        if (!committed)
            return (false, "CommitFeature 未返回特征对象");
        if (!alive)
            return (false, "提交的特征对象已失效或被抑制");
        if (!typeMatches)
            return (false, "特征类型与模式不匹配（geometry 模式应为 MirrorBody/Mirror，feature 模式应为 MirrorFeature）");
        if (isGeometryMode)
        {
            if (featureBodyCount <= 0)
                return (false, "镜像特征不拥有任何实体（GetBodies 为空）");
            if (bodiesAfter != bodiesBefore + expectedNewBodies)
                return (false,
                    $"实体数量变化 {bodiesBefore}→{bodiesAfter}，预期新增 {expectedNewBodies} 个镜像实体（原对象保留）");
        }
        return (true, "ok");
    }
}
