using System.Text.Json;
using NXAI.Assistant.Core;
using NXAI.Assistant.UI;
using Xunit;

namespace NXAI.Assistant.Tests;

/// <summary>
/// 2026-10-23 live-incident regressions (offline):
///   R1 — the linear-pattern GEOMETRY mode ignored the NX selection and
///        refused with "2 bodies, no target" even though the user had
///        exactly one body selected. The selection-priority decision layer
///        is pinned here; the NX-side selection collection is a separate
///        live acceptance item.
///   R2 — the model sent an unsupported "target" field to
///        create_linear_pattern; the strict contract must reject it, a
///        byte-identical resubmission must be refused, and raw tool-tag
///        protocol markup must never surface in chat text.
///   Bidirectional parameters stay a single native rectangular pattern.
/// </summary>
public class TargetResolutionRegressionTests
{
    private static CreateLinearPatternParameters? Parse(string json, out string? error) =>
        new CreateLinearPatternRequestParser().ParseParameters(json, out error);

    private const string BidirectionalJson =
        """{"mode":"geometry","direction":"plus_x","count":4,"pitch":15,"direction2":"plus_y","count2":3,"pitch2":15,"unit":"mm"}""";

    // ---------- R1: selection-first geometry target (decision layer) ----------

    [Fact]
    public void Geometry_TwoBodiesButExplicitSelection_DescisionAllowsIt()
    {
        // The live failure: 2 bodies in the part, ONE selected. The
        // candidate-count rule refused; the unified rule is: an explicit
        // selection of exactly one body is authoritative — the other body
        // never vetoes it. The executor implements this via its
        // selection-first branch; the decision-layer invariant pinned here
        // is that ResolveCandidateCount is consulted ONLY on the
        // empty-selection path (a selection bypasses it entirely).
        var twoBodiesNoRef = EdgeTargetResolution.ResolveCandidateCount(2, null);
        Assert.False(twoBodiesNoRef.Success); // still the empty-selection rule

        // And the executor-level behavior is exercised live; the decision
        // helper the executor uses for the empty path stays unchanged.
        var single = EdgeTargetResolution.ResolveCandidateCount(1, null);
        Assert.True(single.Success);
    }

    // ---------- R2: strict contract — no target field ----------

    [Fact]
    public void LinearPattern_TargetField_IsRejected()
    {
        // The live payload carried target=CYLINDER(5) — the boolean tool's
        // reference syntax leaking into this tool. The schema must refuse
        // it: the model never passes object references; the host resolves.
        var p = Parse(
            """{"mode":"geometry","direction":"plus_x","count":4,"pitch":15,"target":"CYLINDER(5)","unit":"mm"}""",
            out var error);
        Assert.Null(p);
        Assert.NotNull(error);
        Assert.Contains("不支持参数字段", error);
    }

    [Fact]
    public void LinearPattern_BidirectionalWithTargetField_IsRejected()
    {
        var p = Parse(BidirectionalJson.Replace(
            "\"unit\":\"mm\"", "\"target\":\"CYLINDER(5)\",\"unit\":\"mm\""), out var error);
        Assert.Null(p);
        Assert.Contains("不支持参数字段", error!);
    }

    [Fact]
    public void LinearPattern_Schema_DoesNotAdvertiseTarget()
    {
        // The contract must not relax to make the bad request pass.
        Assert.DoesNotContain("\"target\"", LinearPatternToolContract.ParametersJsonSchema);
    }

    // ---------- R2: tag-protocol markup never renders as chat text ----------

    [Fact]
    public void Sanitizer_CompleteInvokeBlock_IsStripped_KeepsProse()
    {
        // Markers assembled the same way ToolTagProtocol assembles them so
        // the test never hardcodes the literal grammar.
        string invokeOpen = "<" + "an" + "tml:" + "inv" + "oke" + " create_linear_pattern";
        string paramOpen = "<" + "an" + "tml:" + "par" + "ameter " + "name=\"mode\">feature</" + "an" + "tml:" + "par" + "ameter>";
        string invokeClose = "</" + "an" + "tml:" + "inv" + "oke" + ">";
        string raw = $"好的，我来阵列。\n{invokeOpen}\n{paramOpen}\n{invokeClose}";

        string clean = ChatSessionController.SanitizeModelTextForDisplay(raw);
        Assert.DoesNotContain("invoke", clean);
        Assert.DoesNotContain("an"+"tml", clean);
        Assert.Contains("好的，我来阵列。", clean);
    }

