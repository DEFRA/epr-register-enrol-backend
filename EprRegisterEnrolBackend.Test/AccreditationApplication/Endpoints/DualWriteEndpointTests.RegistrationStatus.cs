using System.Net;
using System.Net.Http.Json;
using EprRegisterEnrolBackend.AccreditationApplication.Adapters;
using EprRegisterEnrolBackend.DomainModel.Persistence;
using EprRegisterEnrolBackend.Utils.Mongo;
using EprRegisterEnrolBackend.DomainModel.Entities;
using EprRegisterEnrolBackend.Organisation.Models;
using EprRegisterEnrolBackend.Organisation.Services;
using EprRegisterEnrolBackend.Test.Utils.Logging;
using Microsoft.Extensions.DependencyInjection;
using MongoDB.Bson;
using MongoDB.Driver;

namespace EprRegisterEnrolBackend.Test.AccreditationApplication.Endpoints;

/// <summary>
/// RA-806: starting an application writes the Registration with ReEx's status for it, end to end over HTTP through the
/// stub ReEx that local and fe-tests stacks use, for its dedicated orgs 50023 (approved) and 50024 (created). A missing
/// or unknown ReEx status fails the seed, as the adapters' other bad-ReEx-data failures do, and writes nothing.
/// </summary>
public sealed partial class DualWriteEndpointTests
{
    private void RestartWithStubReEx(FakeOrganisationPersistence fakeOrgs) =>
        Restart(
            s_seedOnly,
            services =>
                services.AddSingleton<IReExApiAdapter>(
                    new StubReExApiAdapter(fakeOrgs, EnabledNullLogger<StubReExApiAdapter>.Instance)
                )
        );

    private Task<HttpResponseMessage> SeedPlasticAsync(int orgId, ObjectId reExRegistrationId, int year) =>
        _client.PostAsJsonAsync(
            $"/api/v1/accreditation-applications/{orgId}/{reExRegistrationId}/Plastic/seed",
            new { year },
            Ct
        );

    private Task<List<RegistrationEntity>> RegistrationVersionsAsync(ObjectId reExRegistrationId) =>
        _factory
            .Services.GetRequiredService<IMongoDbClientFactory>()
            .GetCollection<RegistrationEntity>(DomainModelCollections.RegistrationVersions)
            .Find(r => r.ReExRegistrationId == reExRegistrationId.ToString())
            .ToListAsync(Ct);

    private async Task<RegistrationEntity> SeededRegistrationAsync(
        int orgId,
        ObjectId reExRegistrationId
    )
    {
        var response = await SeedPlasticAsync(orgId, reExRegistrationId, 2027);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return Assert.Single(await RegistrationVersionsAsync(reExRegistrationId));
    }

    private static async Task SetFixtureRegistrationStatusAsync(
        FakeOrganisationPersistence fakeOrgs,
        int orgId,
        string status
    )
    {
        var organisation = (await fakeOrgs.GetByOrgIdAsync(orgId))!;
        Assert.Single(organisation.Registrations!).Status = status;
        Assert.True(await fakeOrgs.UpdateAsync(organisation));
    }

    [Fact]
    public async Task Seed_StubReExRegistrationApproved_WritesTheRegistrationApproved()
    {
        RestartWithStubReEx(new FakeOrganisationPersistence());

        var registration = await SeededRegistrationAsync(
            50023,
            FakeOrganisationPersistence.Reg50023
        );

        Assert.Equal(RegistrationStatus.Approved, registration.Status);
        // The number the fe-tests manage page URL uses for a 2027 application.
        Assert.Equal("R26EX0500230938PL", registration.RegistrationNumber);
    }

    [Fact]
    public async Task Seed_StubReExRegistrationCreated_WritesTheRegistrationCreated()
    {
        RestartWithStubReEx(new FakeOrganisationPersistence());

        var registration = await SeededRegistrationAsync(
            50024,
            FakeOrganisationPersistence.Reg50024
        );

        Assert.Equal(RegistrationStatus.Created, registration.Status);
        Assert.Equal("R26EX0500242843PL", registration.RegistrationNumber);
    }

    [Theory]
    [InlineData("suspended")]
    [InlineData("")]
    public async Task Seed_ReExRegistrationStatusMissingOrUnrecognised_FailsAndWritesNoRegistration(
        string rawStatus
    )
    {
        var fakeOrgs = new FakeOrganisationPersistence();
        await SetFixtureRegistrationStatusAsync(fakeOrgs, 50023, rawStatus);
        RestartWithStubReEx(fakeOrgs);

        var response = await SeedPlasticAsync(50023, FakeOrganisationPersistence.Reg50023, 2027);

        // What the seed returns for the adapter's other bad-ReEx-data failures (a ClientError result).
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Empty(await RegistrationVersionsAsync(FakeOrganisationPersistence.Reg50023));
    }

    // A re-seed with a bad status must not keep serving the held Approved as if ReEx still said so: the seed fails, and
    // the Registration it already wrote is left as it was, with no new version.
    [Fact]
    public async Task Seed_ReSeedOfAnApprovedRegistrationWithAnUnrecognisedStatus_FailsAndLeavesItApproved()
    {
        var fakeOrgs = new FakeOrganisationPersistence();
        RestartWithStubReEx(fakeOrgs);
        await SeededRegistrationAsync(50023, FakeOrganisationPersistence.Reg50023);
        await SetFixtureRegistrationStatusAsync(fakeOrgs, 50023, "suspended");

        // A later year, as the live 2027 application would answer a repeat 2027 seed without asking ReEx.
        var response = await SeedPlasticAsync(50023, FakeOrganisationPersistence.Reg50023, 2028);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        var registration = Assert.Single(
            await RegistrationVersionsAsync(FakeOrganisationPersistence.Reg50023)
        );
        Assert.Equal(RegistrationStatus.Approved, registration.Status);
        Assert.Equal(1, registration.Version);
    }
}
