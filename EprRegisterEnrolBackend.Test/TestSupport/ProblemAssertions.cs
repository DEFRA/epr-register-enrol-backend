using System.Net;
using System.Text.Json;
using FluentAssertions;

namespace EprRegisterEnrolBackend.Test.TestSupport;

// The problem+json body every API in this service returns on an error
// (ServiceApiProblems): the status, a code, a detail and a traceId.
internal static class ProblemAssertions
{
    public static async Task AssertProblem(
        HttpResponseMessage response,
        HttpStatusCode status,
        string code
    )
    {
        response.StatusCode.Should().Be(status);
        response.Content.Headers.ContentType!.MediaType.Should().Be("application/problem+json");
        var body = JsonDocument
            .Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken))
            .RootElement;
        body.GetProperty("status").GetInt32().Should().Be((int)status);
        body.GetProperty("code").GetString().Should().Be(code);
        body.GetProperty("detail").GetString().Should().NotBeNullOrWhiteSpace();
        body.GetProperty("traceId").GetString().Should().NotBeNullOrWhiteSpace();
    }
}