using NXAI.Assistant.Core;
using Xunit;

namespace NXAI.Assistant.Tests;

/// <summary>
/// Phase 9.7 generic target re-identification (offline): after a new chat
/// clears the remembered refs, the empty-selection decisions still relocate
/// targets from the LIVE inventory the host gathered from the work part.
/// These pin the DECISION layer; the NX-side inventory collection is a
/// separate live acceptance item.
/// </summary>
public class GenericTargetRelocationTests
{
    private static EdgeTargetResolution.InventoryEntry Body(
        string reff, bool isBox, string? name = null, string subtype = "solid_body") =>
        new(reff, Kind: "body", RefType: "Body",
            Name: name, Subtype: subtype, FaceCount: null, IsBox: isBox);

    private static EdgeTargetResolution.InventoryEntry Feature(
        string reff, string refType, string? name = null) =>
        new(reff, Kind: "feature", RefType: refType,
            Name: name, Subtype: null, FaceCount: null, IsBox: false);

    // ---------- body relocation (fillet/chamfer 'perimeter') ----------

    [Fact]
    public void Body_SingleBody_NoRememberedRef_Resolves()
    {
        var d = EdgeTargetResolution.ResolveBodyFromInventory(
            new[] { Body("BLOCK(5)", isBox: false) });
        Assert.True(d.Success);
        Assert.Contains("BLOCK(5)", d.Reason);
    }

    [Fact]
    public void Body_TwoBodies_UniqueBox_ResolvesToIt()
    {
        // The Phase-9.7 scene: two bodies in the part, the box is the one
        // the user says "长方体周边" about — uniquely identifiable by
        // verified geometry, no chat state needed.
        var d = EdgeTargetResolution.ResolveBodyFromInventory(
            new[] { Body("BLOCK(5)", isBox: true), Body("CYL(9)", isBox: false) });
        Assert.True(d.Success);
        Assert.Contains("BLOCK(5)", d.Reason);
    }

    [Fact]
    public void Body_TwoBoxes_Refuses_Ambiguous()
    {
        var d = EdgeTargetResolution.ResolveBodyFromInventory(
            new[] { Body("BLOCK(5)", isBox: true), Body("BLOCK2(7)", isBox: true) });
        Assert.False(d.Success);
        Assert.Contains("请", d.Reason);
    }

    [Fact]
    public void Body_MultipleNoneBox_Refuses()
    {
        var d = EdgeTargetResolution.ResolveBodyFromInventory(
            new[] { Body("CYL(3)", isBox: false), Body("SPH(4)", isBox: false) });
        Assert.False(d.Success);
        Assert.Contains("请", d.Reason);
    }

    [Fact]
    public void Body_EmptyInventory_Refuses()
    {
        var d = EdgeTargetResolution.ResolveBodyFromInventory(
            Array.Empty<EdgeTargetResolution.InventoryEntry>());
        Assert.False(d.Success);
    }

    // ---------- feature relocation (linear-pattern FEATURE mode) ----------

    [Fact]
    public void Feature_SingleTypedFeature_NoRememberedRef_Resolves()
    {
        // The core Problem-A scene: chat reset cleared
        // LastCreatedFeatureRef, but the part still has the hole created
        // earlier — re-identified from the feature tree alone.
        var d = EdgeTargetResolution.ResolveFeatureFromInventory(
            new[] { Feature("HOLE(12)", "Hole") }, "Hole", null);
        Assert.True(d.Success);
        Assert.Contains("HOLE(12)", d.Reason);
    }

    [Fact]
    public void Feature_OtherTypesPresent_TypedCountAloneDecides()
    {
        var d = EdgeTargetResolution.ResolveFeatureFromInventory(
            new[]
            {
                Feature("BLOCK(5)", "Block"),
                Feature("HOLE(12)", "Hole"),
                Feature("BLEND(7)", "EdgeBlend"),
            }, "Hole", null);
        Assert.True(d.Success);
        Assert.Contains("HOLE(12)", d.Reason);
    }

    [Fact]
    public void Feature_TwoHoles_NoHint_Refuses()
    {
        var d = EdgeTargetResolution.ResolveFeatureFromInventory(
            new[] { Feature("HOLE(12)", "Hole"), Feature("HOLE(20)", "Hole") },
            "Hole", null);
        Assert.False(d.Success);
        Assert.Contains("请", d.Reason);
    }

    [Fact]
    public void Feature_TwoHoles_NameHintMatchesOne_Resolves()
    {
        var d = EdgeTargetResolution.ResolveFeatureFromInventory(
            new[] { Feature("HOLE(12)", "Hole", name: "HOLE 1"),
                    Feature("HOLE(20)", "Hole", name: "HOLE 2") },
            "Hole", "HOLE 2");
        Assert.True(d.Success);
        Assert.Contains("HOLE(20)", d.Reason);
    }

    [Fact]
    public void Feature_NoTypedFeatures_RefusesWithCreationAdvice()
    {
        var d = EdgeTargetResolution.ResolveFeatureFromInventory(
            new[] { Feature("BLOCK(5)", "Block") }, "Hole", null);
        Assert.False(d.Success);
        Assert.Contains("请先创建", d.Reason);
    }

    [Fact]
    public void Feature_EmptyInventory_Refuses()
    {
        var d = EdgeTargetResolution.ResolveFeatureFromInventory(
            Array.Empty<EdgeTargetResolution.InventoryEntry>(), "Hole", null);
        Assert.False(d.Success);
    }
}
