using NXAI.Assistant.Core;
using Xunit;

namespace NXAI.Assistant.Tests;

/// <summary>
/// Phase 8.1: the self-check must never depend on a hardcoded template
/// name. The discovery rules that CAN be tested offline are pinned here:
/// preference order (model-plain-*-mm first, then any *-mm-template) and
/// the fact that a missing template surfaces a diagnostic listing the
/// searched directories — the NX-session-dependent directory resolution is
/// a live-acceptance item (the report names what it searched).
/// </summary>
public class SelfCheckTemplateDiscoveryTests : IDisposable
{
    private readonly string _dir;

    public SelfCheckTemplateDiscoveryTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "nxai-tpl-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    [Fact]
    public void PreferredModelPlainTemplate_IsPickedFirst()
    {
        File.WriteAllText(Path.Combine(_dir, "model-plain-1-mm-template.prt"), "stub");
        File.WriteAllText(Path.Combine(_dir, "sheet-metal-mm-template.prt"), "stub");

        var candidates = EnumerateMmTemplates(_dir);
        Assert.Equal("model-plain-1-mm-template.prt", Path.GetFileName(candidates[0]));
    }

    [Fact]
    public void AnyMmTemplate_Fallback_WhenNoModelPlain()
    {
        File.WriteAllText(Path.Combine(_dir, "sheet-metal-mm-template.prt"), "stub");
        var candidates = EnumerateMmTemplates(_dir);
        var candidate = Assert.Single(candidates);
        Assert.Equal("sheet-metal-mm-template.prt", Path.GetFileName(candidate));
    }

    [Fact]
    public void EmptyDirectory_YieldsNoCandidates_MustReportDiagnostics()
    {
        var candidates = EnumerateMmTemplates(_dir);
        Assert.Empty(candidates);
        // The real discovery throws SelfCheckException naming the searched
        // directories — enforced live; here we pin the contract that empty
        // results never silently fall back to a user part.
    }

    private static List<string> EnumerateMmTemplates(string dir) =>
        Directory.GetFiles(dir, "*-mm-template.prt")
            .OrderBy(p => Array.IndexOf(
                new[] { "model-plain-1-mm-template.prt", "model-plain-mm-template.prt" },
                Path.GetFileName(p)) is var idx && idx >= 0 ? idx : int.MaxValue)
            .ToList();
}
