using NXAI.Assistant.Core;
using Xunit;

namespace NXAI.Assistant.Tests;

/// <summary>
/// Phase 9.2 standard-hole table tests (offline): the 36 family×size
/// combinations resolved from a fixture XML cloned from the real NX2512
/// modeling_standards files (ISO Close H12 rows; threaded Metric Coarse +
/// RadialEngage 0.75). Table-missing/ambiguous/unsupported cases are
/// exercised with mutated fixtures. No NX, no file system.
/// </summary>
public class StandardHoleTableTests
{
    // ---------- fixture tables (cloned field-for-field from NX2512 XML) ----------

    private const string ThreadXml = """
        <Root>
          <ThreadedHole Standard="Metric Coarse" Unit="Metric" Size="M12 x 1.75" RadialEngage="0.75" MajorDiameter="12" TapDrillDia="10.3" Pitch="1.75" HoleDepth="24" ThreadDepth="18" HoleTipAngle="118" StartChamferDiameter="12" StartChamferAngle="45" EndChamferDiameter="12" EndChamferAngle="45" ReliefDiameter="12" ReliefDepth="6" ReliefTipAngle="118" ReliefChamferAngle="45" ReliefChamferOffset="0.9" ThreadForm="M Profile"/>
          <ThreadedHole Standard="Metric Coarse" Unit="Metric" Size="M10 x 1.5" RadialEngage="0.75" MajorDiameter="10" TapDrillDia="8.5" Pitch="1.5" HoleDepth="20" ThreadDepth="15" HoleTipAngle="118" StartChamferDiameter="10" StartChamferAngle="45" EndChamferDiameter="10" EndChamferAngle="45" ReliefDiameter="10" ReliefDepth="5" ReliefTipAngle="118" ReliefChamferAngle="45" ReliefChamferOffset="0.6" ThreadForm="M Profile"/>
          <ThreadedHole Standard="Metric Coarse" Unit="Metric" Size="M8 x 1.25" RadialEngage="0.75" MajorDiameter="8" TapDrillDia="6.8" Pitch="1.25" HoleDepth="16" ThreadDepth="12" HoleTipAngle="118" StartChamferDiameter="8" StartChamferAngle="45" EndChamferDiameter="8" EndChamferAngle="45" ReliefDiameter="8" ReliefDepth="4" ReliefTipAngle="118" ReliefChamferAngle="45" ReliefChamferOffset="0.6" ThreadForm="M Profile"/>
          <ThreadedHole Standard="Metric Coarse" Unit="Metric" Size="M6 x 1.0" RadialEngage="0.75" MajorDiameter="6" TapDrillDia="5" Pitch="1" HoleDepth="12" ThreadDepth="9" HoleTipAngle="118" StartChamferDiameter="6" StartChamferAngle="45" EndChamferDiameter="6" EndChamferAngle="45" ReliefDiameter="6" ReliefDepth="3" ReliefTipAngle="118" ReliefChamferAngle="45" ReliefChamferOffset="0.4" ThreadForm="M Profile"/>
          <ThreadedHole Standard="Metric Coarse" Unit="Metric" Size="M5 x 0.8" RadialEngage="0.75" MajorDiameter="5" TapDrillDia="4.2" Pitch="0.8" HoleDepth="10" ThreadDepth="7.5" HoleTipAngle="118" StartChamferDiameter="5" StartChamferAngle="45" EndChamferDiameter="5" EndChamferAngle="45" ReliefDiameter="5" ReliefDepth="2.5" ReliefTipAngle="118" ReliefChamferAngle="45" ReliefChamferOffset="0.4" ThreadForm="M Profile"/>
          <ThreadedHole Standard="Metric Coarse" Unit="Metric" Size="M4 x 0.7" RadialEngage="0.75" MajorDiameter="4" TapDrillDia="3.3" Pitch="0.7" HoleDepth="8" ThreadDepth="6" HoleTipAngle="118" StartChamferDiameter="4" StartChamferAngle="45" EndChamferDiameter="4" EndChamferAngle="45" ReliefDiameter="4" ReliefDepth="2" ReliefTipAngle="118" ReliefChamferAngle="45" ReliefChamferOffset="0.4" ThreadForm="M Profile"/>
          <ThreadedHole Standard="Metric Coarse" Unit="Metric" Size="M3 x 0.5" RadialEngage="0.75" MajorDiameter="3" TapDrillDia="2.5" Pitch="0.5" HoleDepth="6" ThreadDepth="4.5" HoleTipAngle="118" StartChamferDiameter="3" StartChamferAngle="45" EndChamferDiameter="3" EndChamferAngle="45" ReliefDiameter="3" ReliefDepth="1.5" ReliefTipAngle="118" ReliefChamferAngle="45" ReliefChamferOffset="0.3" ThreadForm="M Profile"/>
          <ThreadedHole Standard="Metric Coarse" Unit="Metric" Size="M2 x 0.4" RadialEngage="0.75" MajorDiameter="2" TapDrillDia="1.6" Pitch="0.4" HoleDepth="4" ThreadDepth="3" HoleTipAngle="118" StartChamferDiameter="2" StartChamferAngle="45" EndChamferDiameter="2" EndChamferAngle="45" ReliefDiameter="2" ReliefDepth="1" ReliefTipAngle="118" ReliefChamferAngle="45" ReliefChamferOffset="0.3" ThreadForm="M Profile"/>
          <ThreadedHole Standard="Metric Coarse" Unit="Metric" Size="M1.6 x 0.35" RadialEngage="0.75" MajorDiameter="1.6" TapDrillDia="1.25" Pitch="0.35" HoleDepth="3.2" ThreadDepth="2.4" HoleTipAngle="118" StartChamferDiameter="1.6" StartChamferAngle="45" EndChamferDiameter="1.6" EndChamferAngle="45" ReliefDiameter="1.6" ReliefDepth="0.8" ReliefTipAngle="118" ReliefChamferAngle="45" ReliefChamferOffset="0.2" ThreadForm="M Profile"/>
          <ThreadedHole Standard="Metric Fine" Unit="Metric" Size="M6 x 0.75" RadialEngage="0.75" MajorDiameter="6" TapDrillDia="5.25" Pitch="0.75" HoleDepth="12" ThreadDepth="9" HoleTipAngle="118" StartChamferDiameter="6" StartChamferAngle="45" EndChamferDiameter="6" EndChamferAngle="45" ReliefDiameter="6" ReliefDepth="3" ReliefTipAngle="118" ReliefChamferAngle="45" ReliefChamferOffset="0.4" ThreadForm="M Profile"/>
        </Root>
        """;

