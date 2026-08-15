using EprRegisterEnrolBackend.AccreditationApplication.Adapters;
using EprRegisterEnrolBackend.CdpUploader.Config;
using EprRegisterEnrolBackend.FileUpload.Config;
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
        var check = MakeCheck(caseWorkingUrl: "http://case-working.test", useStub: false);

        var result = await check.CheckHealthAsync(
            new HealthCheckContext(),
            TestContext.Current.CancellationToken
        );

        result.Status.Should().Be(HealthStatus.Healthy);
    }

    [Fact]
    public async Task CheckHealthAsync_Unhealthy_WhenReExApiBaseUrlMissing()
    {
        var check = MakeCheck(reExBaseUrl: "", isDevelopment: false);

        var result = await check.CheckHealthAsync(
            new HealthCheckContext(),
            TestContext.Current.CancellationToken
        );

        result.Status.Should().Be(HealthStatus.Unhealthy);
        result.Description.Should().Contain("ReExApi__BaseUrl");
    }

    [Fact]
    public async Task CheckHealthAsync_Healthy_WhenReExApiBaseUrlMissingInDevelopment()
    {
        var check = MakeCheck(reExBaseUrl: "", reExUsername: "", reExPassword: "", isDevelopment: true);

        var result = await check.CheckHealthAsync(
            new HealthCheckContext(),
            TestContext.Current.CancellationToken
        );

        result.Status.Should().Be(HealthStatus.Healthy);
    }

    [Fact]
    public async Task CheckHealthAsync_Unhealthy_WhenCaseWorkingUrlMissingAndNotUsingStub()
    {
        var check = MakeCheck(caseWorkingUrl: "", useStub: false);

        var result = await check.CheckHealthAsync(
            new HealthCheckContext(),
            TestContext.Current.CancellationToken
        );

        result.Status.Should().Be(HealthStatus.Unhealthy);
        result.Description.Should().Contain("CaseWorking__Url");
    }

    [Fact]
    public async Task CheckHealthAsync_Healthy_WhenCaseWorkingUrlMissingButUsingStub()
    {
        var check = MakeCheck(caseWorkingUrl: "", useStub: true);

        var result = await check.CheckHealthAsync(
            new HealthCheckContext(),
            TestContext.Current.CancellationToken
        );

        result.Status.Should().Be(HealthStatus.Healthy);
    }

    [Fact]
    public async Task CheckHealthAsync_ListsEveryMissingKey()
    {
        var check = MakeCheck(
            reExBaseUrl: "",
            reExUsername: "",
            reExPassword: "",
            appBaseUrl: "",
            cdpUploaderUrl: "",
            s3Region: "",
            caseWorkingUrl: "",
            useStub: false,
            isDevelopment: false
        );

        var result = await check.CheckHealthAsync(
            new HealthCheckContext(),
            TestContext.Current.CancellationToken
        );

        result.Status.Should().Be(HealthStatus.Unhealthy);
        result.Description.Should()
            .Contain("ReExApi__BaseUrl")
            .And.Contain("REEX_API_BASIC_AUTH_USERNAME")
            .And.Contain("REEX_API_BASIC_AUTH_PASSWORD")
            .And.Contain("App__BaseUrl")
            .And.Contain("CdpUploader__Url")
            .And.Contain("S3__Region")
            .And.Contain("CaseWorking__Url");
    }

    private static RequiredConfigHealthCheck MakeCheck(
        string reExBaseUrl = "http://reex.test",
        string reExUsername = "user",
        string reExPassword = "pass",
        string appBaseUrl = "http://app.test",
        string cdpUploaderUrl = "http://uploader.test",
        string s3Region = "eu-west-2",
        string caseWorkingUrl = "http://case-working.test",
        bool useStub = true,
        bool isDevelopment = false
    )
    {
        var environment = Substitute.For<IHostEnvironment>();
        environment.EnvironmentName = isDevelopment
            ? Environments.Development
            : Environments.Production;

        return new RequiredConfigHealthCheck(
            Options.Create(new ReExConfig { BaseUrl = reExBaseUrl }),
            Options.Create(new ReExCredentials { Username = reExUsername, Password = reExPassword }),
            Options.Create(new AppConfig { BaseUrl = appBaseUrl }),
            Options.Create(new CdpUploaderConfig { Url = cdpUploaderUrl }),
            Options.Create(new S3Config { Region = s3Region }),
            Options.Create(new CaseWorkingApiConfig { Url = caseWorkingUrl, UseStub = useStub }),
            environment
        );
    }
}