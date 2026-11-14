using EprRegisterEnrolBackend.AccreditationApplication.Models;
using EprRegisterEnrolBackend.AccreditationApplication.Services;
using EprRegisterEnrolBackend.DomainModel.ReadModel;
using FluentAssertions;
using MongoDB.Bson;
using NSubstitute;

namespace EprRegisterEnrolBackend.Test.DomainModel.ReadModel;

/// <summary>
/// RA-705: with <c>DomainModel:ReadFromModel</c> off, the routes read through <see cref="LegacyApplicationReader"/>,
/// which must behave exactly as the persistence calls the routes made before the seam.
/// </summary>
public sealed class LegacyApplicationReaderTests
{
    private const string OrganisationId = "6512a0f0c0ffee0000000001";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly IAccreditationApplicationPersistence _persistence =
        Substitute.For<IAccreditationApplicationPersistence>();

    private static AccreditationApplicationModel Application() =>
        new() { Id = ObjectId.GenerateNewId(), OrganisationId = OrganisationId };

    [Fact]
    public async Task GetById_ReturnsWhatPersistenceReturns()
    {
        var application = Application();
        var id = application.Id!.Value.ToString();
        _persistence.GetByIdAsync(OrganisationId, id).Returns(application);

        var read = await new LegacyApplicationReader(_persistence).GetByIdAsync(
            OrganisationId,
            id,
            Ct
        );

        read.Should().BeSameAs(application);
    }

    [Fact]
    public async Task GetById_Unknown_ReturnsNull()
    {
        var read = await new LegacyApplicationReader(_persistence).GetByIdAsync(
            OrganisationId,
            ObjectId.GenerateNewId().ToString(),
            Ct
        );

        read.Should().BeNull();
    }

    [Fact]
    public async Task GetByOrganisation_KeepsPersistencesOrder()
    {
        var newer = Application();
        var older = Application();
        _persistence.GetByOrganisationAsync(OrganisationId).Returns([newer, older]);

        var list = await new LegacyApplicationReader(_persistence).GetByOrganisationAsync(
            OrganisationId,
            Ct
        );

        list.Should().Equal(newer, older);
    }
}
