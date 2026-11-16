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
        new()
        {
            Id = ObjectId.GenerateNewId(),
            OrganisationId = OrganisationId,
            MaterialType = MaterialType.Steel,
            Year = 2027,
        };

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

    [Fact]
    public async Task Serve_ReturnsTheGivenApplication()
    {
        var application = Application();

        var served = await new LegacyApplicationReader(_persistence).ServeFromSourceAsync(application, Ct);

        served.Should().BeSameAs(application);
        await _persistence.DidNotReceiveWithAnyArgs().GetByIdAsync(default!, default!);
    }

    [Fact]
    public async Task ServeMany_ReturnsTheGivenApplicationsInOrder()
    {
        var first = Application();
        var second = Application();

        var served = await new LegacyApplicationReader(_persistence).ServeManyFromSourceAsync(
            [first, second],
            Ct
        );

        served.Should().Equal(first, second);
        await _persistence.DidNotReceiveWithAnyArgs().GetByOrganisationAsync(default!);
    }

    [Fact]
    public async Task GetById_Cancelled_Throws()
    {
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        var act = () =>
            new LegacyApplicationReader(_persistence).GetByIdAsync(
                OrganisationId,
                ObjectId.GenerateNewId().ToString(),
                cancelled.Token
            );

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task GetByOrganisation_Cancelled_Throws()
    {
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        var act = () =>
            new LegacyApplicationReader(_persistence).GetByOrganisationAsync(
                OrganisationId,
                cancelled.Token
            );

        await act.Should().ThrowAsync<OperationCanceledException>();
    }
}
