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
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using MongoDB.Bson;

namespace EprRegisterEnrolBackend.Test.Registrations;

// RA-425: the frontend asks for a registration's current status before showing any of the
// material dashboard pages. Driven through the real host and auth pipeline against a real ephemeral
// Mongo, with the domain model's Expand flag both off and on. Mapped only with
// FeatureFlags:InYearChangesEnabled. The Expand-on runs, including every auth test, are outside
// Development, where the Frontend scheme's bypass would hide a missing gate. The Expand-off runs use
// Development: elsewhere the flag needs DomainModel:DualWrite, which needs Expand.
public sealed class RegistrationStatusEndpointTests(MongoIntegrationFixture fixture)
{
    private const string FrontendSecret = "integration-frontend-secret";
    private static readonly string ReExRegistrationId =
        FakeOrganisationPersistence.Reg50003.ToString();
    private static readonly string StatusPath = $"/api/v1/registrations/{ReExRegistrationId}/status";
    // RA-697: organisationId is required; FakeOrganisationPersistence's org 50003 holds the registration.
    private static readonly string HeldStatusPath = $"{StatusPath}?organisationId=50003";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private EphemeralMongoTestFactory Factory(
        bool expandEnabled,
        bool inYearChangesEnabled = true,
        bool? dualWrite = null
    ) =>
        new(
            fixture,
            "registration_status",
            expandEnabled ? Environments.Production : Environments.Development,
            new Dictionary<string, string?>
            {
                ["AUTH_SHARED_SECRET:FRONTEND"] = FrontendSecret,
                ["AUTH_SHARED_SECRET:MANAGEMENT_BE"] = "integration-management-be-secret",
                ["CaseWorking:UseStub"] = "true",
                // RA-697: the organisation name comes from the stub ReEx registry's org 50003.
                ["ReExApi:UseStub"] = "true",
                ["DomainModel:ExpandEnabled"] = expandEnabled ? "true" : "false",
                ["DomainModel:DualWrite"] = (dualWrite ?? expandEnabled) ? "true" : "false",
                [FeatureFlags.InYearChangesEnabledKey] = inYearChangesEnabled ? "true" : "false",
            }
        );

