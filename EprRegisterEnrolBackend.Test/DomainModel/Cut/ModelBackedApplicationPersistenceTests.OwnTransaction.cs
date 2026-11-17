using EprRegisterEnrolBackend.AccreditationApplication.Models;
using EprRegisterEnrolBackend.DomainModel.Persistence;
using MongoDB.Bson;
using MongoDB.Driver;

namespace EprRegisterEnrolBackend.Test.DomainModel.Cut;

/// <summary>
/// Writes outside a unit of work (seed's insert, the dev seeds, writes with no session) run in their own transaction
/// and flush themselves, so the model holds the change, legacy doesn't, and nothing is left staged. A new application
/// is migrated into the model by that flush.
/// </summary>
public sealed partial class ModelBackedApplicationPersistenceTests
{
    private Task<long> LegacyCountAsync() =>
        _factory
            .GetClient()
            .GetDatabase(_databaseName)
            .GetCollection<BsonDocument>(DomainModelCollections.LegacyApplications)
            .CountDocumentsAsync(FilterDefinition<BsonDocument>.Empty, cancellationToken: Ct);

    private async Task<AccreditationApplicationModel> ReadAsync(
        AccreditationApplicationModel application
    ) => (await _model.GetByIdAsync(application.OrganisationId, application.Id!.Value.ToString()))!;

    [Fact]
    public async Task Update_WithoutASession_FlushesItself()
    {
        var migrated = await MigratedAsync();
        var read = await ReadAsync(migrated);
        read.PaymentReference = "PAY-9";

        var updated = await _model.UpdateAsync(read);

        Assert.NotNull(updated);
        Assert.Equal("PAY-9", (await ReadAsync(migrated)).PaymentReference);
        var legacy = (
            await _legacy.GetByIdAsync(migrated.OrganisationId, migrated.Id!.Value.ToString())
        )!;
        Assert.Equal(migrated.PaymentReference, legacy.PaymentReference);
        Assert.Equal(0, await StagedCountAsync());
    }

    // RA-705 decision M: the model keeps ReEx's validFrom as received. After the cut the model is the only copy, so a
    // write must carry the string through the staged copy and the sync, not re-format it from the date.
    [Fact]
    public async Task Approve_ReExApprovedSite_KeepsItsValidFromAsReceived()
    {
        var application = NewApplication();
        application.OverseasSites!.Sites[0].ValidFrom = "2026-01-01";
        var migrated = await MigratedAsync(application);
        var read = await ReadAsync(migrated);
        // Approval rebuilds each selected site's ORS from the application's copy (B9).
        read.ApplicationStatus = ApplicationStatus.Approved;
        read.CaseManagementStatusUpdatedAt = Created.AddDays(1);
        read.AccreditationReference = "A27EX1234567P";
        foreach (var site in read.OverseasSites!.Sites)
        {
            site.Selected = true;
        }

        Assert.NotNull(await _model.UpdateAsync(read));

        Assert.Equal(
            "2026-01-01",
            (await ReadAsync(migrated)).OverseasSites!.Sites.Single(s => s.SiteId == 1).ValidFrom
        );
    }

    // 3bb.28: an application's approval (re-accreditation included) gives a site with no date its first approved date
    // here, but legacy never writes it onto the site, so the model serves no validFrom either.
    [Fact]
    public async Task Approve_ASiteWithNoDate_ServesNoValidFromAsLegacyHoldsNone()
    {
        var migrated = await MigratedAsync(NewApplication());
        var read = await ReadAsync(migrated);
        read.ApplicationStatus = ApplicationStatus.Approved;
        read.CaseManagementStatusUpdatedAt = Created.AddDays(1);
        read.AccreditationReference = "A27EX1234567P";
        foreach (var site in read.OverseasSites!.Sites)
        {
            site.Selected = true;
        }

        Assert.NotNull(await _model.UpdateAsync(read));

        Assert.All((await ReadAsync(migrated)).OverseasSites!.Sites, site => Assert.Null(site.ValidFrom));
    }

    [Fact]
    public async Task Update_WithoutASession_StaleCopy_IsRefusedAndChangesNothing()
    {
        var migrated = await MigratedAsync();
        var first = await ReadAsync(migrated);
        var stale = await ReadAsync(migrated);
        first.PaymentReference = "PAY-1";
        await _model.UpdateAsync(first);
        stale.PaymentReference = "PAY-2";

        Assert.Null(await _model.UpdateAsync(stale));
        Assert.Equal("PAY-1", (await ReadAsync(migrated)).PaymentReference);
        Assert.Equal(0, await StagedCountAsync());
    }

    [Fact]
    public async Task UpdateFields_WithoutASession_FlushesItself()
    {
        var migrated = await MigratedAsync();

        var updated = await _model.UpdateFieldsAsync(
            migrated.Id!.Value,
            null,
            Builders<AccreditationApplicationModel>.Update.Set(a => a.PaymentReference, "PAY-7"),
            cancellationToken: Ct
        );

        Assert.Equal("PAY-7", updated!.PaymentReference);
        Assert.Equal("PAY-7", (await ReadAsync(migrated)).PaymentReference);
        Assert.Equal(0, await StagedCountAsync());
    }

