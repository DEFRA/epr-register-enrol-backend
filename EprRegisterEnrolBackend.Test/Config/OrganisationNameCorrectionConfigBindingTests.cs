using EprRegisterEnrolBackend.AccreditationApplication.Startup;
using EprRegisterEnrolBackend.Test.TestSupport;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace EprRegisterEnrolBackend.Test.Config;

// RA-764: the rename migration must ship switched off, and the
// Migrations__Ra764OrganisationNameCorrection__* keys must reach it.
public class OrganisationNameCorrectionConfigBindingTests(MongoIntegrationFixture fixture)
{
    [Fact]
    public async Task Defaults_are_off_and_the_migration_is_registered()
    {
        await using var factory = new EphemeralMongoTestFactory(
            fixture,
            "ra764_cfg",
            "Development"
        );
        using var scope = factory.Services.CreateScope();

        var options = scope
            .ServiceProvider.GetRequiredService<IOptions<OrganisationNameCorrectionOptions>>()
            .Value;

        options.Enabled.Should().BeFalse();
        options.Apply.Should().BeFalse();
        options.SpotCheckConfirmedBy.Should().BeNullOrEmpty();
        scope
            .ServiceProvider.GetServices<IHostedService>()
            .Should()
            .ContainSingle(s => s is OrganisationNameCorrectionMigration);
    }

    [Fact]
    public async Task Section_keys_bind()
    {
        await using var factory = new EphemeralMongoTestFactory(
            fixture,
            "ra764_cfg",
            "Development",
            new Dictionary<string, string?>
            {
                ["Migrations:Ra764OrganisationNameCorrection:Enabled"] = "true",
                ["Migrations:Ra764OrganisationNameCorrection:Apply"] = "true",
                ["Migrations:Ra764OrganisationNameCorrection:SpotCheckConfirmedBy"] =
                    "A Reviewer",
            }
        );
        using var scope = factory.Services.CreateScope();

        var options = scope
            .ServiceProvider.GetRequiredService<IOptions<OrganisationNameCorrectionOptions>>()
            .Value;

        options.Enabled.Should().BeTrue();
        options.Apply.Should().BeTrue();
        options.SpotCheckConfirmedBy.Should().Be("A Reviewer");
    }
}
