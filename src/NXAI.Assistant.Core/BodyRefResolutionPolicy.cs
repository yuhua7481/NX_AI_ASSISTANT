namespace NXAI.Assistant.Core;

public sealed record BodyRefFact(string JournalIdentifier, string? DisplayName);

/// <summary>One selected-feature alias fact the host collected read-only:
/// the feature's display name and its OUTPUT body identity, offered only
/// when the host could map the feature to exactly one alive solid body
/// (0, 2+ or unreadable outputs → SelectionAlias absent).</summary>
public sealed record SelectionAlias(string FeatureName, string OutputBodyJournalIdentifier);

public sealed record BodyRefDecision(
    bool Success,
    string? MatchedJournalIdentifier,
    string Error);

/// <summary>
/// Boolean body-reference resolution (2026-10-21 live failure: the model
/// sent "block_1"/"Block" — invented display names — because no real
/// newBodyRef had ever reached it). Resolution contract, strictest first:
///   1. EXACT JournalIdentifier match (ordinal) — the only reference the
///      contract requires;
///   2. compatibility fallback: an EXACT, case-insensitive unique display
///      name — only when exactly ONE body carries that name; zero or
///      multiple matches refuse with a diagnosis;
///   3. selected-feature alias fallback: an EXACT, case-insensitive unique
///      FEATURE display name among the CURRENT selection whose host-verified
///      single output body identity is returned. The alias mapping was
///      already gated at collection time (exactly one alive solid output);
///      duplicate feature names refuse here. No substring/fuzzy matching,
///      no first-object picking, no selection-order substitution — target
///      and tool resolve independently in their requested roles.
/// A null candidate list (unreadable part) always refuses.
/// </summary>
public static class BodyRefResolutionPolicy
{
    public static BodyRefDecision Resolve(
        string requestedRef, IReadOnlyList<BodyRefFact> candidates,
        IReadOnlyList<SelectionAlias>? selectionAliases = null)
    {
        if (string.IsNullOrWhiteSpace(requestedRef))
            return new(false, null, "实体引用为空。");

        foreach (var c in candidates)
        {
            if (string.Equals(c.JournalIdentifier, requestedRef, StringComparison.Ordinal))
                return new(true, c.JournalIdentifier, "");
        }

        var byName = candidates
            .Where(c => c.DisplayName is { } name &&
                        string.Equals(name, requestedRef, StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (byName.Count == 1)
            return new(true, byName[0].JournalIdentifier, "");
        if (byName.Count > 1)
            return new(false, null,
                $"引用“{requestedRef}”匹配到 {byName.Count} 个同名实体" +
                $"（{string.Join("、", byName.Take(3).Select(c => c.JournalIdentifier))}" +
                $"{(byName.Count > 3 ? " 等" : "")}），无法唯一确定。请使用工具结果返回的实体引用。");

        if (selectionAliases is { Count: > 0 })
        {
            var byAlias = selectionAliases
                .Where(a => string.Equals(a.FeatureName, requestedRef, StringComparison.OrdinalIgnoreCase))
                .ToList();
            if (byAlias.Count == 1)
                return new(true, byAlias[0].OutputBodyJournalIdentifier,
                    $"selected-feature alias: {byAlias[0].FeatureName} → {byAlias[0].OutputBodyJournalIdentifier}");
            if (byAlias.Count > 1)
                return new(false, null,
                    $"引用“{requestedRef}”匹配到 {byAlias.Count} 个同名选中特征，无法唯一确定对应实体。请使用输出实体引用。");
        }

        return new(false, null,
            $"引用“{requestedRef}”不匹配任何实体（精确引用、唯一实体名或唯一选中特征）。" +
            "请使用先前工具结果中返回的实体引用（如 BLOCK(3) 的 newBodyRef 字段），或重新选中特征后查看上下文中的输出实体引用。");
    }
}
