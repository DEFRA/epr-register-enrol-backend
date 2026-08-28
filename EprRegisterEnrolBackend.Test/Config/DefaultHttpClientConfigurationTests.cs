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
}
