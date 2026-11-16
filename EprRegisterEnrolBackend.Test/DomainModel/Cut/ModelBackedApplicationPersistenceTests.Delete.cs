using EprRegisterEnrolBackend.AccreditationApplication.Models;
using EprRegisterEnrolBackend.DomainModel.Entities;
using EprRegisterEnrolBackend.DomainModel.Versioning;
using MongoDB.Bson;

namespace EprRegisterEnrolBackend.Test.DomainModel.Cut;

/// <summary>
/// RA-706 decision 7: after the cut, deleting an application (only the test-data teardown does) removes every version
/// of it, and of the accreditation it owns, from the model. Its organisation and registration stay, so a reset that
/// seeds the same ids again links back to them.
/// </summary>
public sealed partial class ModelBackedApplicationPersistenceTests
{
    private async Task<AccreditationEntity> AccreditationOwnedByAsync(
        AccreditationApplicationModel application,
        string number
    ) =>
        await _stores.Accreditations.Versions.InsertAsync(
            new AccreditationEntity
            {
                RegistrationId = (await CurrentVersionAsync(application))!.RegistrationId!.Value,
                Year = application.Year,
                AccreditationNumber = number,
                ApplicationId = application.Id,
            },
            new ChangeInfo { Reason = ChangeReason.Migration },
            cancellationToken: Ct
        );

    [Fact]
    public async Task DeleteMany_RemovesEveryVersionOfEachApplication_AndCountsThem()
    {
        var first = await MigratedAsync();
        var second = await MigratedAsync(NewApplication(OtherOrganisationId));
        var read = await ReadAsync(first);
        read.PaymentReference = "PAY-2";
        await _model.UpdateAsync(read);
        var legacyBefore = await LegacyCountAsync();

        var removed = await _model.DeleteManyAsync([first.Id!.Value, second.Id!.Value]);

        Assert.Equal(2, removed);
        Assert.Empty(
            await _stores.Applications.Versions.GetVersionsAsync(
                first.Id!.Value,
                cancellationToken: Ct
            )
        );
        Assert.Empty(
            await _stores.Applications.Versions.GetVersionsAsync(
                second.Id!.Value,
                cancellationToken: Ct
            )
        );
        Assert.Null(await _model.GetByIdAsync(first.OrganisationId, first.Id!.Value.ToString()));
        Assert.Equal(legacyBefore, await LegacyCountAsync());
    }

    [Fact]
    public async Task DeleteMany_RemovesTheAccreditationTheApplicationOwns_AndNoOther()
    {
        var deleted = await MigratedAsync();
        // Another year: one accreditation per registration and year.
        var keptApplication = NewApplication(OtherOrganisationId);
        keptApplication.Year = deleted.Year + 1;
        var kept = await MigratedAsync(keptApplication);
        var owned = await AccreditationOwnedByAsync(deleted, "ACC-1");
        var other = await AccreditationOwnedByAsync(kept, "ACC-2");

        await _model.DeleteManyAsync([deleted.Id!.Value]);

        Assert.Empty(
            await _stores.Accreditations.Versions.GetVersionsAsync(
                owned.EntityId,
                cancellationToken: Ct
            )
        );
        Assert.NotEmpty(
            await _stores.Accreditations.Versions.GetVersionsAsync(
                other.EntityId,
                cancellationToken: Ct
            )
        );
        Assert.NotNull(await CurrentVersionAsync(kept));
    }

    [Fact]
    public async Task DeleteMany_IdsTheModelDoesNotHold_RemovesNothing()
    {
        var kept = await MigratedAsync();

        var removed = await _model.DeleteManyAsync([ObjectId.GenerateNewId()]);

        Assert.Equal(0, removed);
        Assert.NotNull(await CurrentVersionAsync(kept));
    }

    // The test-data reset: tear down, then seed the same ids again.
    [Fact]
    public async Task DeleteMany_ThenUpsertTheSameApplication_ServesItAgain()
    {
        var migrated = await MigratedAsync();
        var again = await ReadAsync(migrated);

        await _model.DeleteManyAsync([migrated.Id!.Value]);
        await _model.UpsertAsync(again);

        Assert.Equal(migrated.Year, (await ReadAsync(migrated)).Year);
        Assert.Equal(0, await StagedCountAsync());
    }
}
