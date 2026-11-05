using EprRegisterEnrolBackend.AccreditationApplication.Startup;
using EprRegisterEnrolBackend.DevSeed;
using EprRegisterEnrolBackend.Registrations;
using EprRegisterEnrolBackend.Test.TestSupport;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace EprRegisterEnrolBackend.Test.DevSeed;

// RA-425: the local compose stack (DevSeedData:Enabled) gets every dev seed; the fe-tests stack
// (DevSeedData:ManageExporterEnabled) gets only the dedicated exporter, because its other specs use
// Delta Green and must not see the RA-690 data.
public sealed class DevSeedRegistrationTests(MongoIntegrationFixture fixture)
{
    private IEnumerable<Type> DevSeedsRegistered(
        string environment,
        Dictionary<string, string?> settings
    )
    {
        using var factory = new EphemeralMongoTestFactory(
            fixture,
            "dev_seed_registration",
            environment,
            settings
        );
        return factory
            .Services.GetServices<IHostedService>()
            .OfType<DevSeedService>()
            .Select(s => s.GetType())
            .ToList();
    }

    [Fact]
    public void DevSeedDataEnabled_RegistersEveryDevSeed()
    {
        DevSeedsRegistered(
                Environments.Development,
                new() { [DevAccreditationHistorySeedService.ConfigKey] = "true" }
            )
            .Should()
            .BeEquivalentTo(
                [
                    typeof(DevAccreditationHistorySeedService),
                    typeof(DevRegistrationSeedService),
                    typeof(DevExporterAccreditationSeedService),
                ]
            );
    }

    [Fact]
    public void ManageExporterEnabledAlone_RegistersOnlyTheExporterSeed()
    {
        DevSeedsRegistered(
                Environments.Development,
                new() { [DevExporterAccreditationSeedService.ConfigKey] = "true" }
            )
            .Should()
            .Equal(typeof(DevExporterAccreditationSeedService));
    }

    [Fact]
    public void NeitherFlag_RegistersNoDevSeed()
    {
        DevSeedsRegistered(Environments.Development, []).Should().BeEmpty();
    }

    [Fact]
    public void OutsideDevelopment_RegistersNoDevSeed()
    {
        DevSeedsRegistered(
                Environments.Production,
                new()
                {
                    [DevAccreditationHistorySeedService.ConfigKey] = "true",
                    [DevExporterAccreditationSeedService.ConfigKey] = "true",
                }
            )
            .Should()
            .BeEmpty();
    }
}