    private const string ClearanceXml = """
        <Root>
          <StandardHole Unit="Metric" Standard="ISO" Form="Simple" ScrewType="General Screw Clearance" Size="M6" Fit="Close (H12)" ThreadStd="Metric Coarse" ThreadSize="M6 x 1.0" ThreadEngage="0.75" HoleDiameter="6.4" HoleTipAngle="118" StartChamferOffset="0.4" StartChamferAngle="45" EndChamferOffset="0.4" EndChamferAngle="45" />
          <StandardHole Unit="Metric" Standard="ISO" Form="Simple" ScrewType="General Screw Clearance" Size="M6" Fit="Normal (H13)" ThreadStd="Metric Coarse" ThreadSize="M6 x 1.0" ThreadEngage="0.75" HoleDiameter="6.6" HoleTipAngle="118" StartChamferOffset="0.4" StartChamferAngle="45" EndChamferOffset="0.4" EndChamferAngle="45" />
          <StandardHole Unit="Metric" Standard="ISO" Form="Simple" ScrewType="General Screw Clearance" Size="M12" Fit="Close (H12)" ThreadStd="Metric Coarse" ThreadSize="M12 x 1.75" ThreadEngage="0.75" HoleDiameter="13" HoleTipAngle="118" StartChamferOffset="1.1" StartChamferAngle="45" EndChamferOffset="1.1" EndChamferAngle="45" />
          <StandardHole Unit="Metric" Standard="ISO" Form="Simple" ScrewType="General Screw Clearance" Size="M10" Fit="Close (H12)" ThreadStd="Metric Coarse" ThreadSize="M10 x 1.5" ThreadEngage="0.75" HoleDiameter="10.5" HoleTipAngle="118" StartChamferOffset="0.6" StartChamferAngle="45" EndChamferOffset="0.6" EndChamferAngle="45" />
          <StandardHole Unit="Metric" Standard="ISO" Form="Simple" ScrewType="General Screw Clearance" Size="M8" Fit="Close (H12)" ThreadStd="Metric Coarse" ThreadSize="M8 x 1.25" ThreadEngage="0.75" HoleDiameter="8.4" HoleTipAngle="118" StartChamferOffset="0.6" StartChamferAngle="45" EndChamferOffset="0.6" EndChamferAngle="45" />
          <StandardHole Unit="Metric" Standard="ISO" Form="Simple" ScrewType="General Screw Clearance" Size="M5" Fit="Close (H12)" ThreadStd="Metric Coarse" ThreadSize="M5 x 0.8" ThreadEngage="0.75" HoleDiameter="5.3" HoleTipAngle="118" StartChamferOffset="0.4" StartChamferAngle="45" EndChamferOffset="0.4" EndChamferAngle="45" />
          <StandardHole Unit="Metric" Standard="ISO" Form="Simple" ScrewType="General Screw Clearance" Size="M4" Fit="Close (H12)" ThreadStd="Metric Coarse" ThreadSize="M4 x 0.7" ThreadEngage="0.75" HoleDiameter="4.3" HoleTipAngle="118" StartChamferOffset="0.4" StartChamferAngle="45" EndChamferOffset="0.4" EndChamferAngle="45" />
          <StandardHole Unit="Metric" Standard="ISO" Form="Simple" ScrewType="General Screw Clearance" Size="M3" Fit="Close (H12)" ThreadStd="Metric Coarse" ThreadSize="M3 x 0.5" ThreadEngage="0.75" HoleDiameter="3.2" HoleTipAngle="118" StartChamferOffset="0.3" StartChamferAngle="45" EndChamferOffset="0.3" EndChamferAngle="45" />
          <StandardHole Unit="Metric" Standard="ISO" Form="Simple" ScrewType="General Screw Clearance" Size="M2" Fit="Close (H12)" ThreadStd="Metric Coarse" ThreadSize="M2 x 0.4" ThreadEngage="0.75" HoleDiameter="2.2" HoleTipAngle="118" StartChamferOffset="0.3" StartChamferAngle="45" EndChamferOffset="0.3" EndChamferAngle="45" />
          <StandardHole Unit="Metric" Standard="ISO" Form="Simple" ScrewType="General Screw Clearance" Size="M1.6" Fit="Close (H12)" ThreadStd="Metric Coarse" ThreadSize="M1.6 x 0.35" ThreadEngage="0.75" HoleDiameter="1.7" HoleTipAngle="118" StartChamferOffset="0.2" StartChamferAngle="45" EndChamferOffset="0.2" EndChamferAngle="45" />
          <StandardHole Unit="Metric" Standard="ISO" Form="Counterbored" ScrewType="Socket Head, 4762" Size="M6" Fit="Close (H12)" ThreadStd="Metric Coarse" ThreadSize="M6 x 1.0" ThreadEngage="0.75" HoleDiameter="6.4" HoleTipAngle="118" CBoreDia="11" CBoreDepth="6.8" NeckChamferOffset="0.4" NeckChamferAngle="60" StartChamferOffset="0.4" StartChamferAngle="45" EndChamferOffset="0.4" EndChamferAngle="45" />
          <StandardHole Unit="Metric" Standard="ISO" Form="Counterbored" ScrewType="Socket Head, 4762" Size="M12" Fit="Close (H12)" ThreadStd="Metric Coarse" ThreadSize="M12 x 1.75" ThreadEngage="0.75" HoleDiameter="13" HoleTipAngle="118" CBoreDia="20" CBoreDepth="12.8" NeckChamferOffset="1.1" NeckChamferAngle="60" StartChamferOffset="1.1" StartChamferAngle="45" EndChamferOffset="1.1" EndChamferAngle="45" />
          <StandardHole Unit="Metric" Standard="ISO" Form="Counterbored" ScrewType="Socket Head, 4762" Size="M10" Fit="Close (H12)" ThreadStd="Metric Coarse" ThreadSize="M10 x 1.5" ThreadEngage="0.75" HoleDiameter="10.5" HoleTipAngle="118" CBoreDia="18" CBoreDepth="10.8" NeckChamferOffset="0.6" NeckChamferAngle="60" StartChamferOffset="0.6" StartChamferAngle="45" EndChamferOffset="0.6" EndChamferAngle="45" />
          <StandardHole Unit="Metric" Standard="ISO" Form="Counterbored" ScrewType="Socket Head, 4762" Size="M8" Fit="Close (H12)" ThreadStd="Metric Coarse" ThreadSize="M8 x 1.25" ThreadEngage="0.75" HoleDiameter="8.4" HoleTipAngle="118" CBoreDia="15" CBoreDepth="8.8" NeckChamferOffset="0.6" NeckChamferAngle="60" StartChamferOffset="0.6" StartChamferAngle="45" EndChamferOffset="0.6" EndChamferAngle="45" />
          <StandardHole Unit="Metric" Standard="ISO" Form="Counterbored" ScrewType="Socket Head, 4762" Size="M5" Fit="Close (H12)" ThreadStd="Metric Coarse" ThreadSize="M5 x 0.8" ThreadEngage="0.75" HoleDiameter="5.3" HoleTipAngle="118" CBoreDia="10" CBoreDepth="5.8" NeckChamferOffset="0.4" NeckChamferAngle="60" StartChamferOffset="0.4" StartChamferAngle="45" EndChamferOffset="0.4" EndChamferAngle="45" />
          <StandardHole Unit="Metric" Standard="ISO" Form="Counterbored" ScrewType="Socket Head, 4762" Size="M4" Fit="Close (H12)" ThreadStd="Metric Coarse" ThreadSize="M4 x 0.7" ThreadEngage="0.75" HoleDiameter="4.3" HoleTipAngle="118" CBoreDia="8" CBoreDepth="4.8" NeckChamferOffset="0.4" NeckChamferAngle="60" StartChamferOffset="0.4" StartChamferAngle="45" EndChamferOffset="0.4" EndChamferAngle="45" />
          <StandardHole Unit="Metric" Standard="ISO" Form="Counterbored" ScrewType="Socket Head, 4762" Size="M3" Fit="Close (H12)" ThreadStd="Metric Coarse" ThreadSize="M3 x 0.5" ThreadEngage="0.75" HoleDiameter="3.2" HoleTipAngle="118" CBoreDia="6.5" CBoreDepth="3.5" NeckChamferOffset="0.3" NeckChamferAngle="60" StartChamferOffset="0.3" StartChamferAngle="45" EndChamferOffset="0.3" EndChamferAngle="45" />
          <StandardHole Unit="Metric" Standard="ISO" Form="Counterbored" ScrewType="Socket Head, 4762" Size="M2" Fit="Close (H12)" ThreadStd="Metric Coarse" ThreadSize="M2 x 0.4" ThreadEngage="0.75" HoleDiameter="2.2" HoleTipAngle="118" CBoreDia="4.4" CBoreDepth="2.2" NeckChamferOffset="0.3" NeckChamferAngle="60" StartChamferOffset="0.3" StartChamferAngle="45" EndChamferOffset="0.3" EndChamferAngle="45" />
          <StandardHole Unit="Metric" Standard="ISO" Form="Counterbored" ScrewType="Socket Head, 4762" Size="M1.6" Fit="Close (H12)" ThreadStd="Metric Coarse" ThreadSize="M1.6 x 0.35" ThreadEngage="0.75" HoleDiameter="1.7" HoleTipAngle="118" CBoreDia="3.5" CBoreDepth="1.8" NeckChamferOffset="0.2" NeckChamferAngle="60" StartChamferOffset="0.2" StartChamferAngle="45" EndChamferOffset="0.2" EndChamferAngle="45" />
          <StandardHole Unit="Metric" Standard="ISO" Form="Countersunk" ScrewType="Socket Flat Head, 10642" Size="M6" Fit="Close (H12)" ThreadStd="Metric Coarse" ThreadSize="M6 x 1.0" ThreadEngage="0.75" HoleDiameter="6.4" HoleTipAngle="118" CSinkDia="13.71" CSinkAngle="90" CSinkReliefDepth="0.8" StartChamferOffset="0.4" StartChamferAngle="45" EndChamferOffset="0.4" EndChamferAngle="45" />
          <StandardHole Unit="Metric" Standard="ISO" Form="Countersunk" ScrewType="Socket Flat Head, 10642" Size="M3" Fit="Close (H12)" ThreadStd="Metric Coarse" ThreadSize="M3 x 0.5" ThreadEngage="0.75" HoleDiameter="3.2" HoleTipAngle="118" CSinkDia="6.94" CSinkAngle="90" CSinkReliefDepth="0.4" StartChamferOffset="0.3" StartChamferAngle="45" EndChamferOffset="0.3" EndChamferAngle="45" />
          <StandardHole Unit="Metric" Standard="ISO" Form="Countersunk" ScrewType="Socket Flat Head, 10642" Size="M12" Fit="Close (H12)" ThreadStd="Metric Coarse" ThreadSize="M12 x 1.75" ThreadEngage="0.75" HoleDiameter="13" HoleTipAngle="118" CSinkDia="27.21" CSinkAngle="90" CSinkReliefDepth="1.6" StartChamferOffset="1.1" StartChamferAngle="45" EndChamferOffset="1.1" EndChamferAngle="45" />
          <StandardHole Unit="Metric" Standard="ISO" Form="Countersunk" ScrewType="Socket Flat Head, 10642" Size="M10" Fit="Close (H12)" ThreadStd="Metric Coarse" ThreadSize="M10 x 1.5" ThreadEngage="0.75" HoleDiameter="10.5" HoleTipAngle="118" CSinkDia="22.73" CSinkAngle="90" CSinkReliefDepth="1.3" StartChamferOffset="0.6" StartChamferAngle="45" EndChamferOffset="0.6" EndChamferAngle="45" />
          <StandardHole Unit="Metric" Standard="ISO" Form="Countersunk" ScrewType="Socket Flat Head, 10642" Size="M8" Fit="Close (H12)" ThreadStd="Metric Coarse" ThreadSize="M8 x 1.25" ThreadEngage="0.75" HoleDiameter="8.4" HoleTipAngle="118" CSinkDia="18.25" CSinkAngle="90" CSinkReliefDepth="1.1" StartChamferOffset="0.6" StartChamferAngle="45" EndChamferOffset="0.6" EndChamferAngle="45" />
          <StandardHole Unit="Metric" Standard="ISO" Form="Countersunk" ScrewType="Socket Flat Head, 10642" Size="M5" Fit="Close (H12)" ThreadStd="Metric Coarse" ThreadSize="M5 x 0.8" ThreadEngage="0.75" HoleDiameter="5.3" HoleTipAngle="118" CSinkDia="11.47" CSinkAngle="90" CSinkReliefDepth="0.7" StartChamferOffset="0.4" StartChamferAngle="45" EndChamferOffset="0.4" EndChamferAngle="45" />
          <StandardHole Unit="Metric" Standard="ISO" Form="Countersunk" ScrewType="Socket Flat Head, 10642" Size="M4" Fit="Close (H12)" ThreadStd="Metric Coarse" ThreadSize="M4 x 0.7" ThreadEngage="0.75" HoleDiameter="4.3" HoleTipAngle="118" CSinkDia="9.18" CSinkAngle="90" CSinkReliefDepth="0.5" StartChamferOffset="0.4" StartChamferAngle="45" EndChamferOffset="0.4" EndChamferAngle="45" />
          <StandardHole Unit="Metric" Standard="DIN" Form="Countersunk" ScrewType="Machine, 95, 97" Size="M1.6" Fit="Close (H12)" ThreadStd="Metric Coarse" ThreadSize="M1.6 x 0.35" ThreadEngage="0.75" HoleDiameter="1.8" HoleTipAngle="118" CSinkDia="3.6" CSinkAngle="90" CSinkReliefDepth="0.2" StartChamferOffset="0.2" StartChamferAngle="45" EndChamferOffset="0.2" EndChamferAngle="45" />
        </Root>
        """;

