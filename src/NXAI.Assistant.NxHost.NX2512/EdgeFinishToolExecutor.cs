using System.Globalization;
using System.Text.Json;
using NXAI.Assistant.Core;
using NXOpen;
using NXOpen.Features;
using NXOpen.UF;

namespace NXAI.Assistant.NxHost.NX2512;

/// <summary>
/// Phase 9.4: edge-finish executors (create_fillet / create_chamfer).
/// Integrated from the user-recorded journals (2026-10-23):
///   - Round corners of R2.cs: EdgeBlendBuilder + ScCollector +
///     EdgeMultipleSeedTangentRule(seedEdges, 0.5, true) + AddChainset(
///     collector, "radius-string") + Tolerance 0.01 — radius is the
///     chainset's expression string, not a standalone property.
///   - Inverted C2 bevel.cs: ChamferBuilder + EdgeTangentRule(seed, null,
///     true, 0.5, true, false) on the SmartCollector + symmetric offset
///     via the distance expression SetFormula("2") — ChamferOption.
///     SymmetricOffsets is the "C2" form.
/// Both act ONLY on the edges of the CURRENT selection, re-resolved after
/// user confirmation (never stored references, never auto-picked edges).
/// APIs verified against NX2512 NXOpen.xml:
///   - FeatureCollection.CreateEdgeBlendBuilder(Feature) /
///     CreateChamferBuilder(Feature) (null feature = create);
///   - EdgeBlendBuilder.AddChainset(ScCollector, string radius) /
///     GetChainsetAndSectionValue(index, out collector, out section, out
///     conic, out rho, out exp1, out exp2, out exp3) for readback;
///   - ChamferBuilder.Option { SymmetricOffsets, TwoOffsets, OffsetAndAngle
///     } / Method { EdgesAlongFaces, FacesAndTrim } / FirstOffsetExp /
///     AngleExp / SmartCollector / Tolerance;
///   - ScRuleFactory.CreateRuleEdgeDumb(Edge[]) — deterministic rule for
///     EXACTLY the selected edges (the tangent-chain rules in the journals
///     also capture tangent-adjacent edges the user did not pick; for a
///     tool that must not touch unselected edges we use the dumb rule).
/// </summary>
public sealed partial class NxToolExecutor : NxTargetResolutionSink
{
    // ------------------------------------------------------------------
    // Postcondition verdict (extracted, offline-testable)
    // ------------------------------------------------------------------

    /// <summary>
    /// The edge-finish postcondition verdict, as a pure function so every
    /// sub-condition is unit-testable offline. Inputs are the NX facts
    /// gathered right after DoUpdate:
    ///   committed  — CommitFeature returned a feature object;
    ///   alive      — the object is readable, not dead, not suppressed;
    ///   typeMatches— the committed object is the EXACT managed feature
    ///                class for the tool (EdgeBlend for fillet, Chamfer for
    ///                chamfer — the 2026-10-23 live fix: FeatureType STRING
    ///                matching against "EDGE_BLEND" was wrong; NX reports
    ///                "BLEND");
    ///   faceCount  — Feature.GetFaces().Count ("faces created by the
    ///                feature"); a real blend/chamfer builds new faces, so
    ///                zero proves nothing was built;
    ///   bodiesBefore/After — the in-place sanity check (edge finishes do
    ///                not create or destroy bodies).
    /// </summary>
    internal static (bool Pass, string Reason) EdgeFinishPostcondition(
        bool committed, bool alive, bool typeMatches, int faceCount,
        int bodiesBefore, int bodiesAfter)
    {
        if (!committed)
            return (false, "CommitFeature 未返回特征对象");
        if (!alive)
            return (false, "提交的特征对象已失效或被抑制");
        if (!typeMatches)
            return (false, "特征类型与工具不匹配（圆角应为 EdgeBlend，倒斜角应为 Chamfer）");
        if (faceCount <= 0)
            return (false, "特征未产生任何面（可能未实际生效）");
        if (bodiesAfter != bodiesBefore)
            return (false, $"实体数量变化（{bodiesBefore}→{bodiesAfter}），本次编辑不应改变实体数量");
        return (true, "ok");
    }

    // ------------------------------------------------------------------
    // Phase 9.5: auto-target candidate (controller-provided session state)
    // ------------------------------------------------------------------

    /// <summary>The controller wires this to its own session state; the
    /// value is re-read on EVERY empty-selection resolution (a delegate,
    /// never a stale copy). Null = no remembered candidate.</summary>
    public Func<string?>? LastCreatedBodyRefProvider { get; set; }

    /// <summary>Phase 9.6: the controller's most recent executor-verified
    /// FEATURE reference (for the linear-pattern FEATURE mode).</summary>
    public Func<string?>? LastCreatedFeatureRefProvider { get; set; }

    // ------------------------------------------------------------------
    // Shared selection resolution
    // ------------------------------------------------------------------

