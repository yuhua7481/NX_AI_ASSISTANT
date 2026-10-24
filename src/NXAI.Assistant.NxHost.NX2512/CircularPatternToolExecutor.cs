using System.Globalization;
using System.Text.Json;
using NXAI.Assistant.Core;
using NXOpen;
using NXOpen.Features;
using NXOpen.GeometricUtilities;
using NXOpen.UF;

namespace NXAI.Assistant.NxHost.NX2512;

/// <summary>
/// Phase 9.9: circular-pattern executor (create_circular_pattern), same
/// TWO modes as the linear tool (verified against NX2512's NXOpen.xml):
///
///   FEATURE mode — PatternFeatureBuilder, PatternService.PatternType=
///     Circular → CircularDefinition (CircularPattern):
///     AngularSpacing with UsePitchOption per the requested extent;
///     RotationAxis set from the resolved axis.
///   GEOMETRY mode — PatternGeometryBuilder with the same circular
///     PatternService definition; seed = a whole solid Body.
///
/// Axis resolution (host-side, never guessed):
///   - plus_x/plus_y/plus_z: an absolute axis through the origin —
///     a Direction smart object built like the linear tool's XDirection.
///   - datum_axis: the current selection must contain EXACTLY ONE live
///     DatumAxis (or a DatumAxisFeature resolving to one).
///   - line_edge: the current selection must contain EXACTLY ONE live
///     straight Edge; the axis line runs through its two endpoints.
/// The seed resolution reuses the linear tool's selection-first priority
/// (one selected body wins over other bodies in the part; one selected
/// feature wins in feature mode; empty selection falls to the remembered
/// ref / unique-candidate paths).
///
/// Angular semantics (the contract):
///   - full circle (TotalAngleDeg null): count instances evenly over 360°
///     (pitch = 360/count; no duplicated start/end instance);
///   - partial arc (TotalAngleDeg set): the FIRST instance sits on the
///     start and the LAST lands ON the end angle — NX's SpanAngle path.
/// Postcondition: the committed object is the exact managed class for the
/// mode, it is alive, and instance evidence is readable (feature mode) /
/// the body count grew by exactly (count − seed) (geometry mode) — the
/// same verdict shape as LinearPatternPostcondition.
/// </summary>
public sealed partial class NxToolExecutor
{
    public CreateBlockExecutionResult ExecuteCreateCircularPattern(
        CreateCircularPatternParameters parameters, string? expectedWorkPartFullPath)
    {
        string modeText = parameters.Mode == CircularPatternMode.Feature ? "环形阵列特征" : "环形阵列几何体";
        string phaseTag = "execute.cir_pattern";
        string angleText = parameters.TotalAngleDeg is { } ta ? $"{ta:0.###}°" : "360°（完整圆周）";
        PhaseLog.Write(phaseTag,
            $"begin: mode={parameters.Mode} axis={parameters.Axis} count={parameters.Count} " +
            $"totalAngle={(parameters.TotalAngleDeg is { } t ? t.ToString("0.###") : "(full)")} " +
            $"expectedPart={expectedWorkPartFullPath ?? "(null)"}");

        if (!TryResolveWorkPart(expectedWorkPartFullPath, "环形阵列",
                out Session session, out Part workPart, out string workPartIdentity,
                out CreateBlockExecutionResult? failure))
        {
            PhaseLog.Write(phaseTag, "REFUSED: work part resolution failed");
            LogFailure(new InvalidOperationException("环形阵列前置失败：工作部件解析失败。"),
                workPartIdentity: "(unknown)", operation: "CreateCircularPattern");
            return failure!;
        }

        // ---- Target resolution (seed): the linear tool's unified priority,
        // with the axis object resolved from the SAME selection when the
        // axis kind needs one (datum_axis / line_edge). The seed and the
        // axis are different object kinds, so one selection can carry both
        // (user selects the body + the datum axis) — each resolver picks
        // only its own kind and refuses on ambiguity.
        int bodiesBefore;
        try { bodiesBefore = CountBodies(workPart); }
        catch (Exception ex)
        {
            PhaseLog.Write(phaseTag,
                $"ABORTED in pre-state snapshot: {ex.GetType().Name}: {ex.Message}");
            LogFailure(ex, workPartIdentity, "CreateCircularPattern");
            return CreateBlockExecutionResult.Failure(
                ToolExecutionStatus.ExecutionFailed,
                $"环形阵列前置快照发生异常（{ex.GetType().Name}：{ex.Message}），未执行任何建模操作。",
                workPartIdentity: workPartIdentity);
        }

        NXObject? seedObject;
        string targetDescription;
        if (parameters.Mode == CircularPatternMode.Feature)
        {
            var featureResult = ResolveFeatureSeed(workPart, phaseTag, workPartIdentity);
            if (featureResult.Error != null)
                return CreateBlockExecutionResult.Failure(
                    ToolExecutionStatus.PreconditionsChanged, featureResult.Error,
                    workPartIdentity: workPartIdentity);
            seedObject = featureResult.Seed;
            targetDescription = featureResult.Description;
        }
        else
        {
            var bodyResult = ResolveGeometrySeed(workPart, phaseTag, workPartIdentity);
            if (bodyResult.Error != null)
                return CreateBlockExecutionResult.Failure(
                    ToolExecutionStatus.PreconditionsChanged, bodyResult.Error,
                    workPartIdentity: workPartIdentity);
            seedObject = bodyResult.Seed;
            targetDescription = bodyResult.Description;
        }
        PhaseLog.Write(phaseTag,
            $"seed resolved: {targetDescription} ({seedObject!.GetType().Name})");

        // ---- Axis resolution (host-side; the contract refuses to guess) ----
        var axisResolution = ResolveRotationAxis(workPart, parameters.Axis, phaseTag);
        if (axisResolution.Error != null)
        {
            return CreateBlockExecutionResult.Failure(
                ToolExecutionStatus.PreconditionsChanged, axisResolution.Error,
                workPartIdentity: workPartIdentity);
        }

        int expectedNewBodies = parameters.Mode == CircularPatternMode.Geometry
            ? parameters.Count - 1 // the seed stays (HideOriginal=false)
            : 0;

        Builder? builder = null;
        Session.UndoMarkId markId = default;
        bool markCreated = false;
        try
        {
            markId = session.SetUndoMark(
                Session.MarkVisibility.Visible, $"NXAI 环形阵列（{modeText}）");
            markCreated = true;
            PhaseLog.Write(phaseTag, $"undo mark created: NXAI 环形阵列（{modeText}）");

            PatternDefinition? pattern;
            Feature? committedFeature;
            if (parameters.Mode == CircularPatternMode.Feature)
            {
                PhaseLog.Write(phaseTag, "stage: create PatternFeatureBuilder");
                var pfb = workPart.Features.CreatePatternFeatureBuilder(null!);
                builder = pfb;
                pfb.PatternMethod = PatternFeatureBuilder.PatternMethodOptions.Simple;
                pfb.OutputOption = PatternFeatureBuilder.OutputOptions.PatternFeature;
                pfb.ReferencePointService.IsReferencePointInferred = true;
                PhaseLog.Write(phaseTag,
                    $"stage: seed feature added ({seedObject!.GetType().Name} {SafeFeatureObjectId(seedObject)})");
                pfb.FeatureList.Add((Feature)seedObject!);
                pattern = pfb.PatternService;
                PhaseLog.Write(phaseTag, "stage: apply circular definition");
                ApplyCircularDefinition(pattern, workPart, parameters,
                    axisResolution.Direction!, axisResolution.Point!, phaseTag);
                PhaseLog.Write(phaseTag, "stage: commit (feature mode)");
                committedFeature = pfb.CommitFeature() as Feature;
            }
            else
            {
                PhaseLog.Write(phaseTag, "stage: create PatternGeometryBuilder");
                var pgb = workPart.Features.CreatePatternGeometryBuilder(null!);
                builder = pgb;
                pgb.IsAssociative = true;
                pgb.HideOriginal = false; // the seed stays
                pgb.CopyThreads = true;
                PhaseLog.Write(phaseTag,
                    $"stage: seed body added ({seedObject!.GetType().Name} {SafeFeatureObjectId(seedObject)})");
                pgb.GeometryToPattern.Add((Body)seedObject!);
                pattern = pgb.PatternService;
                PhaseLog.Write(phaseTag, "stage: apply circular definition");
                ApplyCircularDefinition(pattern, workPart, parameters,
                    axisResolution.Direction!, axisResolution.Point!, phaseTag);
                PhaseLog.Write(phaseTag, "stage: commit (geometry mode)");
                committedFeature = pgb.CommitFeature() as Feature;
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
            int instanceCount = -1;
            int bodiesAfter = CountBodies(workPart);
            if (committedFeature != null)
            {
                try
                {
                    featureAlive = !IsDead(committedFeature) && !committedFeature.Suppressed;
                    featureType = committedFeature.FeatureType;
                    typeMatches = parameters.Mode == CircularPatternMode.Feature
                        ? committedFeature is NXOpen.Features.PatternFeature
                        : committedFeature is NXOpen.Features.PatternGeometry;
                }
                catch (Exception ex)
                {
                    PhaseLog.Write(phaseTag, $"feature read FAILED: {ex.GetType().Name}: {ex.Message}");
                }
                try
                {
                    instanceCount = committedFeature is PatternFeature pf
                        ? pf.GetAllContainedFeatures().Length
                        : committedFeature is PatternGeometry pg
                            ? pg.GetAllContainedFeatures().Length
                            : -1;
                }
                catch (Exception ex)
                {
                    PhaseLog.Write(phaseTag, $"instance read FAILED: {ex.GetType().Name}: {ex.Message}");
                }
            }
            PhaseLog.Write(phaseTag,
                $"feature evidence: alive={featureAlive} type={featureType} typeMatches={typeMatches} " +
                $"instances={instanceCount} bodies={bodiesBefore}→{bodiesAfter} " +
                $"(expected new bodies={expectedNewBodies})");

            var (postOk, verdictReason) = CircularPatternPostcondition(
                committedFeature != null, featureAlive, typeMatches,
                instanceCount, parameters.Count,
                bodiesBefore, bodiesAfter, expectedNewBodies,
                parameters.Mode == CircularPatternMode.Geometry);
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
                (bool undone, string undoError) = Rollback(session, markId, $"NXAI 环形阵列（{modeText}）");
                LogFailure(new InvalidOperationException(
                        $"create_circular_pattern postcondition failed: alive={featureAlive} " +
                        $"type={featureType} typeMatches={typeMatches} instances={instanceCount} " +
                        $"bodies {bodiesBefore}→{bodiesAfter} (expected +{expectedNewBodies})"),
                    workPartIdentity, "CreateCircularPattern");
                return CreateBlockExecutionResult.Failure(
                    ToolExecutionStatus.VerificationFailed,
                    $"环形阵列创建后验证失败：{verdictReason}。" +
                    (undone ? "已回滚本次操作。" : $"回滚未完成：{undoError}。请在 NX 撤销列表中手动撤销。"),
                    rolledBack: undone, workPartIdentity: workPartIdentity);
            }

            string featureRef = SafeFeatureObjectId(committedFeature!);
            string axisText = parameters.Axis switch
            {
                CircularPatternAxis.PlusX => "+X 轴",
                CircularPatternAxis.PlusY => "+Y 轴",
                CircularPatternAxis.PlusZ => "+Z 轴",
                CircularPatternAxis.DatumAxis => "选中的基准轴",
                _ => "选中的直线边",
            };
            string details = JsonSerializer.Serialize(new Dictionary<string, object?>
            {
                ["featureRef"] = featureRef,
                ["featureType"] = featureType,
                ["mode"] = parameters.Mode.ToString(),
                ["instanceCount"] = instanceCount,
                ["axis"] = parameters.Axis.ToString(),
                ["count"] = parameters.Count,
                ["totalAngleDeg"] = parameters.TotalAngleDeg,
            });
            return CreateBlockExecutionResult.Success(
                0, parameters.Count, bodiesAfter,
                $"已创建环形阵列——{modeText}：种子 {targetDescription}，旋转轴 {axisText}，" +
                $"共 {parameters.Count} 个实例（{angleText}" +
                (parameters.TotalAngleDeg is { } deg
                    ? $"，相邻实例角距约 {deg / (parameters.Count - 1):0.###}°"
                    : $"，相邻实例角距 {360.0 / parameters.Count:0.###}°") +
                $"），特征 {featureRef}（类型 {featureType}，实例证据 {instanceCount}）。",
                workPartIdentity, details);
        }
        catch (Exception ex)
        {
            // 2026-10-23 T1 diagnosis: Message alone hid the failing frame;
            // the full ToString (stack + inner chain) goes to the phase log
            // only — the chat-facing failure text stays a one-line summary.
            PhaseLog.Write(phaseTag,
                $"EXCEPTION: {ex.GetType().Name}: {ex.Message} (markCreated={markCreated})");
            PhaseLog.Write(phaseTag, $"EXCEPTION-DETAIL: {ex}");
            if (ex.InnerException != null)
                PhaseLog.Write(phaseTag,
                    $"EXCEPTION-INNER: {ex.InnerException.GetType().Name}: {ex.InnerException.Message}\n{ex.InnerException}");
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
                ? Rollback(session, markId, $"NXAI 环形阵列（{modeText}）")
                : (false, "撤销标记尚未创建，无需回滚");
            LogFailure(ex, workPartIdentity, markCreated ? "CreateCircularPattern" : "CreateCircularPattern-premark");
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

    /// <summary>
    /// Applies the circular definition to a freshly obtained
    /// PatternDefinition: PatternType=Circular, RotationAxis assigned a
    /// NEWLY CREATED NXOpen.Axis smart object — the 2026-10-23 T1 live fix:
    /// a fresh PatternDefinition's RotationAxis getter returns NULL until
    /// one is assigned (the T1 NRE: `axis.Point = …` dereferenced that
    /// null). AxisCollection.CreateAxis(Point, Direction, UpdateOption)
    /// (NXOpen.xml-verified overload) builds the PointAndDirection axis;
    /// the whole object is then assigned to RotationAxis. AngularSpacing
    /// with NCopies=count and the extent rule (full circle → PitchAngle=
    /// 360/count with UsePitchOption=Angle; partial arc → SpanAngle with
    /// the last instance ON the end angle). Read-back is logged.
    /// </summary>
    private static void ApplyCircularDefinition(
        PatternDefinition pattern, Part workPart,
        CreateCircularPatternParameters parameters,
        Direction axisDirection, NXOpen.Point axisPoint, string phaseTag)
    {
        pattern.PatternType = PatternDefinition.PatternEnum.Circular;
        var circular = pattern.CircularDefinition;
        PhaseLog.Write(phaseTag,
            $"circular definition precheck: rotationAxis={(circular.RotationAxis != null ? "present" : "NULL")}");

        // Create the rotation axis smart object from the resolved
        // (point, direction) pair and assign it whole. Creating it OURSELVES
        // also makes the axis lifetime independent of any datum the user
        // selected — the pattern references its own smart object.
        var axis = workPart.Axes.CreateAxis(
            axisPoint, axisDirection, SmartObject.UpdateOption.WithinModeling);
        PhaseLog.Write(phaseTag,
            $"circular definition: axis created type={axis.Type} point/direction wired");
        circular.RotationAxis = axis;

        var spacing = circular.AngularSpacing;
        spacing.NCopies.SetFormula(parameters.Count.ToString(CultureInfo.InvariantCulture));
        // Angle semantics (T2 2026-10-23 live fix): NX2512's SpanAngle on
        // this definition does NOT mean "total arc across the pattern" —
        // the live part measured 10° between instances after setting
        // SpanAngle=120. The verified-correct path for BOTH extents is
        // UsePitchOption=Angle + PitchAngle per STEP:
        //   full circle:  pitch = 360/count (6 instances → 60°, T1-proven);
        //   partial arc:  pitch = total/(count−1) (5 instances over 120° →
        //                 0°/30°/60°/90°/120°, the last instance ON the end
        //                 angle; count−1 steps between count positions).
        // SpanAngle is deliberately left untouched.
        spacing.UsePitchOption = AngularPatternSpacing.UsePitchOptions.Angle;
        double stepDeg = parameters.TotalAngleDeg is { } total
            ? total / (parameters.Count - 1)
            : 360.0 / parameters.Count;
        spacing.PitchAngle.SetFormula(stepDeg.ToString("R", CultureInfo.InvariantCulture));
        double pitchReadback = spacing.PitchAngle.Value;
        double spanReadback = 0;
        try { spanReadback = spacing.SpanAngle.Value; } catch { /* not carried */ }
        PhaseLog.Write(phaseTag,
            $"circular definition: type=Circular copies={parameters.Count} " +
            $"fullCircle={parameters.TotalAngleDeg == null} " +
            $"readback: copies='{spacing.NCopies.Value}' usePitch={spacing.UsePitchOption} " +
            $"pitch={pitchReadback:0.###} span={spanReadback:0.###} axisType={axis.Type}");
    }

    /// <summary>
    /// The circular-pattern postcondition verdict, as a pure function so
    /// every sub-condition is unit-testable offline. Same shape as
    /// LinearPatternPostcondition: managed-class match per mode, alive,
    /// instance evidence (feature mode) / exact body growth (geometry
    /// mode with the seed staying).
    /// </summary>
    internal static (bool Pass, string Reason) CircularPatternPostcondition(
        bool committed, bool alive, bool typeMatches, int instanceCount, int requestedCount,
        int bodiesBefore, int bodiesAfter, int expectedNewBodies, bool isGeometryMode)
    {
        if (!committed)
            return (false, "CommitFeature 未返回特征对象");
        if (!alive)
            return (false, "提交的特征对象已失效或被抑制");
        if (!typeMatches)
            return (false, "特征类型与模式不匹配（feature 模式应为 PatternFeature，geometry 模式应为 PatternGeometry）");
        if (isGeometryMode)
        {
            if (bodiesAfter != bodiesBefore + expectedNewBodies)
                return (false,
                    $"实体数量变化 {bodiesBefore}→{bodiesAfter}，预期新增 {expectedNewBodies} 个副本（原始实体保留）");
        }
        else
        {
            if (instanceCount <= 0)
                return (false, "阵列特征未包含任何实例证据（GetAllContainedFeatures 为空）");
        }
        return (true, "ok");
    }

    // ------------------------------------------------------------------
    // Seed resolution — the linear tool's unified priority (selection
    // first, then remembered ref / unique candidate), shared shape with
    // LinearPatternToolExecutor but extracted here so the circular tool
    // owns its diagnostics. Each resolver picks ONLY its own object kind
    // from the selection, so one selection can carry both a seed and an
    // axis object without ambiguity.
    // ------------------------------------------------------------------

    private sealed record SeedResolution(NXObject? Seed, string Description, string? Error);

    /// <summary>True for feature classes whose output is a LINE/CURVE (an
    /// AXIS candidate for this tool), not a solid body. NXOpen.xml: the
    /// AssociativeLine feature ("LINE(7)" in the T4 selection) outputs no
    /// bodies; casting it to a seed produced the live "解析出 0 个实体"
    /// refusal. Kept to the verified line-feature class — never a name
    /// guess.</summary>
    private static bool IsAxisLikeFeature(Feature f) =>
        f is NXOpen.Features.AssociativeLine;

    private SeedResolution ResolveFeatureSeed(
        Part workPart, string phaseTag, string workPartIdentity)
    {
        // 1. selection: exactly one live, non-suppressed Feature whose
        //    class can be patterned (anything but the tree-only nodes).
        var liveSelection = CollectSelectionObjects();
        var selectedFeatures = new List<Feature>();
        var nonFeatureDiagnoses = new List<string>();
        foreach (var obj in liveSelection)
        {
            switch (obj)
            {
                case Feature f when !IsDead(f):
                    // Axis-like line features are the axis resolver's input,
                    // never seed candidates (T4 live fix).
                    if (IsAxisLikeFeature(f))
                        break;
                    selectedFeatures.Add(f);
                    break;
                case DatumAxis or DatumPlane or Body or Edge or Face or NXOpen.Point:
                    // Axis/geometry objects in the same selection are for
                    // the axis resolver — not a seed diagnosis.
                    break;
                case Feature f:
                    nonFeatureDiagnoses.Add("选中的特征已失效");
                    break;
                case NXOpen.Line:
                    // Axis candidate (axis resolver's job).
                    break;
                default:
                    nonFeatureDiagnoses.Add($"选中的 {obj.GetType().Name} 不是建模特征");
                    break;
            }
        }
        if (selectedFeatures.Count == 1 && nonFeatureDiagnoses.Count == 0)
        {
            var f = selectedFeatures[0];
            PhaseLog.Write(phaseTag,
                $"seed: explicit feature selection (Scene S): {f.JournalIdentifier}");
            return new SeedResolution(f, $"特征 {f.JournalIdentifier}", null);
        }
        if (selectedFeatures.Count > 1)
        {
            return new SeedResolution(null, "",
                $"当前选中了 {selectedFeatures.Count} 个特征，无法确定要阵列哪一个。请只选中一个特征后重试。");
        }
        if (nonFeatureDiagnoses.Count > 0)
        {
            return new SeedResolution(null, "",
                "当前选择中没有可阵列的建模特征（" + string.Join("；", nonFeatureDiagnoses) + "）。请选中特征后重试。");
        }

        // 2. empty selection → remembered feature ref → typed inventory.
        string? featureRef = LastCreatedFeatureRefProvider?.Invoke();
        Feature? seed = null;
        if (!string.IsNullOrWhiteSpace(featureRef))
        {
            seed = FindHoleFeatureByRef(workPart, featureRef);
            if (seed == null || IsDead(seed))
                seed = null;
        }
        if (seed == null)
        {
            var inventory = new List<Core.EdgeTargetResolution.InventoryEntry>();
            foreach (object item in workPart.Features.ToArray())
            {
                if (item is not Feature f || IsDead(f))
                    continue;
                inventory.Add(new Core.EdgeTargetResolution.InventoryEntry(
                    f.JournalIdentifier, Kind: "feature",
                    RefType: f.GetType().Name, Name: TryName(f),
                    Subtype: null, FaceCount: null, IsBox: false));
            }
            var decision = Core.EdgeTargetResolution.ResolveFeatureFromInventory(
                inventory, "Hole", null);
            if (!decision.Success)
                return new SeedResolution(null, "", decision.Reason);
            var typed = inventory.Where(i => i.RefType == "Hole").ToList();
            seed = FindHoleFeatureByRef(workPart, typed[0].Ref);
            if (seed == null || IsDead(seed))
                return new SeedResolution(null, "",
                    "目标特征在清单验证后已不可解析（可能被并发删除或撤销）。请重试。");
        }
        return new SeedResolution(seed, $"特征 {seed.JournalIdentifier}", null);
    }

    private SeedResolution ResolveGeometrySeed(
        Part workPart, string phaseTag, string workPartIdentity)
    {
        // 1. selection: exactly one live solid Body (feature tree nodes
        //    that own exactly one solid body count as that body).
        var liveSelection = CollectSelectionObjects();
        var selectedBodies = new List<Body>();
        var nonBodyDiagnoses = new List<string>();
        foreach (var obj in liveSelection)
        {
            switch (obj)
            {
                case Body b when !IsDead(b):
                    selectedBodies.Add(b);
                    break;
                case DatumAxis or DatumPlane or NXOpen.Point:
                    // axis-resolver objects — not a seed diagnosis
                    break;
                case Body:
                    nonBodyDiagnoses.Add("选中的实体已失效");
                    break;
                case Feature f:
                    // T4 2026-10-23 live fix: line/curve features (e.g. the
                    // AssociativeLine LINE(7)) have NO body output — they are
                    // AXIS candidates, not seed candidates. Treating them as
                    // a broken seed ("解析出 0 个实体") refused a selection
                    // that contained a perfectly good body + axis pair.
                    if (IsAxisLikeFeature(f))
                        break;
                    try
                    {
                        var owned = f.GetBodies()
                            .Where(x => x is Body bb && !IsDead(bb) && !bb.IsSheetBody)
                            .Cast<Body>().ToList();
                        if (owned.Count == 1)
                            selectedBodies.Add(owned[0]);
                        else
                            nonBodyDiagnoses.Add(
                                $"特征 {TryName(f) ?? SafeFeatureObjectId(f)} 解析出 {owned.Count} 个实体（预期 1）");
                    }
                    catch (Exception ex)
                    {
                        nonBodyDiagnoses.Add($"特征 {SafeFeatureObjectId(f)} 无法读取：{ex.Message}");
                    }
                    break;
                case NXOpen.Line:
                    // A raw line object: axis candidate (axis resolver's job).
                    break;
                default:
                    nonBodyDiagnoses.Add($"选中的 {obj.GetType().Name} 不是实体");
                    break;
            }
        }
        if (selectedBodies.Count == 1 && nonBodyDiagnoses.Count == 0)
        {
            var b = selectedBodies[0];
            PhaseLog.Write(phaseTag,
                $"seed: explicit body selection (Scene S): {b.JournalIdentifier}");
            return new SeedResolution(b, $"实体 {b.JournalIdentifier}", null);
        }
        if (selectedBodies.Count > 1)
        {
            return new SeedResolution(null, "",
                $"当前选中了 {selectedBodies.Count} 个实体，无法确定要阵列哪一个。请只选中一个实体后重试。");
        }
        if (nonBodyDiagnoses.Count > 0)
        {
            return new SeedResolution(null, "",
                "当前选择中没有可阵列的实体（" + string.Join("；", nonBodyDiagnoses) + "）。请选中实体后重试。");
        }

        // 2. empty selection → remembered ref → unique candidate.
        string? bodyRef = LastCreatedBodyRefProvider?.Invoke();
        int bodyCount = 0;
        foreach (object item in workPart.Bodies)
            if (item is Body) bodyCount++;

        var candidateDecision = Core.EdgeTargetResolution.ResolveCandidateCount(bodyCount, bodyRef);
        if (!candidateDecision.Success)
            return new SeedResolution(null, "", candidateDecision.Reason);
        if (string.IsNullOrWhiteSpace(bodyRef))
        {
            foreach (object item in workPart.Bodies)
            {
                if (item is Body single && !IsDead(single))
                {
                    bodyRef = single.JournalIdentifier;
                    break;
                }
            }
        }
        var seedBody = FindLiveBodyByRef(workPart, bodyRef!, phaseTag);
        if (seedBody == null)
        {
            return new SeedResolution(null, "",
                $"要阵列的实体（{bodyRef}）在当前工作部件中已不存在（可能被删除或撤销）。请重新创建实体后重试。");
        }
        return new SeedResolution(seedBody, $"实体 {seedBody.JournalIdentifier}", null);
    }

    // ------------------------------------------------------------------
    // Rotation-axis resolution — explicit kinds only, never a guess.
    //   plus_x/plus_y/plus_z: absolute axis through the origin (Direction
    //     smart object + the origin point — the same construction the
    //     linear tool's XDirection uses).
    //   datum_axis: the selection must contain EXACTLY ONE live DatumAxis
    //     (or DatumAxisFeature resolving to one). Its direction and a
    //     point on it build the axis definition.
    //   line_edge: the selection must contain EXACTLY ONE live straight
    //     Edge; its two endpoints define the axis line. A cylindrical
    //     edge is refused (the axis of a circle is not this tool's job to
    //     guess — the user should create/select a datum axis instead).
    // A Body is NEVER accepted as the axis.
    // ------------------------------------------------------------------

    private sealed record PatternAxisResolution(Direction? Direction, NXOpen.Point? Point, string? Error);

    private PatternAxisResolution ResolveRotationAxis(
        Part workPart, CircularPatternAxis kind, string phaseTag)
    {
        if (kind == CircularPatternAxis.PlusX || kind == CircularPatternAxis.PlusY ||
            kind == CircularPatternAxis.PlusZ)
        {
            var vec = kind switch
            {
                CircularPatternAxis.PlusX => new Vector3d(1, 0, 0),
                CircularPatternAxis.PlusY => new Vector3d(0, 1, 0),
                _ => new Vector3d(0, 0, 1),
            };
            var origin = new Point3d(0, 0, 0);
            var direction = workPart.Directions.CreateDirection(
                origin, vec, SmartObject.UpdateOption.WithinModeling);
            var point = workPart.Points.CreatePoint(origin);
            PhaseLog.Write(phaseTag,
                $"axis: absolute {kind} through origin");
            return new PatternAxisResolution(direction, point, null);
        }

        // Selection-driven kinds: read the CURRENT selection (fresh, not
        // the seed resolver's copy).
        var liveSelection = CollectSelectionObjects();
        if (kind == CircularPatternAxis.DatumAxis)
        {
            var axes = new List<DatumAxis>();
            foreach (var obj in liveSelection)
            {
                switch (obj)
                {
                    case DatumAxis da when !IsDead(da):
                        axes.Add(da);
                        break;
                    case DatumAxisFeature daf when !IsDead(daf):
                        try
                        {
                            var das = daf.GetEntities().OfType<DatumAxis>()
                                .Where(a => !IsDead(a)).ToList();
                            axes.AddRange(das);
                        }
                        catch (Exception ex)
                        {
                            PhaseLog.Write(phaseTag,
                                $"axis: datum axis feature unreadable: {ex.Message}");
                        }
                        break;
                }
            }
            if (axes.Count == 0)
            {
                return new PatternAxisResolution(null, null,
                    "axis=datum_axis 需要用户在 NX 中选中一个基准轴，但当前选区没有基准轴。请选中基准轴后重试，" +
                    "或改用 plus_x/plus_y/plus_z 绝对轴。");
            }
            if (axes.Count > 1)
            {
                return new PatternAxisResolution(null, null,
                    $"当前选中了 {axes.Count} 个基准轴，无法确定旋转轴。请只选中一个基准轴后重试。");
            }
            var datum = axes[0];
            return BuildAxisFromDirectionObject(workPart, datum, "基准轴", phaseTag);
        }

        // line_edge — accepts BOTH a topological straight Edge and a
        // standalone line object/feature (the T4 2026-10-23 live case:
        // the user selected the AssociativeLine feature LINE(7); the old
        // edge-only scan ignored it and the seed resolver mis-diagnosed
        // it as a broken body candidate). The line's REAL geometry (its
        // own start point and direction) builds the axis — no global-axis
        // fallback, no silent substitution with a datum axis.
        var lines = new List<(double Sx, double Sy, double Sz, double Dx, double Dy, double Dz)>();
        var edgeDiagnoses = new List<string>();
        foreach (var obj in liveSelection)
        {
            switch (obj)
            {
                case Edge e when !IsDead(e):
                    try
                    {
                        if (e.SolidEdgeType.ToString().ToLowerInvariant() != "linear")
                        {
                            edgeDiagnoses.Add("选中的边不是直线");
                            break;
                        }
                        e.GetVertices(out var v1, out var v2);
                        lines.Add((v1.X, v1.Y, v1.Z, v2.X - v1.X, v2.Y - v1.Y, v2.Z - v1.Z));
                    }
                    catch (Exception ex)
                    {
                        edgeDiagnoses.Add($"选中的边无法读取：{ex.Message}");
                    }
                    break;
                case NXOpen.Line line when !IsDead(line):
                    try
                    {
                        var s = line.StartPoint;
                        var e2 = line.EndPoint;
                        lines.Add((s.X, s.Y, s.Z, e2.X - s.X, e2.Y - s.Y, e2.Z - s.Z));
                    }
                    catch (Exception ex)
                    {
                        edgeDiagnoses.Add($"选中的直线无法读取：{ex.Message}");
                    }
                    break;
                case Feature f when !IsDead(f) && IsAxisLikeFeature(f):
                    try
                    {
                        // The AssociativeLine feature's line entity carries
                        // the real geometry (Feature.GetEntities → Line).
                        var lineEntities = f.GetEntities().OfType<NXOpen.Line>()
                            .Where(l => !IsDead(l)).ToList();
                        if (lineEntities.Count == 1)
                        {
                            var s = lineEntities[0].StartPoint;
                            var e2 = lineEntities[0].EndPoint;
                            lines.Add((s.X, s.Y, s.Z, e2.X - s.X, e2.Y - s.Y, e2.Z - s.Z));
                        }
                        else
                        {
                            edgeDiagnoses.Add(
                                $"直线特征 {SafeFeatureObjectId(f)} 解析出 {lineEntities.Count} 条线（预期 1）");
                        }
                    }
                    catch (Exception ex)
                    {
                        edgeDiagnoses.Add($"直线特征 {SafeFeatureObjectId(f)} 无法读取：{ex.Message}");
                    }
                    break;
                case Edge:
                    edgeDiagnoses.Add("选中的边已失效");
                    break;
            }
        }
        if (lines.Count == 0)
        {
            string diag = edgeDiagnoses.Count > 0 ? "（" + string.Join("；", edgeDiagnoses) + "）" : "";
            return new PatternAxisResolution(null, null,
                $"axis=line_edge 需要用户在 NX 中选中一条直线（拓扑直线边或直线特征）作为旋转轴，" +
                $"但当前选区没有可用直线{diag}。请选中直线后重试，或改用 plus_x/plus_y/plus_z 绝对轴。");
        }
        if (lines.Count > 1)
        {
            return new PatternAxisResolution(null, null,
                $"当前选中了 {lines.Count} 条直线，无法确定旋转轴。请只选中一条直线后重试。");
        }
        var (sx, sy, sz, dx, dy, dz) = lines[0];
        double len = Math.Sqrt(dx * dx + dy * dy + dz * dz);
        if (len < 1e-9)
        {
            return new PatternAxisResolution(null, null,
                "选中的直线退化为一个点，无法作为旋转轴。");
        }
        try
        {
            var origin = new Point3d(sx, sy, sz);
            var direction = workPart.Directions.CreateDirection(
                origin, new Vector3d(dx, dy, dz), SmartObject.UpdateOption.WithinModeling);
            var point = workPart.Points.CreatePoint(origin);
            PhaseLog.Write(phaseTag,
                $"axis: line resolved (start=({sx:0.##},{sy:0.##},{sz:0.##}) " +
                $"dir=({dx:0.##},{dy:0.##},{dz:0.##}))");
            return new PatternAxisResolution(direction, point, null);
        }
        catch (Exception ex)
        {
            PhaseLog.Write(phaseTag,
                $"axis: line read FAILED: {ex.GetType().Name}: {ex.Message}");
            return new PatternAxisResolution(null, null,
                $"无法从选中的直线读取旋转轴几何（{ex.Message}）。请重新选中直线，或改用绝对轴。");
        }
    }

    /// <summary>Builds the axis definition smart objects from a DatumAxis
    /// via the datum's own smart geometry (NXOpen.xml-verified members:
    /// DatumAxis.Direction = Direction object with a Vector3d .Vector;
    /// DatumAxis.Origin = Point object with part-space Coordinates).</summary>
    private PatternAxisResolution BuildAxisFromDirectionObject(
        Part workPart, DatumAxis datum, string axisText, string phaseTag)
    {
        try
        {
            // DatumAxis.Direction is a plain Vector3d and DatumAxis.Origin a
            // plain Point3d (compile-verified) — build our own (point,
            // direction) smart objects from those values.
            var vector = datum.Direction;
            var coordinates = datum.Origin;
            var direction = workPart.Directions.CreateDirection(
                coordinates, vector, SmartObject.UpdateOption.WithinModeling);
            var point = workPart.Points.CreatePoint(coordinates);
            PhaseLog.Write(phaseTag,
                $"axis: {axisText} resolved (dir=({vector.X:0.##},{vector.Y:0.##},{vector.Z:0.##}) " +
                $"point=({coordinates.X:0.##},{coordinates.Y:0.##},{coordinates.Z:0.##}))");
            return new PatternAxisResolution(direction, point, null);
        }
        catch (Exception ex)
        {
            PhaseLog.Write(phaseTag,
                $"axis: {axisText} read FAILED: {ex.GetType().Name}: {ex.Message}");
            return new PatternAxisResolution(null, null,
                $"无法从{axisText}读取旋转轴几何（{ex.Message}）。请重新选中基准轴，或改用绝对轴。");
        }
    }

    /// <summary>Builds the axis definition from a straight edge's two
    /// endpoints (a verified read path — the context reader uses the same
    /// Edge.GetVertices geometry).</summary>
    private PatternAxisResolution BuildAxisFromEdge(
        Part workPart, Edge edge, string phaseTag)
    {
        try
        {
            edge.GetVertices(out var v1, out var v2);
            var delta = new Vector3d(v2.X - v1.X, v2.Y - v1.Y, v2.Z - v1.Z);
            double len = Math.Sqrt(delta.X * delta.X + delta.Y * delta.Y + delta.Z * delta.Z);
            if (len < 1e-9)
            {
                return new PatternAxisResolution(null, null,
                    "选中的边退化为一个点，无法作为旋转轴。");
            }
            var origin = new Point3d(v1.X, v1.Y, v1.Z);
            var direction = workPart.Directions.CreateDirection(
                origin, delta, SmartObject.UpdateOption.WithinModeling);
            var point = workPart.Points.CreatePoint(origin);
            PhaseLog.Write(phaseTag,
                $"axis: straight edge resolved (endpoints ({v1.X:0.##},{v1.Y:0.##},{v1.Z:0.##})→" +
                $"({v2.X:0.##},{v2.Y:0.##},{v2.Z:0.##}))");
            return new PatternAxisResolution(direction, point, null);
        }
        catch (Exception ex)
        {
            PhaseLog.Write(phaseTag,
                $"axis: edge read FAILED: {ex.GetType().Name}: {ex.Message}");
            return new PatternAxisResolution(null, null,
                $"无法从选中的边读取旋转轴几何（{ex.Message}）。请重新选中直线边，或改用绝对轴。");
        }
    }
}