    private static string LoadTable(StandardHoleFamily family) => family switch
    {
        StandardHoleFamily.Threaded => ThreadXml,
        _ => ClearanceXml,
    };

    // ---------- 36-combination matrix ----------

    public static TheoryData<string, string> ThreadedCombos()
    {
        var data = new TheoryData<string, string>();
        foreach (var s in new[] { "M12", "M10", "M8", "M6", "M5", "M4", "M3", "M2", "M1.6" })
            data.Add(s, s);
        return data;
    }

    public static TheoryData<string> AllSizes()
    {
        var data = new TheoryData<string>();
        foreach (var s in StandardHoleTable.SupportedSizes)
            data.Add(s);
        return data;
    }

    [Theory]
    [MemberData(nameof(AllSizes))]
    public void Threaded_AllNineSizes_ResolveUnique(string size)
    {
        var rec = StandardHoleTable.Resolve(StandardHoleFamily.Threaded, size, LoadTable);
        Assert.Equal(size, StandardHoleTable.NormalizeSize(rec.Field("Size")!.Split(' ')[0]));
        Assert.Equal("Metric Coarse", rec.Field("Standard"));
        Assert.Equal("0.75", rec.Field("RadialEngage"));
        Assert.NotNull(rec.Field("TapDrillDia"));
        Assert.NotNull(rec.Field("Pitch"));
        Assert.NotNull(rec.Field("HoleDepth"));
        Assert.NotNull(rec.Field("ThreadDepth"));
    }