    /// <summary>Resolution result for the current-selection edges.</summary>
    private sealed record SelectedEdgesResolution(bool Success, List<Edge> Edges, string? Error);

    /// <summary>
    /// Resolves the CURRENT selection to a non-empty set of live solid
    /// EDGES of the work part. Feature nodes that resolve to exactly one
    /// Edge (rare) are accepted; everything else (faces, bodies, datums,
    /// curves, sketches) is diagnosed and refused. The selection must
    /// contain at least one edge; edges are deduplicated by
    /// JournalIdentifier; dead/unreadable entries are refused, never
    /// skipped silently into the target list.
    /// </summary>
    private static SelectedEdgesResolution ResolveSelectedEdges(string phaseTag)
    {
        var selected = new List<NXObject>();
        try
        {
            var uiSelection = NXOpen.UI.GetUI().SelectionManager;
            int count = uiSelection.GetNumSelectedObjects();
            for (int i = 0; i < count; i++)
            {
                if (uiSelection.GetSelectedTaggedObject(i) is NXObject obj)
                    selected.Add(obj);
            }
        }
        catch (Exception ex)
        {
            return new SelectedEdgesResolution(false, new List<Edge>(),
                $"读取当前选择失败（{ex.GetType().Name}：{ex.Message}）。请重新选择后重试。");
        }
        PhaseLog.Write(phaseTag, $"edge selection: {selected.Count} object(s)");

        if (selected.Count == 0)
        {
            return new SelectedEdgesResolution(false, new List<Edge>(),
                "当前没有选中的边。请先在 NX 中选中要处理的实体边（可多选）后重试。");
        }

        var edges = new List<Edge>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var diagnoses = new List<string>();
        foreach (var obj in selected)
        {
            switch (obj)
            {
                case Edge edge:
                    AddEdge(edge, "选中的边");
                    break;
                case Feature feature:
                {
                    // Feature tree nodes: accept only when the feature
                    // resolves to EXACTLY one live edge (the mirror of the
                    // point-feature rule); anything else is a diagnosis.
                    try
                    {
                        if (feature is PointFeature or DatumPlaneFeature or SketchFeature)
                        {
                            diagnoses.Add($"特征 {SafeFeatureObjectId(feature)} 不是边特征");
                            break;
                        }
                        var es = feature.GetEntities().OfType<Edge>().Where(e => !IsDead(e)).ToList();
                        if (es.Count == 1)
                            AddEdge(es[0], $"特征 {SafeFeatureObjectId(feature)} 的边");
                        else
                            diagnoses.Add($"特征 {SafeFeatureObjectId(feature)} 解析出 {es.Count} 条边（预期 1）");
                    }
                    catch (Exception ex)
                    {
                        diagnoses.Add($"特征 {SafeFeatureObjectId(feature)} 无法读取：{ex.Message}");
                    }
                    break;
                }
                default:
                    diagnoses.Add($"选中的 {obj.GetType().Name} 不是实体边");
                    break;
            }

            void AddEdge(Edge e, string source)
            {
                try
                {
                    if (IsDead(e))
                    {
                        diagnoses.Add($"{source} 已失效");
                        return;
                    }
                    if (seen.Add(e.JournalIdentifier))
                        edges.Add(e);
                }
                catch
                {
                    diagnoses.Add($"{source} 无法读取");
                }
            }
        }

        if (edges.Count == 0)
        {
            string diag = diagnoses.Count > 0 ? "；" + string.Join("；", diagnoses) : "";
            return new SelectedEdgesResolution(false, new List<Edge>(),
                $"当前选择中没有可用的实体边{diag}。请选中实体边后重试。");
        }
        if (diagnoses.Count > 0)
        {
            // Some selected objects were NOT edges: refuse rather than
            // silently processing the subset the user did not fully intend.
            return new SelectedEdgesResolution(false, new List<Edge>(),
                $"选中对象中混有非边目标（{string.Join("；", diagnoses)}）。" +
                "请只选中要处理的实体边后重试。");
        }
        PhaseLog.Write(phaseTag,
            $"edges resolved: {edges.Count} unique edge(s): " +
            string.Join(", ", edges.Select(e => SafeFeatureObjectId(e))));
        return new SelectedEdgesResolution(true, edges, null);
    }

