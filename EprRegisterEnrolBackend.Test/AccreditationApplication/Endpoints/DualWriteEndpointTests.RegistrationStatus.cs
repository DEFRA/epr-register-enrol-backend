using System.Net;
using System.Net.Http.Json;
using EprRegisterEnrolBackend.AccreditationApplication.Adapters;
using EprRegisterEnrolBackend.DomainModel.Entities;
using EprRegisterEnrolBackend.Organisation.Services;
using EprRegisterEnrolBackend.Test.Utils.Logging;
using Microsoft.Extensions.DependencyInjection;
using MongoDB.Bson;
using MongoDB.Driver;

namespace EprRegisterEnrolBackend.Test.AccreditationApplication.Endpoints;

/// <summary>
/// RA-806: starting an application writes the Registration with ReEx's status for it, end to end over HTTP through the
/// stub ReEx that local and fe-tests stacks use, for its dedicated orgs 50023 (approved) and 50024 (created).
/// </summary>
public sealed partial class DualWriteEndpointTests
{
    private void RestartWithStubReEx() =>
        Restart(
            s_seedOnly,
            services =>
                services.AddSingleton<IReExApiAdapter>(
                    new StubReExApiAdapter(
                        new FakeOrganisationPersistence(),
                        EnabledNullLogger<StubReExApiAdapter>.Instance
                    )
                )
        );

    private async Task<RegistrationEntity?> SeededRegistrationAsync(
        int orgId,
        ObjectId reExRegistrationId
    )
    {
        var response = await _client.PostAsJsonAsync(
            $"/api/v1/accreditation-applications/{orgId}/{reExRegistrationId}/Plastic/seed",
            new { year = 2027 },
            Ct
        );
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return await SeedStores.Registrations.Versions.FindCurrentAsync(
            Builders<RegistrationEntity>.Filter.Eq(
                r => r.ReExRegistrationId,
                reExRegistrationId.ToString()
            ),
            cancellationToken: Ct
        );
    }

    [Fact]
    public async Task Seed_StubReExRegistrationApproved_WritesTheRegistrationApproved()
    {
        RestartWithStubReEx();

        var registration = await SeededRegistrationAsync(
            50023,
            FakeOrganisationPersistence.Reg50023
        );

        Assert.NotNull(registration);
        Assert.Equal(RegistrationStatus.Approved, registration.Status);
        // The number the fe-tests manage page URL uses for a 2027 application.
        Assert.Equal("R26EX0500230938PL", registration.RegistrationNumber);
    }

    [Fact]
    public async Task Seed_StubReExRegistrationCreated_WritesTheRegistrationCreated()
    {
        RestartWithStubReEx();

        var registration = await SeededRegistrationAsync(
            50024,
            FakeOrganisationPersistence.Reg50024
        );

        Assert.NotNull(registration);
        Assert.Equal(RegistrationStatus.Created, registration.Status);
        Assert.Equal("R26EX0500242843PL", registration.RegistrationNumber);
    }
}
