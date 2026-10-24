namespace NXAI.Assistant.Core;

/// <summary>
/// Phase 9.5: "perimeter" (周边) edge-target resolution decision — the
/// pure, offline-testable half of the auto-target capability. Inputs are
/// NX facts the host gathered from the live model (body stats + edge
/// inventory); the decision NEVER trusts model-provided references or
/// coordinates.
///
/// The rule set (intentionally narrow — anything not provably a
/// standard box refuses with a diagnosis, never widens the selection):
///   A body qualifies as a STANDARD BOX when
///     - it is a solid with exactly 6 planar faces, and
///     - it has exactly 12 edges, ALL linear, and
///     - every face is a rectangle (4 edges), and
///     - every edge has exactly 2 vertices, and the vertex-edge incidence
///       is 3 edges per vertex × 8 vertices (the box combinatorics), and
///   the tool has NOT modified the body (the host checks feature output
///   separately; a body carrying extra feature faces fails the 6-face test
///   and refuses).
/// A qualifying box's "perimeter" = ALL 12 of its edges (the outer prism
/// edges of the solid — a box has no other edges to disambiguate).
///
/// Anything else (multiple bodies without a clear candidate, non-box
/// topology, modified bodies, unknown) → refuse with a reason the user can
/// act on. The host then asks instead of guessing.
/// </summary>
public static class EdgeTargetResolution
{
    /// <summary>Body facts the host collected from the live part.</summary>
    public sealed record BodyEdgeFacts(
        string BodyRef,
        int FaceCount,
        int PlanarFaceCount,
        int EdgeCount,
        int LinearEdgeCount,
        bool AllEdgesHaveTwoVertices,
        bool ThreeEdgesPerVertex,
        bool EveryFaceIsFourSided);

    /// <summary>Target decision.</summary>
    public sealed record PerimeterDecision(
        bool Success,
        string Reason,
        int ExpectedEdgeCount = 0);

    /// <summary>
    /// Decides whether the given body facts support an unambiguous
    /// "perimeter = all edges" interpretation.
    /// </summary>
    public static PerimeterDecision ResolveBoxPerimeter(BodyEdgeFacts facts)
    {
        if (facts.FaceCount != 6)
            return new(false,
                $"目标实体有 {facts.FaceCount} 个面（标准长方体应为 6 个平面）。" +
                "实体可能已被修改（孔/圆角/斜角等），无法可靠确定'周边'范围。请明确选择要倒角的边，或说明目标范围。");
        if (facts.PlanarFaceCount != 6)
            return new(false,
                $"目标实体的 6 个面中只有 {facts.PlanarFaceCount} 个平面，不是标准长方体。" +
                "请明确选择要倒角的边。");
        if (facts.EdgeCount != 12)
            return new(false,
                $"目标实体有 {facts.EdgeCount} 条边（标准长方体应为 12 条）。" +
                "实体可能已被修改，无法可靠确定'周边'范围。请明确选择要倒角的边。");
        if (facts.LinearEdgeCount != 12)
            return new(false,
                $"目标实体的 12 条边中有 {12 - facts.LinearEdgeCount} 条不是直线，不是标准长方体。" +
                "请明确选择要倒角的边。");
        if (!facts.AllEdgesHaveTwoVertices)
            return new(false,
                "目标实体存在端点异常的边（标准长方体每条边恰有两个顶点）。请明确选择要倒角的边。");
        if (!facts.ThreeEdgesPerVertex)
            return new(false,
                "目标实体的顶点-边关系不符合长方体拓扑（每个顶点应恰有 3 条边）。请明确选择要倒角的边。");
        if (!facts.EveryFaceIsFourSided)
            return new(false,
                "目标实体存在非四边形面，不是标准长方体。请明确选择要倒角的边。");
        return new(true,
            $"标准长方体（{facts.BodyRef}）'周边'解析为全部 12 条外棱边。",
            12);
    }

    /// <summary>
    /// Multi-body candidate decision: an empty selection + a remembered
    /// candidate resolves ONLY when exactly one candidate exists. Two or
    /// more bodies without an explicit reference refuse (the tool never
    /// picks "the biggest" or "the newest" on its own).
    /// </summary>
    public static PerimeterDecision ResolveCandidateCount(int bodyCount, string? rememberedRef)
    {
        if (bodyCount == 0)
            return new(false, "当前工作部件没有任何实体，无法解析目标。");
        if (bodyCount == 1)
            return new(true, "工作部件只有一个实体，作为目标。");
        if (!string.IsNullOrWhiteSpace(rememberedRef))
            return new(true,
                $"使用最近成功创建的实体（{rememberedRef}）作为目标候选（执行前会重新验证）。");
        return new(false,
            $"当前工作部件有 {bodyCount} 个实体且没有明确目标。请说明要对哪个实体操作" +
            "（例如'给刚创建的长方体倒圆角'），或选中目标实体/边后重试。");
    }

