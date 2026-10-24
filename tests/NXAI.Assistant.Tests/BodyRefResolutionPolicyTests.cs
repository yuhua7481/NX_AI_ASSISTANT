using NXAI.Assistant.Core;
using NXAI.Assistant.UI;
using Xunit;

namespace NXAI.Assistant.Tests;

/// <summary>
/// 2026-10-21 live failure: the model sent invented display names
/// ("block_1", "Block") as boolean body refs because no real newBodyRef had
/// ever reached it. Pins the resolution contract: exact JournalIdentifier
/// first; a unique exact display name is the only fallback; zero/multiple
/// name matches refuse with a diagnosis; empty refs refuse.
/// </summary>
public class BodyRefResolutionPolicyTests
{
    private static readonly IReadOnlyList<BodyRefFact> Bodies = new[]
    {
        new BodyRefFact("BLOCK(3)", null),
        new BodyRefFact("CYLINDER(4)", null),
        new BodyRefFact("BLOCK(7)", "撑板"),
    };

    [Fact]
    public void ExactJournalIdentifier_Matches()
    {
        var d = BodyRefResolutionPolicy.Resolve("BLOCK(3)", Bodies);
        Assert.True(d.Success);
        Assert.Equal("BLOCK(3)", d.MatchedJournalIdentifier);
    }

    [Fact]
    public void InventedAlias_Block1_IsRefused()
    {
        var d = BodyRefResolutionPolicy.Resolve("block_1", Bodies);
        Assert.False(d.Success);
        Assert.Null(d.MatchedJournalIdentifier);
        Assert.Contains("不匹配", d.Error);
    }

    [Fact]
    public void DisplayName_Block_IsRefused_WhenNoBodyCarriesIt()
    {
        var d = BodyRefResolutionPolicy.Resolve("Block", Bodies);
        Assert.False(d.Success);
    }

    [Fact]
    public void UniqueDisplayName_MatchesExactly()
    {
        var d = BodyRefResolutionPolicy.Resolve("撑板", Bodies);
        Assert.True(d.Success);
        Assert.Equal("BLOCK(7)", d.MatchedJournalIdentifier);
    }

    [Fact]
    public void DuplicateDisplayName_IsRefused()
    {
        var bodies = new[]
        {
            new BodyRefFact("BLOCK(3)", "块"),
            new BodyRefFact("BLOCK(7)", "块"),
        };
        var d = BodyRefResolutionPolicy.Resolve("块", bodies);
        Assert.False(d.Success);
        Assert.Contains("2 个同名实体", d.Error);
    }

    [Fact]
    public void EmptyRef_IsRefused()
    {
        Assert.False(BodyRefResolutionPolicy.Resolve("", Bodies).Success);
        Assert.False(BodyRefResolutionPolicy.Resolve("   ", Bodies).Success);
    }

    [Fact]
    public void CaseInsensitiveName_IsCompatibilityOnly()
    {
        // Name fallback is case-insensitive; identifier matching stays exact.
        var d = BodyRefResolutionPolicy.Resolve("撑板 ", Bodies);
        Assert.False(d.Success); // trailing space is not an exact name

        var d2 = BodyRefResolutionPolicy.Resolve("block(3)", Bodies);
        Assert.False(d2.Success); // identifier is case- and format-exact
    }

    [Fact]
    public void EmptyCandidateList_Refuses()
    {
        var d = BodyRefResolutionPolicy.Resolve("BLOCK(3)", Array.Empty<BodyRefFact>());
        Assert.False(d.Success);
    }

    // ---------- selected-feature alias fallback (2026-10-21: "Cylinder"/"Block") ----------

    private static readonly IReadOnlyList<SelectionAlias> Selection =
        new[] { new SelectionAlias("Cylinder", "CYLINDER(2)"), new SelectionAlias("Block", "BLOCK(1)") };

    [Fact]
    public void SelectedFeatureAlias_UniqueName_MapsToRealOutputBody()
    {
        var d = BodyRefResolutionPolicy.Resolve("Cylinder", Bodies, Selection);
        Assert.True(d.Success);
        Assert.Equal("CYLINDER(2)", d.MatchedJournalIdentifier);

        var d2 = BodyRefResolutionPolicy.Resolve("block", Bodies, Selection); // case-insensitive
        Assert.True(d2.Success);
        Assert.Equal("BLOCK(1)", d2.MatchedJournalIdentifier);
    }

    [Fact]
    public void RealJournalIdentifier_StillWins_OverAlias()
    {
        // BLOCK(3) is a real candidate identity (not reachable via any
        // alias): exact identifier hit wins and carries no alias marker.
        var d = BodyRefResolutionPolicy.Resolve("BLOCK(3)", Bodies, Selection);
        Assert.True(d.Success);
        Assert.Equal("BLOCK(3)", d.MatchedJournalIdentifier);
        Assert.False(d.Error.StartsWith("selected-feature alias", StringComparison.Ordinal));
    }