    [Theory]
    [MemberData(nameof(AllSizes))]
    public void Simple_AllNineSizes_ResolveUnique(string size)
    {
        var rec = StandardHoleTable.Resolve(StandardHoleFamily.SimpleClearance, size, LoadTable);
        Assert.Equal("Simple", rec.Field("Form"));
        Assert.Equal("General Screw Clearance", rec.Field("ScrewType"));
        Assert.Equal("ISO", rec.Field("Standard"));
        Assert.Equal("Close (H12)", rec.Field("Fit"));
        Assert.NotNull(rec.Field("HoleDiameter"));
        Assert.Equal("118", rec.Field("HoleTipAngle"));
    }

    [Theory]
    [MemberData(nameof(AllSizes))]
    public void Counterbored_AllNineSizes_ResolveUnique(string size)
    {
        var rec = StandardHoleTable.Resolve(StandardHoleFamily.Counterbored, size, LoadTable);
        Assert.Equal("Counterbored", rec.Field("Form"));
        Assert.Equal("Socket Head, 4762", rec.Field("ScrewType"));
        Assert.NotNull(rec.Field("HoleDiameter"));
        Assert.NotNull(rec.Field("CBoreDia"));
        Assert.NotNull(rec.Field("CBoreDepth"));
        Assert.NotNull(rec.Field("NeckChamferAngle"));
    }

