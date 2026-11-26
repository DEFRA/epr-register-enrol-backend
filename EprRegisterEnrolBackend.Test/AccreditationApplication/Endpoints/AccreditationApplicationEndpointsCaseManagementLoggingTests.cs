using System.Net;
using System.Net.Http.Json;
using System.Text;
using EprRegisterEnrolBackend.AccreditationApplication.Models;
using EprRegisterEnrolBackend.AccreditationApplication.Services;
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

    // Chris's review of PR #297: toStateId is a required string, but an explicit null (or a blank)
    // still binds. Before the guard a null threw in the mapping (a 500) and a blank advanced the
    // ordering watermark for a move nobody can name. Now it is a 400 ProblemDetails, logged at
    // Error with the ids, and nothing is read or written.
    [Theory]
    [InlineData("null")]
    [InlineData("\"\"")]
    [InlineData("\"   \"")]
    public async Task StatusChangedFromCaseManagement_NoToStateId_Returns400AndLogsErrorAndWritesNothing(
        string toStateIdJson
    )
    {
        _factory.FakePersistence.Clear();
        _factory.LogCapture.Clear();
        var workItemId = Guid.NewGuid();
        var watermark = DateTime.UtcNow.AddMinutes(-5);
        _factory.FakePersistence.Seed(
            new AccreditationApplicationModel
            {
                Id = ObjectId.GenerateNewId(),
                OrganisationId = "org-123",
                Year = 2026,
                MaterialType = MaterialType.Steel,
                ApplicationStatus = ApplicationStatus.Submitted,
                CaseManagementWorkItemId = workItemId,
                CaseManagementStatusUpdatedAt = watermark,
            }
        );
        using var body = new StringContent(
            $$"""{"toStateId":{{toStateIdJson}},"actionId":"make-duly-made","occurredAt":"{{DateTime.UtcNow:O}}"}""",
            Encoding.UTF8,
            "application/json"
        );

        var response = await _client.PostAsync(
            $"/api/v1/accreditation-applications/case-management/{workItemId}/status",
            body,
            TestContext.Current.CancellationToken
        );

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        response.Content.Headers.ContentType!.MediaType.Should().Be("application/problem+json");
        (await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken))
            .Should()
            .Contain(CaseManagementStatusMapping.MissingToStateIdDetail);
        var entry = _factory
            .LogCapture.Entries.Should()
            .ContainSingle(e => e.LogLevel == LogLevel.Error)
            .Subject;
        entry.Message.Should().Contain("StatusChangedFromCaseManagement: status push has no toStateId");
        entry.Message.Should().Contain($"workItemId={workItemId}");
        entry.Message.Should().Contain("actionId=make-duly-made");
        entry.Message.Should().Contain("correlationId=(absent)");
        var stored = await _factory.FakePersistence.GetByCaseManagementWorkItemIdAsync(workItemId);
        stored!.ApplicationStatus.Should().Be(ApplicationStatus.Submitted);
        stored.CaseManagementStatusUpdatedAt.Should().Be(watermark);
    }
}