    [Fact]
    public void DuplicateFeatureAliases_AreRefused()
    {
        var selection = new[]
        {
            new SelectionAlias("Cylinder", "CYLINDER(2)"),
            new SelectionAlias("Cylinder", "CYLINDER(9)"),
        };
        var d = BodyRefResolutionPolicy.Resolve("Cylinder", Bodies, selection);
        Assert.False(d.Success);
        Assert.Contains("2 个同名选中特征", d.Error);
    }

    [Fact]
    public void ZeroAliasMatch_StillRefused()
    {
        var d = BodyRefResolutionPolicy.Resolve("Cone", Bodies, Selection);
        Assert.False(d.Success);
    }

    [Fact]
    public void TargetAndTool_KeepRequestedRoles_Independently()
    {
        // The caller resolves each ref separately; the policy never swaps
        // roles. Pin the two independent resolutions of the live failure.
        var target = BodyRefResolutionPolicy.Resolve("Cylinder", Bodies, Selection);
        var tool = BodyRefResolutionPolicy.Resolve("Block", Bodies, Selection);
        Assert.Equal("CYLINDER(2)", target.MatchedJournalIdentifier);
        Assert.Equal("BLOCK(1)", tool.MatchedJournalIdentifier);
        Assert.NotEqual(target.MatchedJournalIdentifier, tool.MatchedJournalIdentifier);
    }

    [Fact]
    public void Alias_WithoutOutputBodies_CannotExist_CollectionGate()
    {
        // Collection-side gate is structural: SelectionAlias instances are
        // only created for exactly-one-alive-solid outputs, so a feature
        // with 0 or 2 outputs simply contributes NO alias — a request naming
        // it cannot match anything and refuses (no guessing possible).
        var d = BodyRefResolutionPolicy.Resolve("UnresolvableFeature", Bodies, Selection);
        Assert.False(d.Success);
    }
}

/// <summary>
/// The creation-tool → boolean-tool reference contract: a primitive tool's
/// success result must carry the real newBodyRef in DetailsJson AND the
/// serialized tool-result payload sent to the model (the 2026-10-21 failure
/// was the model inventing refs because the payload never contained one).
/// </summary>
public class CreateToolBodyRefPayloadTests
{
    [Fact]
    public void PrimitiveSuccessResult_DetailsJson_CarriesRealNewBodyRef()
    {
        var details = System.Text.Json.JsonSerializer.Serialize(
            new Dictionary<string, object?> { ["newBodyRef"] = "BLOCK(3)" });
        var result = NXAI.Assistant.Core.CreateBlockExecutionResult.Success(
            10, 10, 10, "已在工作部件中创建长方体（实体引用 BLOCK(3)）。", "part", details);

        Assert.Contains("BLOCK(3)", result.DetailsJson);
    }

    [Fact]
    public void ToolResultPayload_MergesNewBodyRef_ToTopLevel()
    {
        var details = System.Text.Json.JsonSerializer.Serialize(
            new Dictionary<string, object?> { ["newBodyRef"] = "CYLINDER(4)" });
        var result = NXAI.Assistant.Core.CreateBlockExecutionResult.Success(
            10, 10, 20, "已在工作部件中创建圆柱体（实体引用 CYLINDER(4)）。", "part", details);

        string payload = ChatSessionController.BuildToolResultPayload(result);
        using var doc = System.Text.Json.JsonDocument.Parse(payload);
        Assert.Equal("CYLINDER(4)", doc.RootElement.GetProperty("newBodyRef").GetString());
        Assert.Equal("success", doc.RootElement.GetProperty("status").GetString());
    }

    [Fact]
    public void ToolResultPayload_RealRefIsUsableByResolutionPolicy()
    {
        // End-to-end: the ref the payload carries resolves against the body
        // facts the host would collect, without any fuzzy fallback.
        var details = System.Text.Json.JsonSerializer.Serialize(
            new Dictionary<string, object?> { ["newBodyRef"] = "BLOCK(3)" });
        var result = NXAI.Assistant.Core.CreateBlockExecutionResult.Success(
            10, 10, 10, "ok", "part", details);
        using var doc = System.Text.Json.JsonDocument.Parse(result.DetailsJson!);
        string realRef = doc.RootElement.GetProperty("newBodyRef").GetString()!;

        var bodies = new[] { new BodyRefFact("BLOCK(3)", null), new BodyRefFact("CYLINDER(4)", null) };
        var decision = BodyRefResolutionPolicy.Resolve(realRef, bodies);
        Assert.True(decision.Success);
        Assert.Equal("BLOCK(3)", decision.MatchedJournalIdentifier);
    }
}