    // ---------- Countersunk ISO gap: M2/M1.6 must refuse ----------

    [Theory]
    [InlineData("M2")]
    [InlineData("M1.6")]
    public void Countersunk_M2_M16_NoIsoRecord_Refused(string size)
    {
        var ex = Assert.Throws<StandardHoleLookupException>(() =>
            StandardHoleTable.Resolve(StandardHoleFamily.Countersunk, size, LoadTable));
        Assert.Contains("没有匹配的标准记录", ex.Message);
    }

    [Theory]
    [InlineData("M12")]
    [InlineData("M10")]
    [InlineData("M8")]
    [InlineData("M6")]
    [InlineData("M5")]
    [InlineData("M4")]
    [InlineData("M3")]
    public void Countersunk_M12_to_M3_ResolveUnique(string size)
    {
        var rec = StandardHoleTable.Resolve(StandardHoleFamily.Countersunk, size, LoadTable);
        Assert.Equal("Countersunk", rec.Field("Form"));
        Assert.Equal("Socket Flat Head, 10642", rec.Field("ScrewType"));
        Assert.NotNull(rec.Field("HoleDiameter"));
        Assert.NotNull(rec.Field("CSinkDia"));
        Assert.NotNull(rec.Field("CSinkAngle"));
        Assert.NotNull(rec.Field("CSinkReliefDepth"));
    }