    /// <summary>
    /// Re-resolves the recorded edge JournalIdentifiers in the CURRENT work
    /// part (post-confirmation liveness re-check). Every ref must resolve
    /// to a live edge of the same identity; any miss refuses — a stale
    /// selection never executes.
    /// </summary>
    private static SelectedEdgesResolution RevalidateEdges(
        Part workPart, IReadOnlyList<string> edgeRefs, string phaseTag)
    {
        var resolved = new List<Edge>();
        foreach (var reference in edgeRefs)
        {
            Edge? found = null;
            foreach (object item in workPart.Bodies)
            {
                if (item is not Body body)
                    continue;
                foreach (Edge edge in body.GetFaces().SelectMany(_ => Array.Empty<Edge>()))
                {
                    _ = edge;
                }
            }
            // Body.GetFaces does not yield edges directly; walk each face's
            // edges via Face.GetEdges (the verified per-face read path).
            foreach (object item in workPart.Bodies)
            {
                if (item is not Body body)
                    continue;
                foreach (Face face in body.GetFaces())
                {
                    foreach (Edge edge in face.GetEdges())
                    {
                        try
                        {
                            if (string.Equals(edge.JournalIdentifier, reference, StringComparison.Ordinal) &&
                                !IsDead(edge))
                            {
                                found = edge;
                                break;
                            }
                        }
                        catch
                        {
                            // unreadable edge: skip
                        }
                    }
                    if (found != null) break;
                }
                if (found != null) break;
            }
            if (found == null)
            {
                PhaseLog.Write(phaseTag, $"edge revalidation MISS: {reference}");
                return new SelectedEdgesResolution(false, new List<Edge>(),
                    $"选中的边 {reference} 已失效或不再属于当前工作部件。请重新选择边后重试。");
            }
            resolved.Add(found);
        }
        PhaseLog.Write(phaseTag, $"edges revalidated: {resolved.Count}");
        return new SelectedEdgesResolution(true, resolved, null);
    }

    /// <summary>
    /// Phase 9.5: edge-finish TARGET resolution — the reusable half the
    /// task splits out from feature execution. Priority order:
    ///   1. The CURRENT selection contains ≥1 valid edge → use exactly
    ///      those (Scene B: user's explicit selection always wins; the old
    ///      ResolveSelectedEdges diagnostics apply unchanged).
    ///   2. Selection is EMPTY → auto-target: take the remembered candidate
    ///      (the controller's most recent executor-verified body ref —
    ///      Scene A), re-resolve the LIVE body in the part (a deleted or
    ///      undone candidate refuses), then apply the "perimeter" rule:
    ///      only a STANDARD BOX (6 planar faces, 12 linear edges, box
    ///      vertex/face combinatorics — Core.EdgeTargetResolution) resolves
    ///      to all 12 edges; anything else refuses with a reason the user
    ///      can act on. The tool NEVER auto-picks edges of a non-box body.
    ///   3. Anything ambiguous (multi-body without a candidate, non-box,
    ///      invalid candidate) → refusal with a diagnosis — never a silent
    ///      widening to all edges of all bodies.
    /// </summary>
    private SelectedEdgesResolution ResolveEdgeFinishTarget(Part workPart, string phaseTag)
    {
        // --- 1. explicit selection first (unchanged Scene-B behavior) ---
        var liveSelection = CollectSelectionObjects();
        bool hasEdges = liveSelection.OfType<Edge>().Any() ||
            liveSelection.OfType<Feature>().Any(f =>
            {
                try { return f.GetEntities().OfType<Edge>().Any(); }
                catch { return false; }
            });
        if (liveSelection.Count > 0 && hasEdges)
        {
            PhaseLog.Write(phaseTag, "target: explicit edge selection (Scene B)");
            return ResolveSelectedEdges(phaseTag);
        }
        if (liveSelection.Count > 0)
        {
            // The user selected something that is NOT an edge (face, body,
            // point, …): keep the explicit-selection diagnostics — do not
            // silently fall through to auto-target and operate on an object
            // the user did not select.
            return ResolveSelectedEdges(phaseTag);
        }

        // --- 2. empty selection → auto-target ---
        // Phase 9.7: generic re-identification — the CURRENT inventory is
        // the authority; the remembered ref is only a hint that skips the
        // ambiguity check. A new chat (null remembered ref) still resolves
        // a single body or a unique standard box directly from the part.
        PhaseLog.Write(phaseTag, "target: selection EMPTY — attempting auto-target");
        string? candidateRef = LastCreatedBodyRefProvider?.Invoke();
        var liveBodies = new List<Core.EdgeTargetResolution.InventoryEntry>();
        var bodyList = new List<Body>();
        foreach (object item in workPart.Bodies)
        {
            if (item is not Body b || IsDead(b))
                continue;
            bodyList.Add(b);
            liveBodies.Add(new Core.EdgeTargetResolution.InventoryEntry(
                b.JournalIdentifier, Kind: "body", RefType: b.GetType().Name,
                Name: TryName(b), Subtype: b.IsSheetBody ? "sheet_body" : "solid_body",
                FaceCount: null, IsBox: false));
        }

        // Probe each solid body's box combinatorics ONLY when the generic
        // (no remembered ref) decision needs them — the remembered path
        // re-probes the chosen body below anyway. Read-only.
        if (string.IsNullOrWhiteSpace(candidateRef))
        {
            var boxFlags = new List<bool>();
            foreach (var b in bodyList)
            {
                if (b.IsSheetBody)
                {
                    boxFlags.Add(false);
                    continue;
                }
                var probeFacts = CollectBodyEdgeFacts(workPart, b, phaseTag);
                var verdict = Core.EdgeTargetResolution.ResolveBoxPerimeter(probeFacts);
                boxFlags.Add(verdict.Success);
                PhaseLog.Write(phaseTag,
                    $"inventory body={b.JournalIdentifier} isBox={verdict.Success}");
            }
            for (int i = 0; i < liveBodies.Count; i++)
                liveBodies[i] = liveBodies[i] with { IsBox = boxFlags[i] };
        }

        var bodyDecision = candidateRef != null
            ? Core.EdgeTargetResolution.ResolveCandidateCount(
                liveBodies.Count(e => e.Subtype == "solid_body"), candidateRef)
            : Core.EdgeTargetResolution.ResolveBodyFromInventory(liveBodies);
        if (!bodyDecision.Success)
        {
            PhaseLog.Write(phaseTag, $"target REFUSED: {bodyDecision.Reason}");
            return new SelectedEdgesResolution(false, new List<Edge>(), bodyDecision.Reason);
        }

        if (string.IsNullOrWhiteSpace(candidateRef))
        {
            // Phase 9.7: no remembered ref — the generic inventory decision
            // picked the target (single body, or the unique standard box).
            // Pull its ref from the inventory row the decision described.
            var chosen = liveBodies.FirstOrDefault(e =>
                e.Subtype == "solid_body" &&
                (liveBodies.Count(x => x.Subtype == "solid_body") == 1 || e.IsBox));
            if (chosen == null)
            {
                return new SelectedEdgesResolution(false, new List<Edge>(),
                    "无法从当前工作部件解析目标实体（清单与决策不符），请明确选择后重试。");
            }
            candidateRef = chosen.Ref;
        }

        var body = FindLiveBodyByRef(workPart, candidateRef!, phaseTag);
        if (body == null)
        {
            // The remembered ref no longer resolves (deleted/undone):
            // refuse and tell the user the target vanished — the controller
            // state stays; a fresh create re-establishes it.
            return new SelectedEdgesResolution(false, new List<Edge>(),
                $"最近创建的目标实体（{candidateRef}）在当前工作部件中已不存在（可能被删除或撤销）。" +
                "请重新创建目标实体或明确选择要处理的边。");
        }

        // --- "perimeter" geometric rule: standard box → all 12 edges ---
        var facts = CollectBodyEdgeFacts(workPart, body, phaseTag);
        var perimeter = Core.EdgeTargetResolution.ResolveBoxPerimeter(facts);
        if (!perimeter.Success)
        {
            PhaseLog.Write(phaseTag, $"target REFUSED(perimeter): {perimeter.Reason}");
            return new SelectedEdgesResolution(false, new List<Edge>(), perimeter.Reason);
        }

        // The actual edges: every live linear edge of the verified box body.
        var boxEdges = new List<Edge>();
        foreach (Face face in body.GetFaces())
        {
            foreach (Edge edge in face.GetEdges())
            {
                try
                {
                    if (IsDead(edge))
                        continue;
                    if (boxEdges.Any(e =>
                        string.Equals(e.JournalIdentifier, edge.JournalIdentifier, StringComparison.Ordinal)))
                        continue;
                    boxEdges.Add(edge);
                }
                catch
                {
                    // unreadable edge: not part of the target
                }
            }
        }
        if (boxEdges.Count != 12)
        {
            return new SelectedEdgesResolution(false, new List<Edge>(),
                $"目标长方体的实际边数为 {boxEdges.Count}（预期 12），拓扑与预检不符，拒绝执行以避免错误范围。");
        }
        PhaseLog.Write(phaseTag,
            $"target RESOLVED (Scene A): body={body.JournalIdentifier} perimeter edges=12");
        return new SelectedEdgesResolution(true, boxEdges, null);
    }

