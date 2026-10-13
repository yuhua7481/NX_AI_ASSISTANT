namespace NXAI.Assistant.Core;

/// <summary>
/// Compatibility lifecycle for one NX release profile.
/// A release may only enable model-mutating tools at <see cref="Supported"/>.
/// </summary>
public enum NxCompatibilityStatus
{
    Planned,
    Built,
    Loaded,
    IntegrationTested,
    Supported,
}

/// <summary>
/// Recorded facts about one supported NX release and the adapter built for it.
/// Filled from real probes (NxCompatProbe) or real load/integration evidence —
/// never from folder-name guesses.
/// </summary>
public sealed record NxVersionProfile(
    string ProfileId,
    string NxRelease,
    NxCompatibilityStatus Status,
    string HostRuntime,
    string NxOpenAssemblyVersion,
    string AdapterPackage,
    IReadOnlyList<string> Capabilities)
{
    /// <summary>
    /// Model-mutating tools stay disabled unless the profile passed
    /// real integration testing for this release.
    /// </summary>
    public bool CanEnableModelMutationTools =>
        Status is NxCompatibilityStatus.IntegrationTested or NxCompatibilityStatus.Supported;
}