    // Seed's insert after the cut: the new application joins its registration in the model.
    [Fact]
    public async Task Create_UnderARegistrationTheModelHolds_IsMigratedIntoTheModelAndNotLegacy()
    {
        await MigratedAsync();
        var legacyBefore = await LegacyCountAsync();
        var seeded = NewApplication();
        seeded.Year = 2028;
        seeded.CreatedAt = Created.AddDays(10);

        var created = await _model.CreateAsync(seeded);

        Assert.Equal(seeded.Id, created!.Id);
        var model = await ReadAsync(seeded);
        Assert.Equal(2028, model.Year);
        Assert.Equal(Created.AddDays(10), model.CreatedAt);
        Assert.NotNull(await CurrentVersionAsync(seeded));
        Assert.Equal(legacyBefore, await LegacyCountAsync());
        Assert.Equal(0, await StagedCountAsync());
    }

    // Seed creates its application without an id and relies on the insert to assign one.
    [Fact]
    public async Task Create_WithoutAnId_IsGivenOneAndMigrated()
    {
        await MigratedAsync();
        var seeded = NewApplication();
        seeded.Id = null;
        seeded.Year = 2028;

        var created = await _model.CreateAsync(seeded);

        Assert.NotNull(created!.Id);
        Assert.Equal(2028, (await ReadAsync(created)).Year);
        Assert.Equal(0, await StagedCountAsync());
    }

    [Fact]
    public async Task Create_ForAnOrganisationTheModelDoesNotHoldYet_CreatesItInTheModel()
    {
        var seeded = NewApplication(OtherOrganisationId);
        seeded.RegistrationId = OtherRegistrationId;
        seeded.RegistrationReference = "R27EX0000001P";

        await _model.CreateAsync(seeded);

        var model = await ReadAsync(seeded);
        Assert.Equal("Acme Recycling Ltd", model.OrganisationName);
        Assert.Equal("R27EX0000001P", model.RegistrationReference);
        Assert.Equal(0, await LegacyCountAsync());
    }

    [Fact]
    public async Task Upsert_ExistingApplication_ReplacesItInTheModel()
    {
        var migrated = await MigratedAsync();
        var replacement = await ReadAsync(migrated);
        replacement.PaymentReference = "PAY-U";

        await _model.UpsertAsync(replacement);

        Assert.Equal("PAY-U", (await ReadAsync(migrated)).PaymentReference);
        Assert.Equal(0, await StagedCountAsync());
    }

    [Fact]
    public async Task Upsert_NewApplication_CreatesItInTheModel()
    {
        await MigratedAsync();
        var upserted = NewApplication();
        upserted.Year = 2028;

        await _model.UpsertAsync(upserted);

        Assert.Equal(2028, (await ReadAsync(upserted)).Year);
        Assert.Equal(0, await StagedCountAsync());
    }

    // The migrator finds a new application's group through this lookup, so in a transaction it must see a staged
    // application the model doesn't hold yet.
    [Fact]
    public async Task GetRegistrationGroup_InATransaction_IncludesAStagedNewApplication()
    {
        var migrated = await MigratedAsync();
        var seeded = NewApplication();
        seeded.Year = 2028;
        using var session = await TransactionAsync();
        await _staging.StageAsync(seeded, session, Ct);

        var group = await _model.GetRegistrationGroupAsync(
            ReExOrganisationId,
            ReExRegistrationId,
            50,
            session,
            Ct
        );

        Assert.Equal(
            new[] { migrated.Id, seeded.Id }.OrderBy(id => id),
            group.Select(a => a.Id).OrderBy(id => id)
        );
        await session.AbortTransactionAsync(Ct);
    }

    // Foreign-key stamps only ever mattered on legacy documents; the model-backed copy carries the model's own keys.
    [Fact]
    public async Task StampForeignKeys_StagesNothing()
    {
        var migrated = await MigratedAsync();
        using var session = await TransactionAsync();

        await _model.StampDomainModelForeignKeysAsync(
            migrated.Id!.Value,
            ObjectId.GenerateNewId(),
            new Dictionary<string, ObjectId>(),
            null,
            session,
            Ct
        );

        Assert.Empty(_staging.StagedIds(session));
        await session.AbortTransactionAsync(Ct);
    }

    // A newly created application's staged copy has no model keys yet; later reads in the transaction return that copy,
    // so the migration's stamp must reach it.
    [Fact]
    public async Task StampForeignKeys_OnAStagedCopy_StampsIt()
    {
        var seeded = NewApplication();
        var registrationEntityId = ObjectId.GenerateNewId();
        using var session = await TransactionAsync();
        await _staging.StageAsync(seeded, session, Ct);

        await _model.StampDomainModelForeignKeysAsync(
            seeded.Id!.Value,
            registrationEntityId,
            new Dictionary<string, ObjectId>(),
            null,
            session,
            Ct
        );

        var read = (await _model.GetByObjectIdAsync(seeded.Id!.Value, session, Ct))!;
        Assert.Equal(registrationEntityId, read.RegistrationEntityId);
        await session.AbortTransactionAsync(Ct);
    }
}
