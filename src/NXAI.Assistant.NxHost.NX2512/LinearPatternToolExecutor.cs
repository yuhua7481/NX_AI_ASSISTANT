using System.Globalization;
using System.Text.Json;
using NXAI.Assistant.Core;
using NXOpen;
using NXOpen.Features;
using NXOpen.GeometricUtilities;
using NXOpen.UF;

namespace NXAI.Assistant.NxHost.NX2512;

/// <summary>
/// Phase 9.6: linear-pattern executor (create_linear_pattern), TWO modes
/// that NX2512 genuinely separates (user-recorded journals "阵列特征" /
/// "阵列几何特征" + assembly reflection, no name guessing):
///
///   FEATURE mode — PatternFeatureBuilder
///     (FeatureCollection.CreatePatternFeatureBuilder(Feature)):
///     FeatureList = the seed feature (the controller's last verified
///     featureRef, re-resolved live in the part); PatternService
///     (PatternDefinition) → PatternType=Linear → RectangularDefinition:
///     XDirection = Directions.CreateDirection(origin, vector, UpdateOption)
///     (1-D array: UseYDirectionToggle stays false), XSpacing =
///     DistancePatternSpacing (SpaceType=Offset, NCopies=pitch count,
///     PitchDistance=spacing expression); PatternMethod=Simple;
///     OutputOption=PatternFeature. Postcondition: committed object is
///     Features.PatternFeature; its contained instance count is verified
///     via GetAllContainedFeatures when readable (a plain PatternFeature
///     with zero contained features fails).
///
///   GEOMETRY mode — PatternGeometryBuilder
///     (FeatureCollection.CreatePatternGeometryBuilder(PatternGeometry)):
///     GeometryToPattern = the seed BODY (SelectDisplayableObjectList —
///     bodies/faces/curves are accepted by NX; this tool supports whole
///     Body seeds only); HideOriginal=false (the original stays);
///     IsAssociative=true; same PatternService linear definition.
///     Postcondition: committed object is Features.PatternGeometry and the
///     part's body count grew by exactly (count - 1) (the copies; the
///     seed stays — HideOriginal=false).
///
/// Shared: direction/count/pitch validation, Undo mark + rollback, Builder
/// Destroy on all paths, postcondition before reporting success.
/// </summary>
public sealed partial class NxToolExecutor
{
    public CreateBlockExecutionResult ExecuteCreateLinearPattern(
        CreateLinearPatternParameters parameters, string? expectedWorkPartFullPath)
    {
        string modeText = parameters.Mode == LinearPatternMode.Feature ? "阵列特征" : "阵列几何体";
        string phaseTag = "execute.lin_pattern";
        PhaseLog.Write(phaseTag,
            $"begin: mode={parameters.Mode} dir={parameters.Direction} count={parameters.Count} " +
            $"pitch={parameters.PitchMm:0.###} includeSeed={parameters.IncludeSeed} " +
            (parameters.Direction2 != null
                ? $"dir2={parameters.Direction2} count2={parameters.Count2} pitch2={parameters.Pitch2Mm:0.###} "
                : "") +
            $"expectedPart={expectedWorkPartFullPath ?? "(null)"}");

        if (!TryResolveWorkPart(expectedWorkPartFullPath, "线性阵列",
                out Session session, out Part workPart, out string workPartIdentity,
                out CreateBlockExecutionResult? failure))
        {
            PhaseLog.Write(phaseTag, "REFUSED: work part resolution failed");
            LogFailure(new InvalidOperationException("线性阵列前置失败：工作部件解析失败。"),
                workPartIdentity: "(unknown)", operation: "CreateLinearPattern");
            return failure!;
        }

        // ---- Target resolution (host-side, auto-target capable) ----
        NXObject? seedObject;
        string targetDescription;
        int bodiesBefore;
        try { bodiesBefore = CountBodies(workPart); }
        catch (Exception ex)
        {
            PhaseLog.Write(phaseTag,
                $"ABORTED in pre-state snapshot: {ex.GetType().Name}: {ex.Message}");
            LogFailure(ex, workPartIdentity, "CreateLinearPattern");
            return CreateBlockExecutionResult.Failure(
                ToolExecutionStatus.ExecutionFailed,
                $"线性阵列前置快照发生异常（{ex.GetType().Name}：{ex.Message}），未执行任何建模操作。",
                workPartIdentity: workPartIdentity);
        }

        if (parameters.Mode == LinearPatternMode.Feature)
        {
            // Phase 9.7: generic re-identification — the remembered ref is
            // a HINT; when a new chat cleared it, the LIVE feature tree of
            // the work part is re-inventoried and the requested kind is
            // re-located from the real features (never from chat history).
            string? featureRef = LastCreatedFeatureRefProvider?.Invoke();
            Feature? feature = null;
            // The managed class NX reports for a hole feature is
            // NXOpen.Features.Hole (NXOpen.xml-verified; "HoleFeature" was
            // the builder factory name, not the feature class).
            string wantedType = "Hole";
            if (!string.IsNullOrWhiteSpace(featureRef))
            {
                feature = FindHoleFeatureByRef(workPart, featureRef);
                if (feature == null || IsDead(feature))
                {
                    PhaseLog.Write(phaseTag,
                        $"remembered feature {featureRef} MISS — falling to inventory");
                    feature = null;
                }
            }
            if (feature == null)
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
                    inventory, wantedType, null);
                if (!decision.Success)
                {
                    PhaseLog.Write(phaseTag, $"REFUSED(feature): {decision.Reason}");
                    return CreateBlockExecutionResult.Failure(
                        ToolExecutionStatus.PreconditionsChanged,
                        decision.Reason,
                        workPartIdentity: workPartIdentity);
                }
                // Re-resolve from the part: the decision requires exactly
                // one typed row (the multi-row paths already refused), so
                // the single row's ref is the candidate.
                var typed = inventory.Where(i =>
                    i.RefType == wantedType).ToList();
                feature = FindHoleFeatureByRef(workPart, typed[0].Ref);
                if (feature == null || IsDead(feature))
                {
                    PhaseLog.Write(phaseTag, "REFUSED: inventory feature not resolvable");
                    return CreateBlockExecutionResult.Failure(
                        ToolExecutionStatus.PreconditionsChanged,
                        "目标特征在清单验证后已不可解析（可能被并发删除或撤销）。请重试。",
                        workPartIdentity: workPartIdentity);
                }
            }
            featureRef = feature.JournalIdentifier;
            seedObject = feature;
            targetDescription = $"特征 {featureRef}";
        }
        else
        {
            // ---- Geometry mode target resolution, unified priority ----
            // 1. CURRENT SELECTION first: exactly one live solid Body in
            //    the NX selection IS the seed — other bodies in the part
            //    never veto an explicit user selection (the 2026-10-23 live
            //    failure: 2 bodies + 1 selected → "2 bodies no target"
            //    refused and the selection was ignored). Non-Body objects
            //    (faces/edges/features) are NOT silently converted; a
            //    selection that mixes non-Body objects refuses with a
            //    diagnosis rather than guessing.
            // 2. Empty selection → remembered ref (re-validated live), then
            //    the single-body / remembered-candidate decision as before.
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
                    case Body b:
                        nonBodyDiagnoses.Add("选中的实体已失效");
                        break;
                    case Feature f:
                        // A tree node whose feature owns exactly one solid
                        // body IS the body users pick in the tree (the same
                        // resolution policy NxContextReader.DescribeFeature
                        // applies when describing the selection).
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
                    default:
                        nonBodyDiagnoses.Add($"选中的 {obj.GetType().Name} 不是实体");
                        break;
                }
            }

            Body? seedBody;
            if (selectedBodies.Count == 1 && nonBodyDiagnoses.Count == 0)
            {
                seedBody = selectedBodies[0];
                PhaseLog.Write(phaseTag,
                    $"target: explicit body selection (Scene S): {seedBody.JournalIdentifier}");
            }
            else if (selectedBodies.Count == 1 && nonBodyDiagnoses.Count > 0)
            {
                // Mixed selection: the user picked a body AND something
                // else — refuse rather than process an unclear intent.
                PhaseLog.Write(phaseTag, $"REFUSED: mixed selection ({nonBodyDiagnoses.Count} non-body)");
                return CreateBlockExecutionResult.Failure(
                    ToolExecutionStatus.PreconditionsChanged,
                    "选中对象中混有非实体目标（" + string.Join("；", nonBodyDiagnoses) +
                    "）。请只选中要阵列的实体后重试。",
                    workPartIdentity: workPartIdentity);
            }
            else if (selectedBodies.Count > 1)
            {
                PhaseLog.Write(phaseTag, $"REFUSED: {selectedBodies.Count} bodies selected");
                return CreateBlockExecutionResult.Failure(
                    ToolExecutionStatus.PreconditionsChanged,
                    $"当前选中了 {selectedBodies.Count} 个实体，无法确定要阵列哪一个。" +
                    "请只选中一个实体后重试。",
                    workPartIdentity: workPartIdentity);
            }
            else if (nonBodyDiagnoses.Count > 0)
            {
                // Selection contained objects but NO usable body.
                PhaseLog.Write(phaseTag, "REFUSED: selection has no body");
                return CreateBlockExecutionResult.Failure(
                    ToolExecutionStatus.PreconditionsChanged,
                    "当前选择中没有可阵列的实体（" + string.Join("；", nonBodyDiagnoses) +
                    "）。请选中实体后重试。",
                    workPartIdentity: workPartIdentity);
            }
            else
            {
                // --- 2. empty selection → remembered ref, then inventory ---
                seedBody = null;
                string? bodyRef = LastCreatedBodyRefProvider?.Invoke();
                int bodyCount = 0;
                foreach (object item in workPart.Bodies)
                    if (item is Body) bodyCount++;

                var candidateDecision = Core.EdgeTargetResolution.ResolveCandidateCount(bodyCount, bodyRef);
                if (!candidateDecision.Success)
                {
                    PhaseLog.Write(phaseTag, $"REFUSED: {candidateDecision.Reason}");
                    return CreateBlockExecutionResult.Failure(
                        ToolExecutionStatus.PreconditionsChanged,
                        candidateDecision.Reason,
                        workPartIdentity: workPartIdentity);
                }
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
                seedBody = FindLiveBodyByRef(workPart, bodyRef!, phaseTag);
                if (seedBody == null)
                {
                    PhaseLog.Write(phaseTag, $"REFUSED: body {bodyRef} not resolvable");
                    return CreateBlockExecutionResult.Failure(
                        ToolExecutionStatus.PreconditionsChanged,
                        $"要阵列的实体（{bodyRef}）在当前工作部件中已不存在（可能被删除或撤销）。" +
                        "请重新创建实体后重试。",
                        workPartIdentity: workPartIdentity);
                }
                PhaseLog.Write(phaseTag, "target: empty selection — remembered/single-body path");
            }

            seedObject = seedBody;
            targetDescription = $"实体 {seedBody.JournalIdentifier}" +
                (TryName(seedBody) is { } n ? $"（{n}）" : "");
        }
        PhaseLog.Write(phaseTag, $"target resolved: {targetDescription} ({seedObject.GetType().Name})");

        // Direction vector from the enum (absolute model-space axes).
        var (vec, dirText) = parameters.Direction switch
        {
            LinearPatternDirection.PlusX => (new Vector3d(1, 0, 0), "+X"),
            LinearPatternDirection.MinusX => (new Vector3d(-1, 0, 0), "-X"),
            LinearPatternDirection.PlusY => (new Vector3d(0, 1, 0), "+Y"),
            LinearPatternDirection.MinusY => (new Vector3d(0, -1, 0), "-Y"),
            LinearPatternDirection.PlusZ => (new Vector3d(0, 0, 1), "+Z"),
            _ => (new Vector3d(0, 0, -1), "-Z"),
        };
        bool isBidirectional = parameters.Direction2 != null;
        var (vec2, dir2Text) = parameters.Direction2 switch
        {
            LinearPatternDirection.PlusX => ((Vector3d, string))(new Vector3d(1, 0, 0), "+X"),
            LinearPatternDirection.MinusX => (new Vector3d(-1, 0, 0), "-X"),
            LinearPatternDirection.PlusY => (new Vector3d(0, 1, 0), "+Y"),
            LinearPatternDirection.MinusY => (new Vector3d(0, -1, 0), "-Y"),
            LinearPatternDirection.PlusZ => (new Vector3d(0, 0, 1), "+Z"),
            LinearPatternDirection.MinusZ => (new Vector3d(0, 0, -1), "-Z"),
            null => (default(Vector3d), ""),
            _ => (default(Vector3d), ""),
        };
        int totalInstances = isBidirectional
            ? parameters.Count * parameters.Count2!.Value
            : parameters.Count;

        int expectedNewBodies = parameters.Mode == LinearPatternMode.Geometry
            ? totalInstances - (parameters.IncludeSeed ? 1 : 0)
            : 0;

        Builder? builder = null;
        Session.UndoMarkId markId = default;
        bool markCreated = false;
        try
        {
            markId = session.SetUndoMark(
                Session.MarkVisibility.Visible, $"NXAI 线性阵列（{modeText}）");
            markCreated = true;
            PhaseLog.Write(phaseTag, $"undo mark created: NXAI 线性阵列（{modeText}）");

            // ---- Pattern definition (shared) ----
            // The seed reference point: feature mode infers from the seed
            // (UseInferredReferencePoint=true); geometry mode uses the body
            // reference through the ReferencePoint service.
            PatternDefinition? pattern = null;
            Feature? committedFeature;
            if (parameters.Mode == LinearPatternMode.Feature)
            {
                var pfb = workPart.Features.CreatePatternFeatureBuilder(null!);
                builder = pfb;
                pfb.PatternMethod = PatternFeatureBuilder.PatternMethodOptions.Simple;
                pfb.OutputOption = PatternFeatureBuilder.OutputOptions.PatternFeature;
                // Inferred reference point (the deprecated UseInferredReferencePoint
                // was replaced by the ReferencePointService in NX9).
                pfb.ReferencePointService.IsReferencePointInferred = true;

                var feature = (Feature)seedObject!;
                pfb.FeatureList.Add(feature);

                pattern = pfb.PatternService;
                ApplyLinearDefinition(pattern, workPart, parameters, vec, vec2, phaseTag);

                committedFeature = pfb.CommitFeature() as Feature;
            }
            else
            {
                var pgb = workPart.Features.CreatePatternGeometryBuilder(null!);
                builder = pgb;
                pgb.IsAssociative = true;
                pgb.HideOriginal = false; // the original stays (IncludeSeed)
                pgb.CopyThreads = true;

                var body = (Body)seedObject!;
                pgb.GeometryToPattern.Add(body);

                pattern = pgb.PatternService;
                ApplyLinearDefinition(pattern, workPart, parameters, vec, vec2, phaseTag);

                committedFeature = pgb.CommitFeature() as Feature;
            }

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
                    typeMatches = parameters.Mode == LinearPatternMode.Feature
                        ? committedFeature is NXOpen.Features.PatternFeature
                        : committedFeature is NXOpen.Features.PatternGeometry;
                }
                catch (Exception ex)
                {
                    PhaseLog.Write(phaseTag, $"feature read FAILED: {ex.GetType().Name}: {ex.Message}");
                }
                try
                {
                    // Instance evidence: contained features of the pattern.
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

            var (postOk, verdictReason) = LinearPatternPostcondition(
                committedFeature != null, featureAlive, typeMatches,
                instanceCount, totalInstances,
                bodiesBefore, bodiesAfter, expectedNewBodies,
                parameters.Mode == LinearPatternMode.Geometry);
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
                (bool undone, string undoError) = Rollback(session, markId, $"NXAI 线性阵列（{modeText}）");
                LogFailure(new InvalidOperationException(
                        $"create_linear_pattern postcondition failed: alive={featureAlive} " +
                        $"type={featureType} typeMatches={typeMatches} instances={instanceCount} " +
                        $"bodies {bodiesBefore}→{bodiesAfter} (expected +{expectedNewBodies})"),
                    workPartIdentity, "CreateLinearPattern");
                return CreateBlockExecutionResult.Failure(
                    ToolExecutionStatus.VerificationFailed,
                    $"线性阵列创建后验证失败：{verdictReason}。" +
                    (undone ? "已回滚本次操作。" : $"回滚未完成：{undoError}。请在 NX 撤销列表中手动撤销。"),
                    rolledBack: undone, workPartIdentity: workPartIdentity);
            }

            string featureRef = SafeFeatureObjectId(committedFeature!);
            string dirSummary = isBidirectional
                ? $"{dirText}（{parameters.Count} 个）× {dir2Text}（{parameters.Count2} 个）"
                : dirText;
            string details = JsonSerializer.Serialize(new Dictionary<string, object?>
            {
                ["featureRef"] = featureRef,
                ["featureType"] = featureType,
                ["mode"] = parameters.Mode.ToString(),
                ["instanceCount"] = instanceCount,
                ["direction"] = dirText,
                ["count"] = parameters.Count,
                ["pitchMm"] = parameters.PitchMm,
                ["bidirectional"] = isBidirectional,
                ["direction2"] = isBidirectional ? dir2Text : null,
                ["count2"] = isBidirectional ? parameters.Count2 : null,
                ["pitch2Mm"] = isBidirectional ? parameters.Pitch2Mm : null,
                ["totalInstances"] = totalInstances,
            });
            return CreateBlockExecutionResult.Success(
                parameters.PitchMm, totalInstances, bodiesAfter,
                $"已创建线性阵列——{modeText}：目标 {targetDescription}，方向 {dirSummary}，" +
                (isBidirectional
                    ? $"一次原生矩形阵列共 {totalInstances} 个实例（各方向间距 " +
                      $"{parameters.PitchMm:0.###} / {parameters.Pitch2Mm:0.###} 毫米），"
                    : $"共 {parameters.Count} 个实例（间距 {parameters.PitchMm:0.###} 毫米），") +
                $"特征 {featureRef}（类型 {featureType}，实例证据 {instanceCount}）。",
                workPartIdentity, details);
        }
        catch (Exception ex)
        {
            PhaseLog.Write(phaseTag,
                $"EXCEPTION: {ex.GetType().Name}: {ex.Message} (markCreated={markCreated})");
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
                ? Rollback(session, markId, $"NXAI 线性阵列（{modeText}）")
                : (false, "撤销标记尚未创建，无需回滚");
            LogFailure(ex, workPartIdentity, markCreated ? "CreateLinearPattern" : "CreateLinearPattern-premark");
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
    /// Applies the linear definition to a freshly obtained
    /// PatternDefinition: PatternType=Linear, XDirection from the absolute
    /// axis, XSpacing=DistancePatternSpacing (SpaceType=Offset,
    /// NCopies=count, PitchDistance=pitch).
    /// Phase 9.8 bidirectional (params.Direction2 set): UseYDirectionToggle
    /// =true, YDirection from the second absolute axis, YSpacing with the
    /// second count/pitch — ONE native rectangular pattern produces
    /// count×count2 instances in a single commit (NXOpen.xml-verified:
    /// GeometricUtilities.RectangularPattern "specification along two
    /// linear axes, which may or may not be orthogonal"; YDirection "any
    /// vector not parallel to the x axis"). The parser already refuses a
    /// parallel second axis; never issued as two nested patterns.
    /// The reference point for feature mode is inferred by NX.
    /// </summary>
    private static void ApplyLinearDefinition(
        PatternDefinition pattern, Part workPart,
        CreateLinearPatternParameters parameters, Vector3d vec, Vector3d vec2, string phaseTag)
    {
        pattern.PatternType = PatternDefinition.PatternEnum.Linear;
        var rect = pattern.RectangularDefinition;
        var origin = new Point3d(0, 0, 0);
        var direction = workPart.Directions.CreateDirection(
            origin, vec, SmartObject.UpdateOption.WithinModeling);
        rect.XDirection = direction;

        if (parameters.Direction2 != null)
        {
            rect.UseYDirectionToggle = true;
            var direction2 = workPart.Directions.CreateDirection(
                origin, vec2, SmartObject.UpdateOption.WithinModeling);
            rect.YDirection = direction2;
            var spacing2 = rect.YSpacing;
            spacing2.SpaceType = PatternSpacing.SpacingType.Offset;
            spacing2.NCopies.SetFormula(parameters.Count2!.Value.ToString(CultureInfo.InvariantCulture));
            spacing2.PitchDistance.SetFormula(parameters.Pitch2Mm!.Value.ToString("R", CultureInfo.InvariantCulture));
            PhaseLog.Write(phaseTag,
                $"linear definition (bidirectional): dir2=({vec2.X:0.##},{vec2.Y:0.##},{vec2.Z:0.##}) " +
                $"copies2={parameters.Count2} pitch2={parameters.Pitch2Mm:0.###} " +
                $"readback: copies2='{spacing2.NCopies.Value}' pitch2='{spacing2.PitchDistance.Value}'");
        }
        else
        {
            rect.UseYDirectionToggle = false;
        }

        var spacing = rect.XSpacing;
        spacing.SpaceType = PatternSpacing.SpacingType.Offset;
        spacing.NCopies.SetFormula(parameters.Count.ToString(CultureInfo.InvariantCulture));
        spacing.PitchDistance.SetFormula(parameters.PitchMm.ToString("R", CultureInfo.InvariantCulture));
        PhaseLog.Write(phaseTag,
            $"linear definition: type=Linear dir=({vec.X:0.##},{vec.Y:0.##},{vec.Z:0.##}) " +
            $"copies={parameters.Count} pitch={parameters.PitchMm:0.###} useY={rect.UseYDirectionToggle} " +
            $"readback: copies='{spacing.NCopies.Value}' pitch='{spacing.PitchDistance.Value}'");
    }

    /// <summary>
    /// The linear-pattern postcondition verdict, as a pure function so
    /// every sub-condition is unit-testable offline.
    ///   committed/alive/typeMatches — as with the edge tools (the managed
    ///     class must be PatternFeature or PatternGeometry per mode);
    ///   instanceCount — the pattern's contained-feature count; for
    ///     Geometry mode NX may report fewer contained features (the copies
    ///     are bodies, not features), so only Feature mode hard-gates it;
    ///   bodies — Geometry mode must grow the part by exactly
    ///     expectedNewBodies (count minus seed when IncludeSeed); Feature
    ///     mode keeps the count unchanged (the pattern instances live
    ///     inside the feature).
    /// </summary>
    internal static (bool Pass, string Reason) LinearPatternPostcondition(
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
                    $"实体数量变化 {bodiesBefore}→{bodiesAfter}，预期新增 {expectedNewBodies} 个副本" +
                    (expectedNewBodies > 0 ? "（原始实体保留）" : ""));
        }
        else
        {
            if (instanceCount <= 0)
                return (false, "阵列特征未包含任何实例证据（GetAllContainedFeatures 为空）");
        }
        return (true, "ok");
    }
}