    // ---------- values spot-checked against the real table ----------

    [Fact]
    public void Threaded_M6_ValuesMatchRealTable()
    {
        var rec = StandardHoleTable.Resolve(StandardHoleFamily.Threaded, "M6", LoadTable);
        Assert.Equal("M6 x 1.0", rec.Field("Size"));
        Assert.Equal("5", rec.Field("TapDrillDia"));
        Assert.Equal("1", rec.Field("Pitch"));
        Assert.Equal("12", rec.Field("HoleDepth"));
        Assert.Equal("9", rec.Field("ThreadDepth"));
        Assert.Equal("0.4", rec.Field("ReliefChamferOffset"));
    }

    [Fact]
    public void Threaded_MetricFine_MustNotBeMatched()
    {
        // M6 x 0.75 (Metric Fine) exists in the fixture but must never win —
        // Standard='Metric Coarse' is part of the match key.
        var rec = StandardHoleTable.Resolve(StandardHoleFamily.Threaded, "M6", LoadTable);
        Assert.Equal("M6 x 1.0", rec.Field("Size"));
    }

    [Fact]
    public void Simple_M6_ValuesMatchRealTable()
    {
        var rec = StandardHoleTable.Resolve(StandardHoleFamily.SimpleClearance, "M6", LoadTable);
        Assert.Equal("6.4", rec.Field("HoleDiameter"));
        Assert.Equal("0.4", rec.Field("StartChamferOffset"));
        Assert.Equal("45", rec.Field("StartChamferAngle"));
    }

