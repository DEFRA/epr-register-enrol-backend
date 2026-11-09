using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using EprRegisterEnrolBackend.AccreditationApplication.Models;
using EprRegisterEnrolBackend.Config;
using EprRegisterEnrolBackend.DomainModel.Entities;
using EprRegisterEnrolBackend.DomainModel.Versioning;
using EprRegisterEnrolBackend.Organisation.Services;
using EprRegisterEnrolBackend.Registrations;
using EprRegisterEnrolBackend.Test.TestSupport;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using MongoDB.Bson;

namespace EprRegisterEnrolBackend.Test.Registrations;

// RA-425: the frontend asks for a registration's current status before showing any of the
// material dashboard pages. Driven through the real host and auth pipeline (outside Development,
// where the Frontend scheme's bypass would hide a missing gate), against a real ephemeral Mongo,
// with the domain model's Expand flag both off and on. Mapped only with FeatureFlags:InYearChangesEnabled.
public sealed class RegistrationStatusEndpointTests(MongoIntegrationFixture fixture)
{
    private const string FrontendSecret = "integration-frontend-secret";
    private static readonly string ReExRegistrationId =
        FakeOrganisationPersistence.Reg50003.ToString();
    private static readonly string StatusPath = $"/api/v1/registrations/{ReExRegistrationId}/status";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private EphemeralMongoTestFactory Factory(bool expandEnabled, bool inYearChangesEnabled = true) =>
        new(
            fixture,
            "registration_status",
            Environments.Production,
            new Dictionary<string, string?>
            {
                ["AUTH_SHARED_SECRET:FRONTEND"] = FrontendSecret,
                ["AUTH_SHARED_SECRET:MANAGEMENT_BE"] = "integration-management-be-secret",
                ["CaseWorking:UseStub"] = "true",
                ["DomainModel:ExpandEnabled"] = expandEnabled ? "true" : "false",
                [FeatureFlags.InYearChangesEnabledKey] = inYearChangesEnabled ? "true" : "false",
            }
        );

    private static HttpClient FrontendClient(EphemeralMongoTestFactory factory)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            FrontendSecret
        );
        return client;
    }

    private static VersionedMongoRepository<RegistrationEntity> Versions(
        EphemeralMongoTestFactory factory
    ) => factory.Services.GetRequiredService<RegistrationVersionStore>().Versions;

    private static RegistrationEntity Registration(RegistrationStatus status) =>
        new()
        {
            OrganisationId = ObjectId.GenerateNewId(),
            ReExRegistrationId = ReExRegistrationId,
            RegistrationNumber = "R25ER0500036908PL",
            Status = status,
            WasteProcessingType = WasteProcessingKind.Reprocessor,
            Material = MaterialType.Plastic,
        };

    private static async Task<JsonElement> Json(HttpResponseMessage response) =>
        JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct)).RootElement.Clone();

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReturnsTheCurrentVersionsStatus_NotAnOlderVersions(bool expandEnabled)
    {
        using var factory = Factory(expandEnabled);
        using var client = FrontendClient(factory);
        var versions = Versions(factory);
        var created = await versions.InsertAsync(
            Registration(RegistrationStatus.Created),
            new ChangeInfo { Reason = ChangeReason.ReExImport },
            cancellationToken: Ct
        );
        await versions.UpdateAsync(
            Registration(RegistrationStatus.Approved).WithEntityId(created.EntityId),
            created.Version,
            new ChangeInfo { Reason = ChangeReason.ReExRefresh },
            cancellationToken: Ct
        );

        var response = await client.GetAsync(StatusPath, Ct);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await Json(response);
        body.GetProperty("registrationId").GetString().Should().Be(ReExRegistrationId);
        body.GetProperty("registrationNumber").GetString().Should().Be("R25ER0500036908PL");
        body.GetProperty("status").GetString().Should().Be("Approved");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NoRegistrationRecord_ReturnsNotFound(bool expandEnabled)
    {
        using var factory = Factory(expandEnabled);
        using var client = FrontendClient(factory);

        var response = await client.GetAsync(StatusPath, Ct);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task OnlyAnOlderVersionForTheRegistration_ReturnsNotFound()
    {
        using var factory = Factory(expandEnabled: true);
        using var client = FrontendClient(factory);
        var versions = Versions(factory);
        var created = await versions.InsertAsync(
            Registration(RegistrationStatus.Approved),
            new ChangeInfo { Reason = ChangeReason.ReExImport },
            cancellationToken: Ct
        );
        var moved = Registration(RegistrationStatus.Approved).WithEntityId(created.EntityId);
        moved.ReExRegistrationId = "some-other-registration";
        await versions.UpdateAsync(
            moved,
            created.Version,
            new ChangeInfo { Reason = ChangeReason.ReExRefresh },
            cancellationToken: Ct
        );

        var response = await client.GetAsync(StatusPath, Ct);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    // Off, the route does not exist, even for a registration that has a current version.
    [Fact]
    public async Task InYearChangesFlagOff_ReturnsNotFound()
    {
        using var factory = Factory(expandEnabled: true, inYearChangesEnabled: false);
        using var client = FrontendClient(factory);
        await Versions(factory)
            .InsertAsync(
                Registration(RegistrationStatus.Approved),
                new ChangeInfo { Reason = ChangeReason.ReExImport },
                cancellationToken: Ct
            );

        var response = await client.GetAsync(StatusPath, Ct);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task WithoutTheFrontendSecret_IsRejected()
    {
        using var factory = Factory(expandEnabled: true);
        using var client = factory.CreateClient();

        var response = await client.GetAsync(StatusPath, Ct);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task WithTheWrongSecret_IsRejected()
    {
        using var factory = Factory(expandEnabled: true);
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            "integration-management-be-secret"
        );

        var response = await client.GetAsync(StatusPath, Ct);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }
}

internal static class RegistrationEntityTestExtensions
{
    public static RegistrationEntity WithEntityId(this RegistrationEntity entity, ObjectId entityId)
    {
        entity.EntityId = entityId;
        return entity;
    }
}
