using EprRegisterEnrolBackend.AccreditationApplication.Adapters;
using EprRegisterEnrolBackend.CdpUploader.Config;
using EprRegisterEnrolBackend.ReEx.Config;
using EprRegisterEnrolBackend.Utils.Health;
using FluentAssertions;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace EprRegisterEnrolBackend.Test.Utils.Health;

public class RequiredConfigHealthCheckTests
{
    [Fact]
    public async Task CheckHealthAsync_Healthy_WhenAllRequiredConfigPresent()
    {
        var check = MakeCheck();

        var result = await CheckHealth(check);

        result.Status.Should().Be(HealthStatus.Healthy);
        result.Description.Should().BeNullOrEmpty();
    }

    public static TheoryData<string, Action<Builder>> IndividuallyMissingKeys() =>
        new()
        {
            { "ReExApi__BaseUrl", b => b.ReExBaseUrl = "" },
            { "REEX_API_BASIC_AUTH_USERNAME", b => b.ReExUsername = "" },
            { "REEX_API_BASIC_AUTH_PASSWORD", b => b.ReExPassword = "" },
            { "App__BaseUrl", b => b.AppBaseUrl = "" },
            { "CdpUploader__Url", b => b.CdpUploaderUrl = "" },
            { "CaseWorking__Url", b => b.CaseWorkingUrl = "" },
            { "CASE_MANAGEMENT_API_SHARED_SECRET", b => b.CaseWorkingSharedSecret = "" },
            { "AUTH_SHARED_SECRET__MANAGEMENT_BE", b => b.CaseManagementAuthSharedSecret = "" }
        };

    [Theory]
    [MemberData(nameof(IndividuallyMissingKeys))]
    public async Task CheckHealthAsync_Unhealthy_WhenSingleKeyMissing(
        string expectedKey,
        Action<Builder> makeBlank
    )
    {
        var builder = new Builder();
        makeBlank(builder);
        var check = MakeCheck(builder);

        var result = await CheckHealth(check);

        result.Status.Should().Be(HealthStatus.Unhealthy);
        result.Description.Should().Contain(expectedKey);
    }

    [Fact]
    public async Task CheckHealthAsync_Healthy_WhenReExAndCaseManagementAuthMissingInDevelopment()
    {
        var check = MakeCheck(
            new Builder
            {
                ReExBaseUrl = "",
                ReExUsername = "",
                ReExPassword = "",
                CaseManagementAuthSharedSecret = "",
                IsDevelopment = true
            }
        );

        var result = await CheckHealth(check);

        result.Status.Should().Be(HealthStatus.Healthy);
    }

    [Fact]
    public async Task CheckHealthAsync_Healthy_WhenUsingStubInDevelopment_EvenWithNoUrlOrSecret()
    {
        var check = MakeCheck(
            new Builder
            {
                UseStub = true,
                CaseWorkingUrl = "",
                CaseWorkingSharedSecret = "",
                IsDevelopment = true
            }
        );

        var result = await CheckHealth(check);

        result.Status.Should().Be(HealthStatus.Healthy);
    }

    [Fact]
    public async Task CheckHealthAsync_Healthy_WhenRealAdapterInDevelopment_EvenWithNoSharedSecret()
    {
        // appsettings.Development.json sets CaseWorking:UseStub=false (the real adapter runs
        // locally) but never provides CASE_MANAGEMENT_API_SHARED_SECRET — HttpCaseWorkingApiAdapter
        // tolerates that by sending unsigned, so this must not report unhealthy on a vanilla
        // local run. Url is still required even in Development, since the real adapter needs
        // somewhere to call.
        var check = MakeCheck(
            new Builder
            {
                UseStub = false,
                CaseWorkingUrl = "http://localhost:8085",
                CaseWorkingSharedSecret = "",
                IsDevelopment = true
            }
        );

        var result = await CheckHealth(check);

        result.Status.Should().Be(HealthStatus.Healthy);
    }

    [Fact]
    public async Task CheckHealthAsync_Unhealthy_WhenStillUsingStubOutsideDevelopment()
    {
        var check = MakeCheck(new Builder { UseStub = true, IsDevelopment = false });

        var result = await CheckHealth(check);

        result.Status.Should().Be(HealthStatus.Unhealthy);
        result.Description.Should().Contain("CaseWorking__UseStub");
    }

    [Fact]
    public async Task CheckHealthAsync_ListsEveryMissingKey()
    {
        var check = MakeCheck(
            new Builder
            {
                ReExBaseUrl = "",
                ReExUsername = "",
                ReExPassword = "",
                AppBaseUrl = "",
                CdpUploaderUrl = "",
                CaseWorkingUrl = "",
                CaseWorkingSharedSecret = "",
                CaseManagementAuthSharedSecret = ""
            }
        );

        var result = await CheckHealth(check);

        result.Status.Should().Be(HealthStatus.Unhealthy);
        result.Description.Should()
            .Contain("ReExApi__BaseUrl")
            .And.Contain("REEX_API_BASIC_AUTH_USERNAME")
            .And.Contain("REEX_API_BASIC_AUTH_PASSWORD")
            .And.Contain("App__BaseUrl")
            .And.Contain("CdpUploader__Url")
            .And.Contain("CaseWorking__Url")
            .And.Contain("CASE_MANAGEMENT_API_SHARED_SECRET")
            .And.Contain("AUTH_SHARED_SECRET__MANAGEMENT_BE");
    }

    private static Task<HealthCheckResult> CheckHealth(RequiredConfigHealthCheck check) =>
        check.CheckHealthAsync(new HealthCheckContext(), TestContext.Current.CancellationToken);

    // All fields default to a valid, present value — an all-healthy config — so each test
    // only needs to say which field(s) it's blanking out.
    public sealed class Builder
    {
        public string ReExBaseUrl { get; set; } = "http://reex.test";
        public string ReExUsername { get; set; } = "user";
        public string ReExPassword { get; set; } = "pass";
        public string AppBaseUrl { get; set; } = "http://app.test";
        public string CdpUploaderUrl { get; set; } = "http://uploader.test";
        public string CaseWorkingUrl { get; set; } = "http://case-working.test";
        public string CaseWorkingSharedSecret { get; set; } = "case-working-secret";
        public string CaseManagementAuthSharedSecret { get; set; } = "case-management-secret";
        public bool UseStub { get; set; } = false;
        public bool IsDevelopment { get; set; } = false;
    }

    private static RequiredConfigHealthCheck MakeCheck(Builder? builder = null)
    {
        builder ??= new Builder();

        var environment = Substitute.For<IHostEnvironment>();
        environment.EnvironmentName = builder.IsDevelopment
            ? Environments.Development
            : Environments.Production;

        return new RequiredConfigHealthCheck(
            Options.Create(new ReExConfig { BaseUrl = builder.ReExBaseUrl }),
            Options.Create(
                new ReExCredentials { Username = builder.ReExUsername, Password = builder.ReExPassword }
            ),
            Options.Create(new AppConfig { BaseUrl = builder.AppBaseUrl }),
            Options.Create(new CdpUploaderConfig { Url = builder.CdpUploaderUrl }),
            Options.Create(
                new CaseWorkingApiConfig
                {
                    Url = builder.CaseWorkingUrl,
                    SharedSecret = builder.CaseWorkingSharedSecret,
                    UseStub = builder.UseStub
                }
            ),
            Options.Create(
                new CaseManagementAuthConfig { SharedSecret = builder.CaseManagementAuthSharedSecret }
            ),
            environment
        );
    }
}