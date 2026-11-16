using System.Net;
using System.Net.Http.Json;
using EprRegisterEnrolBackend.AccreditationApplication.Models;
using EprRegisterEnrolBackend.DomainModel.DualWrite;
using EprRegisterEnrolBackend.DomainModel.Versioning;
using EprRegisterEnrolBackend.ReEx;
using EprRegisterEnrolBackend.Test.DomainModel.DualWrite;
using EprRegisterEnrolBackend.Utils.Mongo;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
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

        var before = await RegistrationOfAsync(application);

        var response = await GetByIdAsync(application);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(Nation.Wales, (await LegacyAsync(application))!.Nation);
        var after = await RegistrationOfAsync(application);
        Assert.Equal(Nation.Wales, after.Nation);
        Assert.Equal(before.EntityId, after.EntityId);
        Assert.Equal(before.Version + 1, after.Version);
        Assert.Equal(ChangeReason.ReExRefresh, after.LastChange.Reason);
        Assert.Equal(application.Id, after.LastChange.ApplicationId);
    }

    // The model's Registration keeps a nation it already holds: the backfill fills a gap, it never overwrites.
    [Fact]
    public async Task GetById_ModelAlreadyHoldsANation_KeepsItAndWritesNoVersion()
    {
        var application = await MigratedWithoutNationOrOrgIdAsync();
        var registration = await RegistrationOfAsync(application);
        registration.Nation = Nation.England;
        await Stores.Registrations.Versions.UpdateAsync(
            registration,
            registration.Version,
            new ChangeInfo { Reason = ChangeReason.ReExRefresh },
            cancellationToken: Ct
        );
        var held = await RegistrationOfAsync(application);
        ReExKnowsNothing();
        _reEx
            .GetNationAsync(OrganisationId, ReExRegistrationId, Arg.Any<CancellationToken>())
            .Returns(ReExResult<Nation>.Success(Nation.Wales, 200));

        var response = await GetByIdAsync(application);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(Nation.Wales, (await LegacyAsync(application))!.Nation);
        var after = await RegistrationOfAsync(application);
        Assert.Equal(Nation.England, after.Nation);
        Assert.Equal(held.Version, after.Version);
    }

    // A GET with nothing to backfill calls no one and writes nothing, on either side.
    [Fact]
    public async Task GetById_NothingToBackfill_WritesNoVersionOnEitherSide()
    {
        var application = await MigratedWithoutNationOrOrgIdAsync();
        ReExKnowsNothing();
        _reEx
            .GetNationAsync(OrganisationId, ReExRegistrationId, Arg.Any<CancellationToken>())
            .Returns(ReExResult<Nation>.Success(Nation.Wales, 200));
        _reEx
            .GetOrganisationNumberAsync(OrganisationId, Arg.Any<CancellationToken>())
            .Returns(ReExResult<int?>.Success(500123, 200));
        Assert.Equal(HttpStatusCode.OK, (await GetByIdAsync(application)).StatusCode);
        var legacy = (await LegacyAsync(application))!;
        var registration = await RegistrationOfAsync(application);
        var entity = (await CurrentApplicationAsync(application))!;
        _reEx.ClearReceivedCalls();

        var response = await GetByIdAsync(application);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        await _reEx.DidNotReceiveWithAnyArgs().GetNationAsync(default!, default!, Ct);
        await _reEx.DidNotReceiveWithAnyArgs().GetOrganisationNumberAsync(default!, Ct);
        Assert.Equal(legacy.Version, (await LegacyAsync(application))!.Version);
        Assert.Equal(registration.Version, (await RegistrationOfAsync(application)).Version);
        Assert.Equal(entity.Version, (await CurrentApplicationAsync(application))!.Version);
    }

    // A failed save rolls both sides back but still answers with what ReEx resolved, as before RA-705: the frontend no
    // longer falls back to postcode, so a GET answering no nation would show the wrong nation's bank details (RA-526).
    // The next read retries the save.
    [Fact]
    public async Task GetById_BackfillSaveFails_StillServesTheResolvedValuesAndPersistsNothing()
    {
        var application = await MigratedWithoutNationOrOrgIdAsync();
        ReExKnowsNothing();
        _reEx
            .GetNationAsync(OrganisationId, ReExRegistrationId, Arg.Any<CancellationToken>())
            .Returns(ReExResult<Nation>.Success(Nation.Wales, 200));
        _reEx
            .GetOrganisationNumberAsync(OrganisationId, Arg.Any<CancellationToken>())
            .Returns(ReExResult<int?>.Success(500123, 200));
        IApplicationModelSync failing = new FailingModelSync();
        var (failingFactory, failingClient) = Alongside(
            s_dualWriteOn,
            services => services.Replace(ServiceDescriptor.Singleton(failing))
        );
        using var _ = failingFactory;

        var response = await failingClient.GetAsync(
            $"/api/v1/accreditation-applications/{OrganisationId}/{application.Id}",
            Ct
        );

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await JsonAsync(response);
        Assert.Equal("Wales", body.GetProperty("nation").GetString());
        Assert.Equal(500123, body.GetProperty("orgId").GetInt32());
        var legacy = (await LegacyAsync(application))!;
        Assert.Null(legacy.Nation);
        Assert.Null(legacy.OrgId);
        Assert.Null((await RegistrationOfAsync(application)).Nation);
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
