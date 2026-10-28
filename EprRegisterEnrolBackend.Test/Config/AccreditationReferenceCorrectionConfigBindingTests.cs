using EprRegisterEnrolBackend.AccreditationApplication.Startup;
using EprRegisterEnrolBackend.Test.TestSupport;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace EprRegisterEnrolBackend.Test.Config;

// RA-691: the correction migration must ship switched off, and the
// Migrations__Ra691AccreditationReferenceCorrection__* keys must reach it. DryRun defaults on.
public class AccreditationReferenceCorrectionConfigBindingTests(MongoIntegrationFixture fixture)
{
    [Fact]
    public async Task Defaults_are_disabled_and_dry_run_and_the_migration_is_registered()
    {
        await using var factory = new EphemeralMongoTestFactory(
            fixture,
            "ra691_cfg",
            "Development"
        );
        using var scope = factory.Services.CreateScope();

        var options = scope
            .ServiceProvider.GetRequiredService<IOptions<AccreditationReferenceCorrectionOptions>>()
            .Value;

        options.Enabled.Should().BeFalse();
        options.DryRun.Should().BeTrue();
        options.SpotCheckConfirmedBy.Should().BeNullOrEmpty();
        scope
            .ServiceProvider.GetServices<IHostedService>()
            .Should()
            .ContainSingle(s => s is AccreditationReferenceCorrectionMigration);
    }

    [Fact]
    public async Task Enabled_alone_leaves_DryRun_true()
    {
        await using var factory = new EphemeralMongoTestFactory(
            fixture,
            "ra691_cfg",
            "Development",
            new Dictionary<string, string?>
            {
                ["Migrations:Ra691AccreditationReferenceCorrection:Enabled"] = "true",
            }
        );
        using var scope = factory.Services.CreateScope();

        var options = scope
            .ServiceProvider.GetRequiredService<IOptions<AccreditationReferenceCorrectionOptions>>()
            .Value;

        options.Enabled.Should().BeTrue();
        options.DryRun.Should().BeTrue();
    }

    [Fact]
    public async Task Section_keys_bind()
    {
        await using var factory = new EphemeralMongoTestFactory(
            fixture,
            "ra691_cfg",
            "Development",
            new Dictionary<string, string?>
            {
                ["Migrations:Ra691AccreditationReferenceCorrection:Enabled"] = "true",
                ["Migrations:Ra691AccreditationReferenceCorrection:DryRun"] = "false",
                ["Migrations:Ra691AccreditationReferenceCorrection:SpotCheckConfirmedBy"] =
                    "A Reviewer",
            }
        );
        using var scope = factory.Services.CreateScope();

        var options = scope
            .ServiceProvider.GetRequiredService<IOptions<AccreditationReferenceCorrectionOptions>>()
            .Value;

        options.Enabled.Should().BeTrue();
        options.DryRun.Should().BeFalse();
        options.SpotCheckConfirmedBy.Should().Be("A Reviewer");
    }
}
