using System.Net;
using System.Net.Http.Json;
using EprRegisterEnrolBackend.AccreditationApplication.Models;
using EprRegisterEnrolBackend.ReEx;
using EprRegisterEnrolBackend.Utils.Mongo;
using Microsoft.Extensions.DependencyInjection;
using MongoDB.Bson;
using MongoDB.Driver;
using NSubstitute;

namespace EprRegisterEnrolBackend.Test.AccreditationApplication.Endpoints;

/// <summary>
/// RA-705: GetById backfills OrgId and Nation from ReEx the first time an application without them is read. Both are
/// projected from the model (OrgId from the Organisation's number, Nation from the Registration), so with dual-write on
/// the backfill has to reach the model in the same transaction as legacy. Otherwise a read from the model after the
/// switch would show the operator no organisation number, or the wrong nation's bank details and payment reference
/// (RA-526).
/// </summary>
public sealed partial class DualWriteEndpointTests
{
    private const string LegacyCollection = "accreditationApplications";

    // ReEx knows nothing, so GetById's backfills leave the application as it is. A test stubs the one call it needs after.
    private void ReExKnowsNothing()
    {
        _reEx
            .GetOrganisationNumberAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(ReExResult<int?>.Fail(new ReExError(ReExErrorKind.ServerError, "down"), 500));
        _reEx
            .GetNationAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(
                ReExResult<Nation>.Fail(new ReExError(ReExErrorKind.ServerError, "down"), 500)
            );
    }

    private Task UnsetLegacyAsync(
        AccreditationApplicationModel application,
        params string[] fields
    ) =>
        _factory
            .Services.GetRequiredService<IMongoDbClientFactory>()
            .GetCollection<BsonDocument>(LegacyCollection)
            .UpdateOneAsync(
                Builders<BsonDocument>.Filter.Eq("_id", application.Id!.Value),
                Builders<BsonDocument>.Update.Combine(
                    fields.Select(f => Builders<BsonDocument>.Update.Unset(f))
                ),
                cancellationToken: Ct
            );

    // Migrated (by its first dual-written write) while legacy still has no nation or organisation number.
    private async Task<AccreditationApplicationModel> MigratedWithoutNationOrOrgIdAsync()
    {
        var application = await ApplicationAsync(null, ReExSite());
        await UnsetLegacyAsync(application, "nation", "orgId");
        var added = await _client.PostAsJsonAsync(Url(application), AddRequest(), Ct);
        Assert.Equal(HttpStatusCode.Created, added.StatusCode);
        Assert.Null((await RegistrationOfAsync(application)).Nation);
        return application;
    }

    private Task<HttpResponseMessage> GetByIdAsync(AccreditationApplicationModel application) =>
        _client.GetAsync(
            $"/api/v1/accreditation-applications/{OrganisationId}/{application.Id}",
            Ct
        );

    [Fact]
    public async Task GetById_NationBackfill_ReachesTheModelRegistration()
    {
        var application = await MigratedWithoutNationOrOrgIdAsync();
        ReExKnowsNothing();
        _reEx
            .GetNationAsync(OrganisationId, ReExRegistrationId, Arg.Any<CancellationToken>())
            .Returns(ReExResult<Nation>.Success(Nation.Wales, 200));

        var response = await GetByIdAsync(application);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(Nation.Wales, (await LegacyAsync(application))!.Nation);
        Assert.Equal(Nation.Wales, (await RegistrationOfAsync(application)).Nation);
    }

    [Fact]
    public async Task GetById_OrgIdBackfill_ReachesTheModelOrganisation()
    {
        var application = await MigratedWithoutNationOrOrgIdAsync();
        ReExKnowsNothing();
        _reEx
            .GetOrganisationNumberAsync(OrganisationId, Arg.Any<CancellationToken>())
            .Returns(ReExResult<int?>.Success(500123, 200));

        var response = await GetByIdAsync(application);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(500123, (await LegacyAsync(application))!.OrgId);
        using var session = await _factory
            .Services.GetRequiredService<IMongoDbClientFactory>()
            .GetClient()
            .StartSessionAsync(cancellationToken: Ct);
        var organisation = await Stores.Organisations.FindByReExOrganisationIdAsync(
            OrganisationId,
            session,
            Ct
        );
        Assert.Equal(500123, organisation!.OrganisationNumber);
    }

    // A lookup failure leaves both sides as they were, so the next read retries.
    [Fact]
    public async Task GetById_NationLookupFails_ChangesNeitherSide()
    {
        var application = await MigratedWithoutNationOrOrgIdAsync();
        ReExKnowsNothing();
        _reEx
            .GetNationAsync(OrganisationId, ReExRegistrationId, Arg.Any<CancellationToken>())
            .Returns(
                ReExResult<Nation>.Fail(new ReExError(ReExErrorKind.ServerError, "down"), 500)
            );

        var response = await GetByIdAsync(application);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Null((await LegacyAsync(application))!.Nation);
        Assert.Null((await RegistrationOfAsync(application)).Nation);
    }
}
