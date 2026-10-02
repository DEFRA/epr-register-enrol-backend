using System.Net;
using System.Net.Http.Json;
using EprRegisterEnrolBackend.AccreditationApplication.Models;
using EprRegisterEnrolBackend.Test.Utils.Logging;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using MongoDB.Bson;

namespace EprRegisterEnrolBackend.Test.AccreditationApplication.Endpoints;

// RA-620: two production 400s from promoteOverseasSite could not be diagnosed because neither
// side logged which field the validator rejected. These pin that every overseas-site write
// now logs the failing property names and error codes - and never the submitted values.
public class AccreditationApplicationEndpointsValidationLoggingTests
    : IClassFixture<AccreditationApplicationEndpointsValidationLoggingTests.LoggingTestFactory>
{
    public sealed class LoggingTestFactory : AccreditationApplicationTestFactory
    {
        public CapturingLoggerFactory LogCapture { get; } = new();

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.ConfigureServices(services =>
                services.AddSingleton<ILoggerFactory>(LogCapture)
            );
        }
    }

    // 31 characters: one over the backend's ContactPhone limit, and a value that must never
    // reach a log line.
    private const string TooLongPhone = "+44 (0)20 7946 0000 9876 543210";

    private readonly LoggingTestFactory _factory;
    private readonly HttpClient _client;

    public AccreditationApplicationEndpointsValidationLoggingTests(LoggingTestFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
    }

    private string SeedApplication()
    {
        _factory.FakePersistence.Clear();
        _factory.LogCapture.Clear();
        var app = new AccreditationApplicationModel
        {
            Id = ObjectId.GenerateNewId(),
            OrganisationId = "org-123",
            Year = 2026,
            MaterialType = MaterialType.Steel,
            ApplicationStatus = ApplicationStatus.Saved,
        };
        _factory.FakePersistence.Seed(app);
        return app.Id!.Value.ToString();
    }

    private static PromoteOverseasSiteRequest InvalidPromoteRequest() =>
        new()
        {
            SiteName = "Promoted Recycling GmbH",
            AddressLine1 = "Neue Strasse 1",
            TownOrCity = "Munich",
            Country = "Germany",
            ContactName = "Greta Schmidt",
            ContactEmail = "greta@promotedrecycling.de",
            ContactPhone = TooLongPhone,
            OperationCodes = ["R3"],
            Code1 = "A1181",
            RepatriatedLoads = new string('x', 5001),
        };

    private static AddOverseasSiteRequest InvalidAddRequest() =>
        new()
        {
            SiteName = "Test Recycling GmbH",
            AddressLine1 = "Industriestrasse 42",
            TownOrCity = "Hamburg",
            Country = "Germany",
            ContactName = "Hans Müller",
            ContactEmail = "hans@testrecycling.de",
            ContactPhone = TooLongPhone,
            OperationCodes = ["R3"],
            Code1 = "A1181",
            RepatriatedLoads = "Rejected loads returned within 30 days at our expense.",
        };

    private CapturingLoggerFactory.Entry SingleValidationWarning(string operation)
    {
        var entries = _factory
            .LogCapture.Entries.Where(e =>
                e.LogLevel == LogLevel.Warning
                && e.Message.StartsWith($"{operation} validation failed", StringComparison.Ordinal)
            )
            .ToList();
        entries.Should().ContainSingle();
        return entries[0];
    }

    [Fact]
    public async Task Promote_ValidationFailure_LogsTheFailingFieldsWithoutTheirValues()
    {
        var applicationId = SeedApplication();

        var response = await _client.PostAsJsonAsync(
            $"/api/v1/accreditation-applications/org-123/{applicationId}/overseas-sites/900001/promote",
            InvalidPromoteRequest(),
            cancellationToken: TestContext.Current.CancellationToken
        );

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var entry = SingleValidationWarning("PromoteOverseasSite");
        entry.Message.Should().Contain($"applicationId={applicationId}");
        entry.Message.Should().Contain("ContactPhone (MaximumLengthValidator)");
        entry.Message.Should().Contain("RepatriatedLoads (MaximumLengthValidator)");
        entry.Message.Should().NotContain(TooLongPhone);
        entry.Message.Should().NotContain("greta@promotedrecycling.de");
    }

    [Fact]
    public async Task Update_ValidationFailure_LogsTheFailingFields()
    {
        var applicationId = SeedApplication();

        var response = await _client.PatchAsJsonAsync(
            $"/api/v1/accreditation-applications/org-123/{applicationId}/overseas-sites/900001",
            InvalidPromoteRequest(),
            cancellationToken: TestContext.Current.CancellationToken
        );

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var entry = SingleValidationWarning("UpdateOverseasSite");
        entry.Message.Should().Contain("ContactPhone (MaximumLengthValidator)");
        entry.Message.Should().NotContain(TooLongPhone);
    }

    [Fact]
    public async Task Add_ValidationFailure_LogsTheFailingFields()
    {
        var applicationId = SeedApplication();

        var response = await _client.PostAsJsonAsync(
            $"/api/v1/accreditation-applications/org-123/{applicationId}/overseas-sites",
            InvalidAddRequest(),
            cancellationToken: TestContext.Current.CancellationToken
        );

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var entry = SingleValidationWarning("AddOverseasSite");
        entry.Message.Should().Contain("ContactPhone (MaximumLengthValidator)");
        entry.Message.Should().NotContain(TooLongPhone);
    }

    [Fact]
    public async Task AddInterimSite_ValidationFailure_LogsTheFailingFields()
    {
        var applicationId = SeedApplication();

        var response = await _client.PostAsJsonAsync(
            $"/api/v1/accreditation-applications/org-123/{applicationId}/overseas-sites/900001/interim-site",
            new AddInterimSiteRequest
            {
                Country = "France",
                SiteName = "Interim Recycling Site",
                AddressLine1 = "1 Rue Example",
                TownOrCity = "Paris",
                Postcode = new string('9', 21),
                ContactName = "Jane Smith",
                ContactEmail = "jane.smith@example.com",
                ContactPhone = "+33 1 23 45 67 89",
                OperationCodes = ["R12"],
            },
            cancellationToken: TestContext.Current.CancellationToken
        );

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var entry = SingleValidationWarning("AddInterimSite");
        entry.Message.Should().Contain("Postcode (MaximumLengthValidator)");
        entry.Message.Should().NotContain("jane.smith@example.com");
    }

    [Fact]
    public async Task Promote_ValidRequest_DoesNotLogAValidationFailure()
    {
        var applicationId = SeedApplication();
        var request = InvalidPromoteRequest() with
        {
            ContactPhone = "+44 20 7946 0000",
            RepatriatedLoads = "Returned at our expense.",
        };

        await _client.PostAsJsonAsync(
            $"/api/v1/accreditation-applications/org-123/{applicationId}/overseas-sites/900001/promote",
            request,
            cancellationToken: TestContext.Current.CancellationToken
        );

        _factory
            .LogCapture.Entries.Should()
            .NotContain(e => e.Message.Contains("validation failed", StringComparison.Ordinal));
    }
}