    // ------------------------------------------------------------------
    // Generic feature re-identification (Phase 9.7) — the empty-selection
    // re-location path that never depends on remembered chat state.
    // ------------------------------------------------------------------

    /// <summary>
    /// One inert, already-verified-by-the-host row of the live feature/body
    /// inventory: what the host probed from the CURRENT work part. Neither
    /// this record nor anything the decision prints ever trusts model text —
    /// agents only ever name things the inventory itself reported, so the
    /// re-location round-trips against real NX objects.
    /// </summary>
    public sealed record InventoryEntry(
        string Ref,
        string Kind, // body | feature
        string RefType, // the NX managed class name, e.g. Body, HoleFeature
        string? Name,
        string? Subtype,
        int? FaceCount,
        bool IsBox);

    /// <summary>
    /// Generic empty-selection body re-location: given the gathered live
    /// body facts (already liveness-checked by the host), pick the ONE
    /// candidate. Policy is deliberately conservative:
    ///   - exactly one solid body → it;
    ///   - multiple bodies, but exactly one is a STANDARD BOX by the same
    ///     6-face/12-edge combinatorics (the "长方体" users name) → it;
    ///   - anything else (0, or 2+ boxes / none box) → refuse; the tool
    ///     never picks on volume, recency implied by tree order, or shape
    ///     similarity it cannot verify from the given facts.
    /// The decision NEVER trusts model-provided references, never mutates,
    /// and its output is re-validated by the caller before any edit.
    /// </summary>
    public static PerimeterDecision ResolveBodyFromInventory(
        IReadOnlyList<InventoryEntry> bodies)
    {
        var solid = bodies.Where(b => b.Kind == "body").ToList();
        if (solid.Count == 0)
            return new(false, "当前工作部件没有任何实体，无法解析目标。");
        if (solid.Count == 1)
            return new(true, $"工作部件只有一个实体（{solid[0].Ref}），作为目标。");
        var boxes = solid.Where(b => b.IsBox).ToList();
        if (boxes.Count == 1)
            return new(true,
                $"多个实体中只有一个标准长方体（{boxes[0].Ref}），自动解析为'周边'目标。");
        if (boxes.Count > 1)
            return new(false,
                $"当前工作部件有 {solid.Count} 个实体，其中 {boxes.Count} 个均为标准长方体，" +
                "无法确定'长方体'指哪一个。请说明目标（例如'给第二个长方体倒圆角'），" +
                "或选中目标边后重试。");
        return new(false,
            $"当前工作部件有 {solid.Count} 个实体但没有一个标准长方体，无法推断'周边'范围。" +
            "请选中目标边后重试。");
    }

    /// <summary>
    /// Generic empty-selection FEATURE re-location (mirrors the body path):
    /// given the gathered live feature facts (already liveness/suppression
    /// checked by the host), pick the ONE candidate whose managed feature
    /// class matches the requested kind. Policy:
    ///   - exactly one feature of the requested type → it (the common
    ///     "刚创建的那个孔" request after a chat reset);
    ///   - multiple of the type → refuse unless the caller supplied a
    ///     name hint that matches EXACTLY one displayed name (Ordinal
    ///     match — a name came from our own inventory, never free text);
    ///   - zero → refuse with an actionable reason.
    /// </summary>
    public static PerimeterDecision ResolveFeatureFromInventory(
        IReadOnlyList<InventoryEntry> features, string wantedType, string? nameHint)
    {
        var typed = features.Where(f =>
            f.Kind == "feature" &&
            string.Equals(f.RefType, wantedType, StringComparison.Ordinal)).ToList();
        if (typed.Count == 0)
            return new(false,
                $"当前工作部件没有可阵列的 {wantedType} 特征。请先创建要阵列的特征（如孔），或明确说明目标。");
        if (typed.Count == 1)
            return new(true,
                $"工作部件只有一个 {wantedType} 特征（{typed[0].Ref}），作为目标候选。");
        var matched = string.IsNullOrWhiteSpace(nameHint)
            ? null
            : typed.FirstOrDefault(f => f.Name != null &&
                string.Equals(f.Name, nameHint, StringComparison.Ordinal));
        if (matched != null)
            return new(true, $"按显示名称唯一匹配 {wantedType} 特征（{matched.Ref}）。");
        return new(false,
            $"当前工作部件有 {typed.Count} 个 {wantedType} 特征，目标不唯一。" +
            "请说明要对哪个特征做阵列（可引用特征名称）。");
    }
}