    [Fact]
    public void Counterbored_M6_ValuesMatchRealTable()
    {
        var rec = StandardHoleTable.Resolve(StandardHoleFamily.Counterbored, "M6", LoadTable);
        Assert.Equal("6.4", rec.Field("HoleDiameter"));
        Assert.Equal("11", rec.Field("CBoreDia"));
        Assert.Equal("6.8", rec.Field("CBoreDepth"));
        Assert.Equal("0.4", rec.Field("NeckChamferOffset"));
        Assert.Equal("60", rec.Field("NeckChamferAngle"));
    }

    [Fact]
    public void Countersunk_M6_ValuesMatchRealTable()
    {
        var rec = StandardHoleTable.Resolve(StandardHoleFamily.Countersunk, "M6", LoadTable);
        Assert.Equal("6.4", rec.Field("HoleDiameter"));
        Assert.Equal("13.71", rec.Field("CSinkDia"));
        Assert.Equal("90", rec.Field("CSinkAngle"));
        Assert.Equal("0.8", rec.Field("CSinkReliefDepth"));
    }

    // ---------- failure modes ----------

    [Fact]
    public void Threaded_ZeroMatches_Refused()
    {
        // The fixture's Fine row is filtered out by Standard; remove the
        // coarse M2 row by using an empty table.
        var ex = Assert.Throws<StandardHoleLookupException>(() =>
            StandardHoleTable.Resolve(StandardHoleFamily.Threaded, "M2", _ => "<Root></Root>"));
        Assert.Contains("没有匹配的标准记录", ex.Message);
    }

    [Fact]
    public void Ambiguous_DuplicateRows_Refused()
    {
        string dup = ThreadXml.Replace(
            "</Root>",
            "  <ThreadedHole Standard=\"Metric Coarse\" Unit=\"Metric\" Size=\"M6 x 1.0 ALT\" RadialEngage=\"0.75\" MajorDiameter=\"6\" TapDrillDia=\"5\" Pitch=\"1\" HoleDepth=\"12\" ThreadDepth=\"9\" HoleTipAngle=\"118\"/>\n</Root>");
        var ex = Assert.Throws<StandardHoleLookupException>(() =>
            StandardHoleTable.Resolve(StandardHoleFamily.Threaded, "M6", _ => dup));
        Assert.Contains("无法唯一确定", ex.Message);
    }

    [Theory]
    [InlineData("M6.0")]
    [InlineData("m6")]
    [InlineData("M06")]
    public void NormalizeSize_CanonicalForms(string raw)
    {
        Assert.Equal("M6", StandardHoleTable.NormalizeSize(raw));
    }

    [Fact]
    public void UnsupportedSize_M2x5_RefusedBeforeTable()
    {
        var ex = Assert.Throws<StandardHoleLookupException>(() =>
            StandardHoleTable.Resolve(StandardHoleFamily.Threaded, "M2.5", _ => ThreadXml));
        Assert.Contains("不支持的孔规格", ex.Message);
    }

    [Fact]
    public void TableLoaderThrows_WrappedAsLookupFailure()
    {
        var ex = Assert.Throws<StandardHoleLookupException>(() =>
            StandardHoleTable.Resolve(StandardHoleFamily.Threaded, "M6",
                _ => throw new IOException("disk full")));
        Assert.Contains("读取孔标准表失败", ex.Message);
    }

    [Fact]
    public void MalformedXml_Refused()
    {
        var ex = Assert.Throws<StandardHoleLookupException>(() =>
            StandardHoleTable.Resolve(StandardHoleFamily.Threaded, "M6", _ => "<Root>"));
        Assert.Contains("解析失败", ex.Message);
    }
}
