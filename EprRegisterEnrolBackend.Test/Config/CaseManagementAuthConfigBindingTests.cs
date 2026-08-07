using EprRegisterEnrolBackend.AccreditationApplication.Adapters;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace EprRegisterEnrolBackend.Test.Config;

// Naming-convention fix: CaseManagementAuthConfig.SharedSecret must be sourced
// from the flat OPERATOR_BACKEND_SHARED_SECRET env var (CDP's secrets naming
// convention — flat UPPER_SNAKE_CASE, not the nested CaseManagementAuth__*
// form ExpectedCognitoClientId uses), rather than CaseManagementAuth:SharedSecret.
// See Program.cs and CaseManagementAuthConfig.cs.
public class CaseManagementAuthConfigBindingTests
{
    [Fact]
    public async Task SharedSecret_BindsFromFlatOperatorBackendSharedSecretEnvVar()
    {
        await using var factory = new BindingTestFactory(
            new Dictionary<string, string?>
            {
                ["OPERATOR_BACKEND_SHARED_SECRET"] = "test-secret",
                ["CaseManagementAuth:ExpectedCognitoClientId"] = "epr-register-enrol-management-be",
            }
        );
        using var scope = factory.Services.CreateScope();

        var config = scope
            .ServiceProvider.GetRequiredService<IOptions<CaseManagementAuthConfig>>()
            .Value;

        config.SharedSecret.Should().Be("test-secret");
        config.ExpectedCognitoClientId.Should().Be("epr-register-enrol-management-be");
    }

    [Fact]
    public async Task SharedSecret_IgnoresRetiredNestedCaseManagementAuthSharedSecretKey()
    {
        // The old CaseManagementAuth__SharedSecret env var name must have no
        // effect after the naming-convention fix — otherwise an operator who
        // hasn't migrated their secret's env var name yet would be silently
        // unsigned (empty SharedSecret) rather than getting a clear signal
        // that the old name no longer works.
        await using var factory = new BindingTestFactory(
            new Dictionary<string, string?>
            {
                ["CaseManagementAuth:SharedSecret"] = "should-not-be-used",
            }
        );
        using var scope = factory.Services.CreateScope();

        var config = scope
            .ServiceProvider.GetRequiredService<IOptions<CaseManagementAuthConfig>>()
            .Value;

        config.SharedSecret.Should().BeNullOrEmpty();
    }

    private sealed class BindingTestFactory(IReadOnlyDictionary<string, string?> configOverrides)
        : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Development");
            builder.ConfigureAppConfiguration(
                (_, config) => config.AddInMemoryCollection(configOverrides)
            );
        }
    }
}
