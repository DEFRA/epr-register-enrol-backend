using EprRegisterEnrolBackend.AccreditationApplication.Startup;
using EprRegisterEnrolBackend.Test.TestSupport;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace EprRegisterEnrolBackend.Test.Config;

// RA-768: the overseas sites correction must ship switched off, and the
// Migrations__Ra768OverseasSitesCorrection__* keys must reach it.
public class OverseasSitesCorrectionConfigBindingTests(MongoIntegrationFixture fixture)
{
    [Fact]
    public async Task Defaults_are_off_and_the_migration_is_registered()
    {
        await using var factory = new EphemeralMongoTestFactory(
            fixture,
            "ra768_cfg",
            "Development"
        );
        using var scope = factory.Services.CreateScope();

        var options = scope
            .ServiceProvider.GetRequiredService<IOptions<OverseasSitesCorrectionOptions>>()
            .Value;

        options.Enabled.Should().BeFalse();
        options.Apply.Should().BeFalse();
        options.SpotCheckConfirmedBy.Should().BeNullOrEmpty();
        scope
            .ServiceProvider.GetServices<IHostedService>()
            .Should()
            .ContainSingle(s => s is OverseasSitesCorrectionMigration);
    }

    [Fact]
    public async Task Section_keys_bind()
    {
        await using var factory = new EphemeralMongoTestFactory(
            fixture,
            "ra768_cfg",
            "Development",
            new Dictionary<string, string?>
            {
                ["Migrations:Ra768OverseasSitesCorrection:Enabled"] = "true",
                ["Migrations:Ra768OverseasSitesCorrection:Apply"] = "true",
                ["Migrations:Ra768OverseasSitesCorrection:SpotCheckConfirmedBy"] =
                    "A Reviewer",
            }
        );
        using var scope = factory.Services.CreateScope();

        var options = scope
            .ServiceProvider.GetRequiredService<IOptions<OverseasSitesCorrectionOptions>>()
            .Value;

        options.Enabled.Should().BeTrue();
        options.Apply.Should().BeTrue();
        options.SpotCheckConfirmedBy.Should().Be("A Reviewer");
    }
}
