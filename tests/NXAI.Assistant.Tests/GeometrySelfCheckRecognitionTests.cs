using NXAI.Assistant.Core;
using Xunit;

namespace NXAI.Assistant.Tests;

/// <summary>
/// Phase 10 support: the geometry self-check's object-recognition case
/// (offline pins — the NX-side execution of the case is the self-check
/// button's live run, a separate acceptance item). These tests pin the
/// case's contract from its implementation source and the shared report
/// DTO so future edits cannot silently drop a classification dimension.
/// </summary>
public class GeometrySelfCheckRecognitionTests
{
    private static string SelfCheckSource =>
        File.ReadAllText(Path.Combine(
            AppContext.BaseDirectory, "..", "..", "..", "..", "..",
            "src", "NXAI.Assistant.NxHost.NX2512", "NxGeometrySelfCheck.cs"));

    [Fact]
    public void RecognitionCase_IsRegistered_InRunAll()
    {
        // The case must actually run — registered in RunAll's case list.
        Assert.Contains("CheckObjectRecognition(tempPart)", SelfCheckSource);
    }

    [Fact]
    public void RecognitionCase_Classifies_AllFourKinds()
    {
        // Body / datum_plane / planar_face / feature — the four selection
        // roles the mirror/pattern tools resolve.
        Assert.Contains("\"body\"", SelfCheckSource);
        Assert.Contains("\"datum_plane\"", SelfCheckSource);
        Assert.Contains("\"planar_face\"", SelfCheckSource);
        Assert.Contains("\"feature\"", SelfCheckSource);
    }

    [Fact]
    public void RecognitionCase_PlanarFace_OutputsOriginAndNormal()
    {
        // The verifiable plane geometry: origin + unit normal, asserted
        // against the fixture block's top face (Z=20, normal +Z).
        Assert.Contains("faceRow.OriginZ", SelfCheckSource);
        Assert.Contains("faceRow.NormalZ", SelfCheckSource);
        Assert.Contains("Math.Abs(faceRow.OriginZ.Value - 20.0)", SelfCheckSource);
    }

    [Fact]
    public void RecognitionCase_DetectsAmbiguousPlaneCandidates()
    {
        // Two plane candidates (datum + face) must be countable as an
        // ambiguity input — the mirror tool's multi-plane refusal contract.
        Assert.Contains("planeCandidates != 2", SelfCheckSource);
    }

    [Fact]
    public void RecognitionCase_IsReadOnly_OnUserModel()
    {
        // The case runs in the disposable temp part like every other case;
        // RunAll closes it without saving and restores the user's part.
        Assert.Contains("CloseModified.CloseModified", SelfCheckSource);
        Assert.Contains("SetDisplay(originalDisplayed", SelfCheckSource);
    }

    [Fact]
    public void RecognitionCase_UsesVerifiedFaceReadPaths()
    {
        // AskFaceUvMinmax + AskFaceProps — the verified path pair; the
        // forbidden UFEvalsf EVALUATOR (an actual API call, not the crash
        // note comment) must not appear.
        Assert.Contains("AskFaceUvMinmax", SelfCheckSource);
        Assert.Contains("AskFaceProps", SelfCheckSource);
        Assert.DoesNotContain("GetEvalsf", SelfCheckSource);
        Assert.DoesNotContain("NXOpen.UF.UFEvalsf.", SelfCheckSource);
    }

    // ---------- report DTO contract ----------

    [Fact]
    public void ReportDto_CarriesPerCaseDetails()
    {
        // The self-check report shape consumed by the chat UI: name, pass,
        // stage, input, expected, actual, error.
        var parameters = typeof(GeometrySelfCheckCase).GetConstructors()[0].GetParameters();
        var names = parameters.Select(p => p.Name).ToHashSet();
        Assert.Contains("Name", names);
        Assert.Contains("Passed", names);
        Assert.Contains("Actual", names);
        Assert.Contains("Error", names);
    }
}
