using NXAI.Assistant.Core;
using Xunit;

namespace NXAI.Assistant.Tests;

public class NxVersionProfileTests
{
    [Theory]
    [InlineData(NxCompatibilityStatus.Planned)]
    [InlineData(NxCompatibilityStatus.Built)]
    [InlineData(NxCompatibilityStatus.Loaded)]
    public void Mutation_tools_stay_disabled_below_integration_testing(NxCompatibilityStatus status)
    {
        var profile = MakeProfile(status);
        Assert.False(profile.CanEnableModelMutationTools);
    }

    [Theory]
    [InlineData(NxCompatibilityStatus.IntegrationTested)]
    [InlineData(NxCompatibilityStatus.Supported)]
    public void Mutation_tools_allowed_only_after_real_integration_testing(NxCompatibilityStatus status)
    {
        var profile = MakeProfile(status);
        Assert.True(profile.CanEnableModelMutationTools);
    }

    private static NxVersionProfile MakeProfile(NxCompatibilityStatus status) =>
        new(
            ProfileId: "test-profile",
            NxRelease: "NX2512",
            Status: status,
            HostRuntime: "test",
            NxOpenAssemblyVersion: "test",
            AdapterPackage: "test",
            Capabilities: new[] { "read-active-part" });
}
