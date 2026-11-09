using System.Net;
using System.Net.Http.Json;
using EprRegisterEnrolBackend.AccreditationApplication.Models;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using MongoDB.Bson;

namespace EprRegisterEnrolBackend.Test.AccreditationApplication.Endpoints;

// RA-527: a repeated final-status push from the Case Management service is accepted with no
// write and logged at Information. The default test host's logger has Information disabled,
// so this class uses the capturing logger factory to run and pin that log line.
public class AccreditationApplicationEndpointsCaseManagementLoggingTests
    : IClassFixture<AccreditationApplicationEndpointsValidationLoggingTests.LoggingTestFactory>
{
    private readonly AccreditationApplicationEndpointsValidationLoggingTests.LoggingTestFactory _factory;
    private readonly HttpClient _client;

    public AccreditationApplicationEndpointsCaseManagementLoggingTests(
        AccreditationApplicationEndpointsValidationLoggingTests.LoggingTestFactory factory
    )
    {
        _factory = factory;
        _client = factory.CreateClient();
    }

    [Theory]
    [InlineData(ApplicationStatus.Approved, "approved")]
    [InlineData(ApplicationStatus.Rejected, "rejected")]
    public async Task StatusChangedFromCaseManagement_RepeatOfCurrentFinalStatus_LogsTheIgnoredRepeat(
        ApplicationStatus finalStatus,
        string toStateId
    )
    {
        _factory.FakePersistence.Clear();
        _factory.LogCapture.Clear();
        var workItemId = Guid.NewGuid();
        var application = new AccreditationApplicationModel
        {
            Id = ObjectId.GenerateNewId(),
            OrganisationId = "org-123",
            Year = 2026,
            MaterialType = MaterialType.Steel,
            ApplicationStatus = finalStatus,
            CaseManagementWorkItemId = workItemId,
            CaseManagementStatusUpdatedAt = DateTime.UtcNow.AddMinutes(-5),
        };
        _factory.FakePersistence.Seed(application);

        var response = await _client.PostAsJsonAsync(
            $"/api/v1/accreditation-applications/case-management/{workItemId}/status",
            new StatusChangedFromCaseManagementRequest
            {
                ToStateId = toStateId,
                ActionId = "confirm-applicant-contact",
                OccurredAt = DateTime.UtcNow,
            },
            cancellationToken: TestContext.Current.CancellationToken
        );

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var entry = _factory
            .LogCapture.Entries.Should()
            .ContainSingle(e =>
                e.LogLevel == LogLevel.Information
                && e.Message.StartsWith(
                    "StatusChangedFromCaseManagement: idempotent repeat of final status",
                    StringComparison.Ordinal
                )
            )
            .Subject;
        entry.Message.Should().Contain($"{finalStatus}");
        entry.Message.Should().Contain($"applicationId={application.Id}");
        entry.Message.Should().Contain($"workItemId={workItemId}");
        entry.Message.Should().Contain($"toStateId={toStateId}");
        entry.Message.Should().Contain("correlationId=(absent)");
        _factory
            .LogCapture.Entries.Should()
            .NotContain(e => e.Message.Contains("is terminal", StringComparison.Ordinal));
    }
}