    private static List<NXObject> CollectSelectionObjects()    {
        var selected = new List<NXObject>();
        try
        {
            var uiSelection = NXOpen.UI.GetUI().SelectionManager;
            int count = uiSelection.GetNumSelectedObjects();
            for (int i = 0; i < count; i++)
            {
                if (uiSelection.GetSelectedTaggedObject(i) is NXObject obj)
                    selected.Add(obj);
            }
        }
        catch
        {
            // Selection unreadable → treated as empty by the caller's
            // next step; ResolveSelectedEdges re-reports the real error.
        }
        return selected;
    }

    /// <summary>Read-only display-name probe for inventory rows (never
    /// fatal: an unreadable name simply stays null).</summary>
    private static string? TryName(NXObject obj)
    {
        try
        {
            string? n = obj.Name;
            return string.IsNullOrWhiteSpace(n) ? null : n;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Resolves a body reference to the LIVE body of the current
    /// part (exact JournalIdentifier first, unique display name as the
    /// existing policy fallback). Null when the ref no longer resolves.</summary>
    private static Body? FindLiveBodyByRef(Part workPart, string bodyRef, string phaseTag)
    {
        var bodies = new List<(Body Body, string Id, string? Name)>();
        foreach (object item in workPart.Bodies)
        {
            if (item is not Body body || IsDead(body))
                continue;
            string? name = null;
            try { name = string.IsNullOrWhiteSpace(body.Name) ? null : body.Name; }
            catch { name = null; }
            bodies.Add((body, body.JournalIdentifier, name));
        }
        var exact = bodies.FirstOrDefault(b =>
            string.Equals(b.Id, bodyRef, StringComparison.Ordinal));
        if (exact.Body != null)
        {
            PhaseLog.Write(phaseTag, $"auto-target body resolved: {exact.Id}");
            return exact.Body;
        }
        var byName = bodies.Where(b =>
                b.Name != null &&
                string.Equals(b.Name, bodyRef, StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (byName.Count == 1)
        {
            PhaseLog.Write(phaseTag, $"auto-target body resolved by unique name: {byName[0].Id}");
            return byName[0].Body;
        }
        return null;
    }

    /// <summary>Collects the box-detection facts for a body: face counts,
    /// edge counts/types, vertex and face-siding combinatorics — the inputs
    /// Core.EdgeTargetResolution needs. Read-only.</summary>
    private static Core.EdgeTargetResolution.BodyEdgeFacts CollectBodyEdgeFacts(
        Part workPart, Body body, string phaseTag)
    {
        int faceCount = 0, planarFaceCount = 0;
        var edgeIds = new HashSet<string>(StringComparer.Ordinal);
        int linearEdges = 0, twoVertexEdges = 0;
        var vertexEdgeCount = new Dictionary<string, int>(StringComparer.Ordinal);
        var faceSides = new List<int>();

        foreach (Face face in body.GetFaces())
        {
            faceCount++;
            try
            {
                if (face.SolidFaceType.ToString().ToLowerInvariant() == "planar")
                    planarFaceCount++;
            }
            catch { /* unreadable type: not planar-confirmed */ }

            int faceEdgeCount = 0;
            foreach (Edge edge in face.GetEdges())
            {
                faceEdgeCount++;
                string id;
                try { id = edge.JournalIdentifier; }
                catch { continue; }
                if (edgeIds.Add(id))
                {
                    try
                    {
                        if (edge.SolidEdgeType.ToString().ToLowerInvariant() == "linear")
                            linearEdges++;
                        edge.GetVertices(out var v1, out var v2);
                        double degenerate = Math.Abs(v1.X - v2.X) + Math.Abs(v1.Y - v2.Y) + Math.Abs(v1.Z - v2.Z);
                        if (degenerate > 1e-9)
                            twoVertexEdges++;
                        foreach (var v in new[] { v1, v2 })
                        {
                            var key = $"{v.X:G17},{v.Y:G17},{v.Z:G17}";
                            vertexEdgeCount[key] = vertexEdgeCount.GetValueOrDefault(key) + 1;
                        }
                    }
                    catch { /* unreadable geometry */ }
                }
            }
            faceSides.Add(faceEdgeCount);
        }

        var facts = new Core.EdgeTargetResolution.BodyEdgeFacts(
            body.JournalIdentifier,
            faceCount,
            planarFaceCount,
            edgeIds.Count,
            linearEdges,
            AllEdgesHaveTwoVertices: twoVertexEdges == edgeIds.Count && edgeIds.Count > 0,
            ThreeEdgesPerVertex: vertexEdgeCount.Count == 8 &&
                vertexEdgeCount.Values.All(c => c == 3),
            EveryFaceIsFourSided: faceSides.Count == 6 && faceSides.All(c => c == 4));
        PhaseLog.Write(phaseTag,
            $"body facts: faces={faceCount}(planar {planarFaceCount}) edges={edgeIds.Count}" +
            $"(linear {linearEdges}) vertices={vertexEdgeCount.Count} faceSides=[{string.Join(",", faceSides)}]");
        return facts;
    }

    // ------------------------------------------------------------------
    // create_fillet (EdgeBlendBuilder)
    // ------------------------------------------------------------------

    public CreateBlockExecutionResult ExecuteCreateFillet(
        CreateFilletParameters parameters, string? expectedWorkPartFullPath)
        => ExecuteEdgeFinish(
            parameters, null, expectedWorkPartFullPath,
            CreateFilletRequestParser.ToolName, "圆角", "NXAI 边倒圆", "execute.fillet");

    // ------------------------------------------------------------------
    // create_chamfer (ChamferBuilder, symmetric offsets)
    // ------------------------------------------------------------------

    public CreateBlockExecutionResult ExecuteCreateChamfer(
        CreateChamferParameters parameters, string? expectedWorkPartFullPath)
        => ExecuteEdgeFinish(
            null, parameters, expectedWorkPartFullPath,
            CreateChamferRequestParser.ToolName, "倒斜角", "NXAI 倒斜角", "execute.chamfer");

    private CreateBlockExecutionResult ExecuteEdgeFinish(
        CreateFilletParameters? filletParameters,
        CreateChamferParameters? chamferParameters,
        string? expectedWorkPartFullPath,
        string toolName, string familyText, string undoName, string phaseTag)
    {
        double sizeMm = filletParameters?.RadiusMm ?? chamferParameters!.DistanceMm;
        bool isFillet = filletParameters != null;
        PhaseLog.Write(phaseTag,
            $"begin: tool={toolName} size={sizeMm:0.###}mm expectedPart={expectedWorkPartFullPath ?? "(null)"}");

        if (!TryResolveWorkPart(expectedWorkPartFullPath, familyText,
                out Session session, out Part workPart, out string workPartIdentity,
                out CreateBlockExecutionResult? failure))
        {
            PhaseLog.Write(phaseTag, "REFUSED: work part resolution failed");
            LogFailure(new InvalidOperationException($"{familyText}前置失败：工作部件解析失败。"),
                workPartIdentity: "(unknown)", operation: isFillet ? "CreateFillet" : "CreateChamfer");
            return failure!;
        }

        // ---- Target resolution (Phase 9.5): selection first, then the
        // remembered auto-target, then a clear refusal. ----
        var selection = ResolveEdgeFinishTarget(workPart, phaseTag);
        if (!selection.Success)
        {
            PhaseLog.Write(phaseTag, $"REFUSED(selection): {selection.Error}");
            return CreateBlockExecutionResult.Failure(
                ToolExecutionStatus.PreconditionsChanged,
                selection.Error!,
                workPartIdentity: workPartIdentity);
        }
        var edgeRefs = selection.Edges.Select(e => e.JournalIdentifier).ToList();
        int edgeCount = edgeRefs.Count;

        int bodiesBefore;
        try { bodiesBefore = CountBodies(workPart); }
        catch (Exception ex)
        {
            PhaseLog.Write(phaseTag,
                $"ABORTED in pre-state snapshot: {ex.GetType().Name}: {ex.Message}");
            LogFailure(ex, workPartIdentity, toolName);
            return CreateBlockExecutionResult.Failure(
                ToolExecutionStatus.ExecutionFailed,
                $"{familyText}前置快照发生异常（{ex.GetType().Name}：{ex.Message}），未执行任何建模操作。",
                workPartIdentity: workPartIdentity);
        }

        Builder? builder = null;
        Session.UndoMarkId markId = default;
        bool markCreated = false;
        try
        {
            markId = session.SetUndoMark(Session.MarkVisibility.Visible, undoName);
            markCreated = true;
            PhaseLog.Write(phaseTag, $"undo mark created: {undoName}");

            // Post-confirmation liveness re-check: every edge must still be
            // the same live object in the CURRENT work part.
            var revalidated = RevalidateEdges(workPart, edgeRefs, phaseTag);
            if (!revalidated.Success)
            {
                PhaseLog.Write(phaseTag, $"REFUSED(revalidate): {revalidated.Error}");
                return CreateBlockExecutionResult.Failure(
                    ToolExecutionStatus.PreconditionsChanged,
                    revalidated.Error!,
                    workPartIdentity: workPartIdentity);
            }

            string sizeFormula = sizeMm.ToString("R", CultureInfo.InvariantCulture);
            Feature? committedFeature;
            string? readbackText = null;
            if (isFillet)
            {
                // ---- EdgeBlendBuilder (journal-proven path) ----
                var blendBuilder = workPart.Features.CreateEdgeBlendBuilder(null!);
                builder = blendBuilder;
                blendBuilder.Tolerance = 0.01;
                blendBuilder.RemoveSelfIntersection = true;
                blendBuilder.PatchComplexGeometryAreas = true;
                blendBuilder.LimitFailingAreas = true;
                blendBuilder.AllInstancesOption = false;

                var collector = workPart.ScCollectors.CreateCollector();
                // Deterministic dumb rule: EXACTLY the selected edges (the
                // journals' tangent rules would also absorb tangent-adjacent
                // edges the user did not pick — not acceptable here).
                var rule = workPart.ScRuleFactory.CreateRuleEdgeDumb(revalidated.Edges.ToArray());
                collector.ReplaceRules(new SelectionIntentRule[] { rule }, false);
                int chainIndex = blendBuilder.AddChainset(collector, sizeFormula);
                PhaseLog.Write(phaseTag,
                    $"blend chainset added: index={chainIndex} radius='{sizeFormula}' edges={edgeCount}");

                // Readback gate: the chainset's radius expression must equal
                // the requested value (±0.001) — a refused/normalized string
                // would otherwise commit silently wrong geometry.
                blendBuilder.GetChainsetAndSectionValue(chainIndex,
                    out _, out _, out _, out _, out var radiusExp, out _, out _);
                if (radiusExp == null)
                {
                    PhaseLog.Write(phaseTag, "REFUSED: chainset radius expression unreadable");
                    return CreateBlockExecutionResult.Failure(
                        ToolExecutionStatus.VerificationFailed,
                        "圆角半径读回失败（链集半径表达式不可用），未提交任何几何。",
                        workPartIdentity: workPartIdentity);
                }
                double delta = Math.Abs(radiusExp.Value - sizeMm);
                readbackText = $"radius expr '{radiusExp.RightHandSide}' readback={radiusExp.Value:G17} delta={delta:G4}";
                PhaseLog.Write(phaseTag, $"blend readback: {readbackText}");
                if (delta > 0.001)
                {
                    return CreateBlockExecutionResult.Failure(
                        ToolExecutionStatus.VerificationFailed,
                        $"圆角半径读回不一致（设置 {sizeMm:0.###}，读回 {radiusExp.Value:0.###} 毫米），未提交任何几何。",
                        workPartIdentity: workPartIdentity);
                }

                committedFeature = blendBuilder.CommitFeature() as Feature;
            }
            else
            {
                // ---- ChamferBuilder (journal-proven symmetric path) ----
                var chamferBuilder = workPart.Features.CreateChamferBuilder(null!);
                builder = chamferBuilder;
                chamferBuilder.Tolerance = 0.01;
                // SymmetricOffsets = the "C2" form: one distance on both
                // faces. Non-symmetric modes are deliberately not offered.
                chamferBuilder.Option = ChamferBuilder.ChamferOption.SymmetricOffsets;
                chamferBuilder.Method = ChamferBuilder.OffsetMethod.EdgesAlongFaces;

                var collector = workPart.ScCollectors.CreateCollector();
                var rule = workPart.ScRuleFactory.CreateRuleEdgeDumb(revalidated.Edges.ToArray());
                collector.ReplaceRules(new SelectionIntentRule[] { rule }, false);
                chamferBuilder.SmartCollector = collector;
                // SymmetricOffsets takes the single FirstOffset expression;
                // the journal set it via SetFormula("2") — same here.
                var offsetExp = chamferBuilder.FirstOffsetExp;
                if (offsetExp == null)
                {
                    PhaseLog.Write(phaseTag, "REFUSED: FirstOffsetExp null");
                    return CreateBlockExecutionResult.Failure(
                        ToolExecutionStatus.VerificationFailed,
                        "倒角距离表达式不可用（FirstOffsetExp 为空），未提交任何几何。",
                        workPartIdentity: workPartIdentity);
                }
                offsetExp.SetFormula(sizeFormula);
                double delta = Math.Abs(offsetExp.Value - sizeMm);
                readbackText = $"offset expr '{offsetExp.RightHandSide}' readback={offsetExp.Value:G17} delta={delta:G4}";
                PhaseLog.Write(phaseTag, $"chamfer readback: {readbackText}");
                if (delta > 0.001)
                {
                    return CreateBlockExecutionResult.Failure(
                        ToolExecutionStatus.VerificationFailed,
                        $"倒角距离读回不一致（设置 {sizeMm:0.###}，读回 {offsetExp.Value:0.###} 毫米），未提交任何几何。",
                        workPartIdentity: workPartIdentity);
                }
                // Option readback: the builder must still carry
                // SymmetricOffsets right before commit.
                if (chamferBuilder.Option != ChamferBuilder.ChamferOption.SymmetricOffsets)
                {
                    PhaseLog.Write(phaseTag,
                        $"REFUSED: chamfer option readback={chamferBuilder.Option}");
                    return CreateBlockExecutionResult.Failure(
                        ToolExecutionStatus.VerificationFailed,
                        $"倒角类型读回不一致（builder 实际为 {chamferBuilder.Option}，要求对称等距），未提交任何几何。",
                        workPartIdentity: workPartIdentity);
                }

                committedFeature = chamferBuilder.CommitFeature() as Feature;
            }

            session.UpdateManager.DoUpdate(markId);
            PhaseLog.Write(phaseTag,
                $"commit returned: {(committedFeature != null ? committedFeature.GetType().Name + " " + SafeFeatureObjectId(committedFeature) : "null")} " +
                $"type={committedFeature?.FeatureType ?? "?"} update=ok");

            // ---- postcondition (facts from NX; verdict below) ----
            // 2026-10-23 live fix: the previous version required
            // FeatureType=="EDGE_BLEND" and a JournalIdentifier round-trip
            // through the feature tree — the real NX feature reports
            // type=BLEND and the string lookup proved unstable
            // (roundTrip=False rolled back perfectly good blends). The
            // verification now uses RELIABLE API facts only:
            //   1. the committed object is the exact managed feature class
            //      (EdgeBlend / Chamfer — NXOpen.xml-verified types);
            //   2. the feature is alive and unsuppressed;
            //   3. the feature OWNS faces (Feature.GetFaces() — "faces
            //      created by the feature", NX1899+): a real blend/chamfer
            //      produces new face geometry on the target body. Zero faces
            //      means nothing was actually built.
            // bodyCount staying equal stays as a sanity check (subtractive/
            // decorative in-place edits do not create bodies).
            string? featureType = null;
            bool featureAlive = false;
            bool typeMatches = false;
            int featureFaceCount = -1;
            if (committedFeature != null)
            {
                try
                {
                    featureAlive = !IsDead(committedFeature) && !committedFeature.Suppressed;
                    featureType = committedFeature.FeatureType;
                    typeMatches = isFillet
                        ? committedFeature is NXOpen.Features.EdgeBlend
                        : committedFeature is NXOpen.Features.Chamfer;
                }
                catch (Exception ex)
                {
                    PhaseLog.Write(phaseTag, $"feature read FAILED: {ex.GetType().Name}: {ex.Message}");
                }
                try
                {
                    featureFaceCount = committedFeature.GetFaces().Count();
                }
                catch (Exception ex)
                {
                    PhaseLog.Write(phaseTag, $"feature faces read FAILED: {ex.GetType().Name}: {ex.Message}");
                }
            }
            int bodiesAfter = CountBodies(workPart);
            PhaseLog.Write(phaseTag,
                $"feature evidence: alive={featureAlive} type={featureType} typeMatches={typeMatches} " +
                $"featureFaces={featureFaceCount} bodyCount={bodiesBefore}→{bodiesAfter}");

            bool postOk = committedFeature != null && featureAlive && typeMatches &&
                          featureFaceCount > 0 && bodiesAfter == bodiesBefore;
            if (!postOk)
            {
                var (verdictPass, verdictReason) = EdgeFinishPostcondition(
                    committedFeature != null, featureAlive, typeMatches,
                    featureFaceCount, bodiesBefore, bodiesAfter);
                PhaseLog.Write(phaseTag, $"postcondition verdict: pass={verdictPass} reason={verdictReason}");
                // Cleanup order (2026-10-23 live fix): the failed attempt's
                // builder must be destroyed BEFORE the undo-mark rollback —
                // UndoToMark invalidates the builder (live log: "试图使用不
                // 活动的对象" when Destroy ran after the rollback). The
                // destroy failure never masks the original verdict.
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
                (bool undone, string undoError) = Rollback(session, markId, undoName);
                LogFailure(new InvalidOperationException(
                        $"{toolName} postcondition failed: alive={featureAlive} type={featureType} " +
                        $"typeMatches={typeMatches} featureFaces={featureFaceCount} " +
                        $"bodies {bodiesBefore}→{bodiesAfter}"),
                    workPartIdentity, toolName);
                return CreateBlockExecutionResult.Failure(
                    ToolExecutionStatus.VerificationFailed,
                    $"{familyText}创建后验证失败：无法证明特征已正确提交" +
                    (!typeMatches && featureType != null
                        ? $"（实际特征类型 {featureType}）" : "") +
                    (featureFaceCount == 0 ? "（特征未产生任何面，可能未实际生效）" : "") + "。" +
                    (undone ? "已回滚本次操作。" : $"回滚未完成：{undoError}。请在 NX 撤销列表中手动撤销。"),
                    rolledBack: undone, workPartIdentity: workPartIdentity);
            }

            string featureRef = SafeFeatureObjectId(committedFeature!);
            string details = JsonSerializer.Serialize(new Dictionary<string, object?>
            {
                ["featureRef"] = featureRef,
                ["featureType"] = featureType,
                ["featureFaces"] = featureFaceCount,
                ["edgeCount"] = edgeCount,
                ["sizeMm"] = sizeMm,
                ["readback"] = readbackText,
                ["bodyCountUnchanged"] = true,
            });
            return CreateBlockExecutionResult.Success(
                sizeMm, edgeCount, bodiesAfter,
                $"已创建{familyText}（{sizeMm:0.###} 毫米 × {edgeCount} 条边，特征 {featureRef}，类型 {featureType}，生成面 {featureFaceCount} 个；读回 {readbackText}）。",
                workPartIdentity, details);
        }
        catch (Exception ex)
        {
            PhaseLog.Write(phaseTag,
                $"EXCEPTION: {ex.GetType().Name}: {ex.Message} (markCreated={markCreated})");
            // Cleanup order (2026-10-23 live fix): destroy the builder BEFORE
            // the undo-mark rollback — UndoToMark invalidates the builder
            // (live: "试图使用不活动的对象"). The destroy failure never
            // masks the original exception.
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
                ? Rollback(session, markId, undoName)
                : (false, "撤销标记尚未创建，无需回滚");
            LogFailure(ex, workPartIdentity, markCreated ? toolName : toolName + "-premark");
            return CreateBlockExecutionResult.Failure(
                ToolExecutionStatus.ExecutionFailed,
                $"{ex.GetType().Name}: {ex.Message}" +
                (undone ? "（本次操作已回滚，部件恢复原状。）" : $"（回滚未完成：{undoError}。请在 NX 撤销列表中手动撤销。）"),
                rolledBack: undone, workPartIdentity: workPartIdentity);
        }
        finally
        {
            // Normal success path (or a failure path whose pre-rollback
            // destroy already ran): destroy is idempotent-guarded by the
            // null check. A destroy failure on the success path means the
            // commit already owns the feature — logged, never fatal.
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
}