    private static HttpClient FrontendClient(WebApplicationFactory<Program> factory)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            FrontendSecret
        );
        return client;
    }

    private static VersionedMongoRepository<RegistrationEntity> Versions(
        WebApplicationFactory<Program> factory
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

        var response = await client.GetAsync(HeldStatusPath, Ct);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await Json(response);
        body.GetProperty("registrationId").GetString().Should().Be(ReExRegistrationId);
        body.GetProperty("registrationNumber").GetString().Should().Be("R25ER0500036908PL");
        body.GetProperty("status").GetString().Should().Be("Approved");
    }

    // RA-697: a registration-only operator's manage dashboard is built from these, as there is no
    // accreditation to read them from.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ForAnExporter_ReturnsTheOrganisationNameMaterialAndNoSiteAddress(
        bool expandEnabled
    )
    {
        using var factory = Factory(expandEnabled);
        using var client = FrontendClient(factory);
        var exporter = Registration(RegistrationStatus.Approved);
        exporter.WasteProcessingType = WasteProcessingKind.Exporter;
        await Versions(factory)
            .InsertAsync(
                exporter,
                new ChangeInfo { Reason = ChangeReason.ReExImport },
                cancellationToken: Ct
            );

        var response = await client.GetAsync(HeldStatusPath, Ct);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await Json(response);
        body.GetProperty("organisationName").GetString().Should().Be("Delta Green Ltd");
        body.GetProperty("materialType").GetString().Should().Be("Plastic");
        body.GetProperty("glassRecyclingProcess").ValueKind.Should().Be(JsonValueKind.Null);
        body.GetProperty("isExporter").GetBoolean().Should().BeTrue();
        body.GetProperty("siteAddress").ValueKind.Should().Be(JsonValueKind.Null);
        body.GetProperty("registrationNumber").GetString().Should().Be("R25ER0500036908PL");
        body.GetProperty("status").GetString().Should().Be("Approved");
    }

    [Fact]
    public async Task ForAReprocessor_ReturnsItsMaterialAndSiteAddress()
    {
        using var factory = Factory(expandEnabled: true);
        using var client = FrontendClient(factory);
        var reprocessor = Registration(RegistrationStatus.Approved);
        reprocessor.Material = MaterialType.Glass;
        reprocessor.GlassRecyclingProcess = GlassRecyclingProcess.Remelt;
        reprocessor.Site = new RegistrationSite
        {
            Address = new PostalAddress
            {
                Line1 = "Green Works",
                TownOrCity = "Leeds",
                Postcode = "LS10 1AB",
            },
        };
        await Versions(factory)
            .InsertAsync(
                reprocessor,
                new ChangeInfo { Reason = ChangeReason.ReExImport },
                cancellationToken: Ct
            );

        var response = await client.GetAsync(HeldStatusPath, Ct);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await Json(response);
        body.GetProperty("organisationName").GetString().Should().Be("Delta Green Ltd");
        body.GetProperty("materialType").GetString().Should().Be("Glass");
        body.GetProperty("glassRecyclingProcess").GetString().Should().Be("glass_re_melt");
        body.GetProperty("isExporter").GetBoolean().Should().BeFalse();
        body.GetProperty("siteAddress").GetString().Should().Be("Green Works, Leeds, LS10 1AB");
    }

    // A registration-only page shows these under the URL's organisation, so it must hold the registration.
    [Theory]
    [InlineData("50011")]
    [InlineData("99999")]
    public async Task ForAnOrganisationThatDoesNotHoldTheRegistration_ReturnsNotFound(
        string organisationId
    )
    {
        using var factory = Factory(expandEnabled: true);
        using var client = FrontendClient(factory);
        await Versions(factory)
            .InsertAsync(
                Registration(RegistrationStatus.Approved),
                new ChangeInfo { Reason = ChangeReason.ReExImport },
                cancellationToken: Ct
            );

        var response = await client.GetAsync($"{StatusPath}?organisationId={organisationId}", Ct);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    // As an accreditation application's site address, which has no Line2 or Region.
    [Fact]
    public async Task SiteAddress_IsLine1TownAndPostcode()
    {
        using var factory = Factory(expandEnabled: true);
        using var client = FrontendClient(factory);
        var reprocessor = Registration(RegistrationStatus.Approved);
        reprocessor.Site = new RegistrationSite
        {
            Address = new PostalAddress
            {
                Line1 = "Green Works",
                Line2 = "Unit 4",
                TownOrCity = "Leeds",
                Region = "West Yorkshire",
                Postcode = "LS10 1AB",
            },
        };
        await Versions(factory)
            .InsertAsync(
                reprocessor,
                new ChangeInfo { Reason = ChangeReason.ReExImport },
                cancellationToken: Ct
            );

        var response = await client.GetAsync(HeldStatusPath, Ct);

        (await Json(response)).GetProperty("siteAddress").GetString()
            .Should()
            .Be("Green Works, Leeds, LS10 1AB");
    }

    // Any other ReEx failure leaves the organisation name out rather than failing the status, which every manage
    // page needs.
    [Fact]
    public async Task ReExFailing_ReturnsTheStatusWithNoOrganisationName()
    {
        using var baseFactory = Factory(expandEnabled: true);
        using var factory = baseFactory.WithReExFailing();
        using var client = FrontendClient(factory);
        await Versions(factory)
            .InsertAsync(
                Registration(RegistrationStatus.Approved),
                new ChangeInfo { Reason = ChangeReason.ReExImport },
                cancellationToken: Ct
            );

        var response = await client.GetAsync(HeldStatusPath, Ct);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await Json(response);
        body.GetProperty("organisationName").ValueKind.Should().Be(JsonValueKind.Null);
        body.GetProperty("status").GetString().Should().Be("Approved");
    }

    // RA-697: without the organisation there is no ReEx check that it holds the registration.
    [Theory]
    [InlineData("")]
    [InlineData("?organisationId=")]
    [InlineData("?organisationId=%20")]
    public async Task WithoutAnOrganisationId_ReturnsBadRequest(string query)
    {
        using var factory = Factory(expandEnabled: true);
        using var client = FrontendClient(factory);
        await Versions(factory)
            .InsertAsync(
                Registration(RegistrationStatus.Approved),
                new ChangeInfo { Reason = ChangeReason.ReExImport },
                cancellationToken: Ct
            );

        var response = await client.GetAsync($"{StatusPath}{query}", Ct);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NoRegistrationRecord_ReturnsNotFound(bool expandEnabled)
    {
        using var factory = Factory(expandEnabled);
        using var client = FrontendClient(factory);

        var response = await client.GetAsync(HeldStatusPath, Ct);

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

        var response = await client.GetAsync(HeldStatusPath, Ct);

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

        var response = await client.GetAsync(HeldStatusPath, Ct);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    // On CDP only dual-write fills the domain model these routes read, so without it the host refuses to
    // start rather than serve empty pages.
    [Fact]
    public void InYearChangesWithoutDualWrite_RefusesToStart()
    {
        using var factory = Factory(expandEnabled: true, dualWrite: false);

        var act = () => factory.CreateClient();

        act.Should()
            .Throw<InvalidOperationException>()
            .WithMessage("FeatureFlags:InYearChangesEnabled needs DomainModel:DualWrite*");
    }

    [Fact]
    public async Task WithoutTheFrontendSecret_IsRejected()
    {
        using var factory = Factory(expandEnabled: true);
        using var client = factory.CreateClient();

        var response = await client.GetAsync(HeldStatusPath, Ct);

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

        var response = await client.GetAsync(HeldStatusPath, Ct);

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
