using NXAI.Assistant.Core;
using NXAI.Assistant.UI;
using Xunit;

namespace NXAI.Assistant.Tests;

/// <summary>
/// Phase 10 create_mirror offline tests: parsing/validation of the two
/// modes, strict unknown-field rejection, registration/allowlist/schema
/// (NO object-reference fields — the R2 lesson), the postcondition verdict
/// sub-conditions, the confirmation-description coverage (the T1 lesson:
/// every new kind needs a DescribePendingOperation case), and the tool
/// count bump. The NX builder wiring (MirrorBodyBuilder/MirrorFeatureBuilder
/// with the role-separated plane) is a separate live acceptance item.
/// </summary>
public class MirrorToolTests
{
    private static CreateMirrorParameters? Parse(string json, out string? error) =>
        new CreateMirrorRequestParser().ParseParameters(json, out error);

    private static ToolConfirmationRequest MirrorRequest(string json)
    {
        var p = Parse(json, out var error);
        Assert.Null(error);
        return ToolConfirmationRequest.ForMirror(
            "call_m", MirrorToolContract.Name, p!);
    }

    // ---------- happy paths ----------

    [Fact]
    public void GeometryMode_Parses()
    {
        var p = Parse("""{"mode":"geometry"}""", out var error);
        Assert.Null(error);
        Assert.Equal(MirrorMode.Geometry, p!.Mode);
        Assert.True(p.KeepOriginal);
    }

    [Fact]
    public void FeatureMode_KeepOriginalFalse_Parses()
    {
        var p = Parse("""{"mode":"feature","keep_original":false}""", out var error);
        Assert.Null(error);
        Assert.Equal(MirrorMode.Feature, p!.Mode);
        Assert.False(p.KeepOriginal);
    }

    // ---------- rejections ----------

    [Theory]
    [InlineData("""{}""", "mode")]
    [InlineData("""{"mode":"body"}""", "不支持的 mode")]
    [InlineData("""{"mode":"feature","keep_original":"yes"}""", "keep_original")]
    [InlineData("""{"mode":"geometry","target_body_ref":"BLOCK(5)"}""", "不支持参数字段")]
    [InlineData("""{"mode":"geometry","plane_ref":"DATUM(2)"}""", "不支持参数字段")]
    [InlineData("""{"mode":"feature","seeds":["a"]}""", "不支持参数字段")]
    public void Rejections(string json, string expectedFragment)
    {
        var p = Parse(json, out var error);
        Assert.Null(p);
        Assert.NotNull(error);
        Assert.Contains(expectedFragment, error!);
    }

    // ---------- contract surface ----------

    [Fact]
    public void Registry_IncludesMirror()
    {
        var registry = CreateBlockTool.CreateRegistry();
        Assert.True(registry.IsRegistered(MirrorToolContract.Name));
    }

    [Fact]
    public void Allowlist_IncludesMirror()
    {
        Assert.True(AutonomousToolAllowlist.Contains(MirrorToolContract.Name));
    }

    [Fact]
    public void Schema_HasNoObjectReferenceFields()
    {
        var schema = MirrorToolContract.ParametersJsonSchema;
        Assert.DoesNotContain("target", schema);
        Assert.DoesNotContain("plane_ref", schema);
        Assert.DoesNotContain("seed", schema);
        using var doc = System.Text.Json.JsonDocument.Parse(schema);
        Assert.True(doc.RootElement.TryGetProperty("required", out _));
    }

    // ---------- confirmation descriptions never NRE (the T1 lesson) ----------

    [Fact]
    public void DescribePendingOperation_MirrorRequests_DoNotThrow()
    {
        foreach (var mode in new[] { "geometry", "feature" })
        {
            var request = MirrorRequest($$"""{"mode":"{{mode}}"}""");
            var text = ChatSessionController.DescribePendingOperation(request);
            Assert.Contains("镜像", text);
            Assert.DoesNotContain("布尔", text); // never the boolean wildcard
        }
    }

    // ---------- postcondition verdict ----------

    [Fact]
    public void Verdict_GeometryMode_BodiesGrowBySeedCount()
    {
        var (pass, reason) = NxHostVerdicts.MirrorPostcondition(
            committed: true, alive: true, typeMatches: true,
            featureBodyCount: 2, seedCount: 2,
            bodiesBefore: 1, bodiesAfter: 3, expectedNewBodies: 2,
            isGeometryMode: true);
        Assert.True(pass, reason);
    }

    [Fact]
    public void Verdict_GeometryMode_WrongGrowth_Fails()
    {
        var (pass, reason) = NxHostVerdicts.MirrorPostcondition(
            committed: true, alive: true, typeMatches: true,
            featureBodyCount: 2, seedCount: 2,
            bodiesBefore: 1, bodiesAfter: 2, expectedNewBodies: 2,
            isGeometryMode: true);
        Assert.False(pass);
        Assert.Contains("实体数量变化", reason);
    }

    [Fact]
    public void Verdict_GeometryMode_NoOwnedBodies_Fails()
    {
        var (pass, reason) = NxHostVerdicts.MirrorPostcondition(
            committed: true, alive: true, typeMatches: true,
            featureBodyCount: 0, seedCount: 2,
            bodiesBefore: 1, bodiesAfter: 1, expectedNewBodies: 2,
            isGeometryMode: true);
        Assert.False(pass);
        Assert.Contains("不拥有任何实体", reason);
    }

    [Fact]
    public void Verdict_FeatureMode_DoesNotAssertBodyGrowth()
    {
        // A mirrored hole feature subtracts inside an existing body —
        // body count may legitimately stay the same.
        var (pass, reason) = NxHostVerdicts.MirrorPostcondition(
            committed: true, alive: true, typeMatches: true,
            featureBodyCount: -1, seedCount: 1,
            bodiesBefore: 1, bodiesAfter: 1, expectedNewBodies: 0,
            isGeometryMode: false);
        Assert.True(pass, reason);
    }

    [Fact]
    public void Verdict_TypeMismatch_Fails()
    {
        var (pass, reason) = NxHostVerdicts.MirrorPostcondition(
            committed: true, alive: true, typeMatches: false,
            featureBodyCount: 2, seedCount: 2,
            bodiesBefore: 1, bodiesAfter: 3, expectedNewBodies: 2,
            isGeometryMode: true);
        Assert.False(pass);
        Assert.Contains("不匹配", reason);
    }

    [Fact]
    public void Verdict_DeadFeature_Fails()
    {
        var (pass, _) = NxHostVerdicts.MirrorPostcondition(
            committed: true, alive: false, typeMatches: true,
            featureBodyCount: 2, seedCount: 2,
            bodiesBefore: 1, bodiesAfter: 3, expectedNewBodies: 2,
            isGeometryMode: true);
        Assert.False(pass);
    }

    [Fact]
    public void Verdict_NotCommitted_Fails()
    {
        var (pass, reason) = NxHostVerdicts.MirrorPostcondition(
            committed: false, alive: false, typeMatches: false,
            featureBodyCount: -1, seedCount: 1,
            bodiesBefore: 1, bodiesAfter: 1, expectedNewBodies: 1,
            isGeometryMode: true);
        Assert.False(pass);
        Assert.Contains("CommitFeature", reason);
    }
}
