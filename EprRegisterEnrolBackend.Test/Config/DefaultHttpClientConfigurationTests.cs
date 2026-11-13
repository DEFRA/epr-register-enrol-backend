using EprRegisterEnrolBackend.Test.AccreditationApplication.Endpoints;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace EprRegisterEnrolBackend.Test.Config;

// RA-311 fix 3: the "DefaultClient" HttpClient is used for the Registration & Accreditation
// service BE -> ManagementBe submit call (HttpCaseWorkingApiAdapter.SubmitApplicationAsync).
// Without an explicit Timeout it defaults to .NET's 100s, which can leave the Registration &
// Accreditation service BE still waiting long after the Registration & Accreditation service
// FE's own submit budget has given up. This asserts the registration in Program.cs actually
// wires up the intended 15s timeout, rather than relying only on the adapter-level behavioural
// tests.
public class DefaultHttpClientConfigurationTests : IClassFixture<AccreditationApplicationTestFactory>
{
    private readonly AccreditationApplicationTestFactory _factory;

    public DefaultHttpClientConfigurationTests(AccreditationApplicationTestFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public void DefaultClient_IsConfiguredWithFifteenSecondTimeout()
    {
        var httpClientFactory = _factory.Services.GetRequiredService<IHttpClientFactory>();
        var client = httpClientFactory.CreateClient("DefaultClient");

        client.Timeout.Should().Be(TimeSpan.FromSeconds(15));
    }

    [Fact]
    public void BackgroundClient_IsConfiguredWithFifteenSecondTimeout()
    {
        var httpClientFactory = _factory.Services.GetRequiredService<IHttpClientFactory>();
        var client = httpClientFactory.CreateClient("BackgroundClient");

        client.Timeout.Should().Be(TimeSpan.FromSeconds(15));
    }

    // RA-766: the charge correction runs in a hosted service, outside any HTTP request. The
    // "DefaultClient" has header propagation, which throws InvalidOperationException with no
    // request to propagate from, so a call through it fails before it is sent. The
    // "BackgroundClient" must get as far as the network (port 1 refuses the connection).
    [Fact]
    public async Task BackgroundClient_CanSendOutsideAnHttpRequest_WhereDefaultClientCannot()
    {
        var httpClientFactory = _factory.Services.GetRequiredService<IHttpClientFactory>();
        var url = "http://127.0.0.1:1/";

        var viaDefault = async () =>
            await httpClientFactory
                .CreateClient("DefaultClient")
                .GetAsync(url, TestContext.Current.CancellationToken);
        var viaBackground = async () =>
            await httpClientFactory
                .CreateClient("BackgroundClient")
                .GetAsync(url, TestContext.Current.CancellationToken);

        await viaDefault.Should().ThrowAsync<InvalidOperationException>();
        await viaBackground.Should().ThrowAsync<HttpRequestException>();
    }
}