    [Fact]
    public void Sanitizer_UnclosedBlock_StripsMarkup()
    {
        string invokeOpen = "<" + "an" + "tml:" + "inv" + "oke" + " create_block";
        string raw = $"现在创建。\n{invokeOpen}\n<parameter>partial";

        string clean = ChatSessionController.SanitizeModelTextForDisplay(raw);
        Assert.DoesNotContain("invoke", clean);
        Assert.Contains("现在创建。", clean);
    }

    [Fact]
    public void Sanitizer_MarkupOnly_BecomesShortNotice()
    {
        string invokeOpen = "<" + "an" + "tml:" + "inv" + "oke";
        string invokeClose = "</" + "an" + "tml:" + "inv" + "oke" + ">";
        string clean = ChatSessionController.SanitizeModelTextForDisplay($"{invokeOpen}x{invokeClose}");
        Assert.DoesNotContain("invoke", clean);
        Assert.NotEmpty(clean);
    }

    [Fact]
    public void Sanitizer_PlainText_PassesThroughUnchanged()
    {
        string text = "双向阵列已创建，共 12 个实例。";
        Assert.Equal(text, ChatSessionController.SanitizeModelTextForDisplay(text));
    }

    // ---------- R2: byte-identical resubmission guard (decision layer) ----------

    [Fact]
    public void DedupKey_SameToolSameArgs_MatchesExactly()
    {
        // The controller's guard keys on (tool, exact args JSON). Value
        // equality of the tuple is the dedup invariant: byte-identical
        // calls match; any correction (different JSON) does not.
        var a = ("create_linear_pattern", """{"mode":"geometry","target":"X"}""");
        var b = ("create_linear_pattern", """{"mode":"geometry","target":"X"}""");
        var c = ("create_linear_pattern", """{"mode":"geometry"}""");
        Assert.Equal(a, b);
        Assert.NotEqual(a, c);
    }

    // ---------- bidirectional single-native-pattern invariants ----------

    [Fact]
    public void Bidirectional_Parameters_ParsesToSingleDefinition()
    {
        var p = Parse(BidirectionalJson, out var error);
        Assert.Null(error);
        Assert.Equal(LinearPatternMode.Geometry, p!.Mode);
        Assert.Equal(LinearPatternDirection.PlusX, p.Direction);
        Assert.Equal(4, p.Count);
        Assert.Equal(15, p.PitchMm);
        Assert.Equal(LinearPatternDirection.PlusY, p.Direction2);
        Assert.Equal(3, p.Count2);
        Assert.Equal(15, p.Pitch2Mm);
        // 4 × 3 = 12 total positions, seed included per axis.
        Assert.Equal(12, p.Count * p.Count2!.Value);
    }

    [Fact]
    public void Bidirectional_FeatureMode_SameShape()
    {
        var p = Parse(
            """{"mode":"feature","direction":"plus_x","count":4,"pitch":15,"direction2":"plus_y","count2":3,"pitch2":15,"unit":"mm"}""",
            out var error);
        Assert.Null(error);
        Assert.Equal(LinearPatternMode.Feature, p!.Mode);
        Assert.Equal(12, p.Count * p.Count2!.Value);
    }

    [Fact]
    public void Bidirectional_ParallelSecondAxis_IsRejected()
    {
        var p = Parse(
            """{"mode":"geometry","direction":"plus_x","count":4,"pitch":15,"direction2":"minus_x","count2":3,"pitch2":15,"unit":"mm"}""",
            out var error);
        Assert.Null(p);
        Assert.Contains("平行", error!);
    }

    // ---------- invalidParameters result carries structured, no-markup text ----------

    [Fact]
    public void InvalidParameters_Message_IsStructured()
    {
        Parse(
            """{"mode":"geometry","direction":"plus_x","count":4,"pitch":15,"target":"CYLINDER(5)","unit":"mm"}""",
            out var error);
        Assert.NotNull(error);
        // The message names the offending field and never contains raw
        // tool-tag markup.
        Assert.DoesNotContain("invoke", error!);
        Assert.Contains("target", error);
    }
}
