using NXAI.Assistant.Core;
using Xunit;

namespace NXAI.Assistant.Tests;

/// <summary>
/// Phase 10.3 P1: "M6x1.0" (the standard table's own full name for M6,
/// Pitch=1 in the real NX_Thread_Standard.xml) must normalize to M6 and
/// pass the threaded lookup, while a WRONG pitch (M6x1.25) still refuses
/// and non-threaded families refuse pitch annotations entirely. The
/// existing bare names (M6/M8/...) keep working. The real-table pitch
/// check happens at Lookup with the production XML loader — these tests
/// cover the parsing/normalization layer plus Lookup with a synthetic
/// in-memory table.
/// </summary>
public class StandardHoleSizeNormalizationTests
{
    // ---------- parsing layer: NormalizeSize ----------

    [Theory]
    [InlineData("M6", "M6")]
    [InlineData("m6", "M6")]
    [InlineData("M6.0", "M6")]
    [InlineData("M1.60", "M1.6")]
    [InlineData("M6x1.0", "M6")]       // the live P1 input
    [InlineData("M6 x 1.0", "M6")]     // the table's own spelling
    [InlineData("m6x1.0", "M6")]
    [InlineData("M12x1.75", "M12")]
    public void NormalizeSize_SplitsPitchSuffix(string input, string expected)
    {
        Assert.Equal(expected, StandardHoleTable.NormalizeSize(input));
    }

    [Fact]
    public void ExtractRequestedPitch_ReadsSuffix()
    {
        Assert.Equal("1.0", StandardHoleTable.ExtractRequestedPitch("M6x1.0"));
        Assert.Equal("1.0", StandardHoleTable.ExtractRequestedPitch("M6 x 1.0"));
        Assert.Null(StandardHoleTable.ExtractRequestedPitch("M6"));
        Assert.Null(StandardHoleTable.ExtractRequestedPitch("M6x"));      // no value
        Assert.Null(StandardHoleTable.ExtractRequestedPitch("BLOCK"));    // not an M size
    }

    // ---------- lookup layer with a synthetic in-memory table ----------

    private const string ThreadedTable = """
        <Root>
          <ThreadedHole Standard="Metric Coarse" Unit="Metric" Size="M6 x 1.0" RadialEngage="0.75" MajorDiameter="6" MinorDiameter="4.917" TapDrillDia="5" Pitch="1" HoleDepth="12" ThreadDepth="9" HoleTipAngle="118" ThreadForm="M Profile"/>
          <ThreadedHole Standard="Metric Coarse" Unit="Metric" Size="M8 x 1.25" RadialEngage="0.75" MajorDiameter="8" MinorDiameter="6.647" TapDrillDia="6.8" Pitch="1.25" HoleDepth="16" ThreadDepth="12" HoleTipAngle="118" ThreadForm="M Profile"/>
        </Root>
        """;

    private static string LoadTable(StandardHoleFamily family) =>
        family == StandardHoleFamily.Threaded ? ThreadedTable : "<Root></Root>";

    [Fact]
    public void Lookup_M6x1_0_ResolvesToM6Record()
    {
        // The exact live failure input now resolves to the M6 record.
        var record = StandardHoleTable.Resolve(
            StandardHoleFamily.Threaded, "M6x1.0", LoadTable);
        Assert.Equal("6", record.Fields["MajorDiameter"]);
        Assert.Equal("1", record.Fields["Pitch"]);
    }

    [Fact]
    public void Lookup_M6WithSpaces_AlsoResolves()
    {
        var record = StandardHoleTable.Resolve(
            StandardHoleFamily.Threaded, "M6 x 1.0", LoadTable);
        Assert.Equal("6", record.Fields["MajorDiameter"]);
    }

    [Fact]
    public void Lookup_WrongPitch_StillRefuses()
    {
        // M6's standard pitch is 1 — 1.25 must NOT silently map to M6.
        var ex = Assert.Throws<StandardHoleLookupException>(
            () => StandardHoleTable.Resolve(
                StandardHoleFamily.Threaded, "M6x1.25", LoadTable));
        Assert.Contains("与标准不符", ex.Message);
    }

    [Fact]
    public void Lookup_BareNames_KeepWorking()
    {
        var m6 = StandardHoleTable.Resolve(StandardHoleFamily.Threaded, "M6", LoadTable);
        Assert.Equal("6", m6.Fields["MajorDiameter"]);
        var m8 = StandardHoleTable.Resolve(StandardHoleFamily.Threaded, "M8", LoadTable);
        Assert.Equal("8", m8.Fields["MajorDiameter"]);
    }

    [Fact]
    public void Lookup_UnsupportedSize_StillRefuses()
    {
        var ex = Assert.Throws<StandardHoleLookupException>(
            () => StandardHoleTable.Resolve(StandardHoleFamily.Threaded, "M7", LoadTable));
        Assert.Contains("不支持的孔规格", ex.Message);
    }

    [Fact]
    public void Lookup_PitchOnClearanceFamily_Refuses()
    {
        // Clearance families have no pitch — the annotation must be refused,
        // never silently normalized.
        var ex = Assert.Throws<StandardHoleLookupException>(
            () => StandardHoleTable.Resolve(
                StandardHoleFamily.SimpleClearance, "M6x1.0", LoadTable));
        Assert.Contains("不使用螺距", ex.Message);
    }

    // ---------- depth semantics untouched ----------
    // The fix only splits the NAME: HoleDepth/ThreadDepth fields flow from
    // the SAME record row as before (M6 → HoleDepth=12, ThreadDepth=9 in
    // the real table). Pinned here via the synthetic table's fields so a
    // name/pitch fix can never silently alter depth sourcing.

    [Fact]
    public void Lookup_M6x1_0_DepthFieldsFromSameRecord()
    {
        var record = StandardHoleTable.Resolve(
            StandardHoleFamily.Threaded, "M6x1.0", LoadTable);
        Assert.Equal("12", record.Fields["HoleDepth"]);
        Assert.Equal("9", record.Fields["ThreadDepth"]);
    }
}
