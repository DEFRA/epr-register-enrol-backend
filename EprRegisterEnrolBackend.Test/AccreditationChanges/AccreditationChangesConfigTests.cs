using EprRegisterEnrolBackend.AccreditationChanges;
using EprRegisterEnrolBackend.Test.AccreditationApplication.Endpoints;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace EprRegisterEnrolBackend.Test.AccreditationChanges;

// RA-739: the submit-to-case-management flag's defaults. Running the service locally (Development) submits
// to the local management-be it already points at, as the monorepo compose stack does; every
// other environment takes the base appsettings' false, and CDP declares it per environment.
public class AccreditationChangesConfigTests : IDisposable
{
    private const string Key =
        AccreditationChangesConfig.SectionName + ":" + nameof(AccreditationChangesConfig.SubmitToCaseManagement);

    private readonly AccreditationApplicationTestFactory _factory = new();

    public void Dispose() => _factory.Dispose();

    [Fact]
    public void Development_SubmitsToCaseManagement()
    {
        var configuration = _factory.Services.GetRequiredService<IConfiguration>();

        configuration.GetValue<bool>(Key).Should().BeTrue();
    }

    [Fact]
    public void BaseAppSettings_DoNotSubmitToCaseManagement()
    {
        var contentRoot = _factory.Services.GetRequiredService<IHostEnvironment>().ContentRootPath;
        var configuration = new ConfigurationBuilder()
            .SetBasePath(contentRoot)
            .AddJsonFile("appsettings.json", optional: false)
            .Build();

        configuration[Key].Should().Be("False");
    }
}