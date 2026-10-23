using EprRegisterEnrolBackend.AccreditationApplication.Adapters;
using EprRegisterEnrolBackend.AccreditationApplication.Models;
using EprRegisterEnrolBackend.ReEx;
using FluentAssertions;
using NSubstitute;

namespace EprRegisterEnrolBackend.Test.AccreditationApplication.Adapters;

public class TestOrganisationReExApiAdapterTests
{
    private const string TestOrg = "e2e-0a1b2c";
    private const string RealOrg = "6650a1b2c3d4e5f6a7b8c9d0";

    private readonly IReExApiAdapter _real = Substitute.For<IReExApiAdapter>();
    private readonly IReExApiAdapter _stub = Substitute.For<IReExApiAdapter>();
    private readonly TestOrganisationReExApiAdapter _sut;

    public TestOrganisationReExApiAdapterTests()
    {
        _sut = new TestOrganisationReExApiAdapter(_real, _stub);
    }

    public static TheoryData<string, bool> Routes =>
        new()
        {
            { TestOrg, true },
            { RealOrg, false },
            { "E2E-0a1b2c", false },
            { "50001", false },
        };

    [Theory]
    [MemberData(nameof(Routes))]
    public async Task GetAccreditationAsync_RoutesByPrefix(string organisationId, bool toStub)
    {
        var expected = ReExResult<ReExAccreditationDto>.Success(new ReExAccreditationDto(), 200);
        Chosen(toStub)
            .GetAccreditationAsync(organisationId, "reg", MaterialType.Plastic, 2025)
            .Returns(expected);

        var result = await _sut.GetAccreditationAsync(
            organisationId,
            "reg",
            MaterialType.Plastic,
            2025
        );

        result.Should().BeSameAs(expected);
        await Other(toStub)
            .DidNotReceiveWithAnyArgs()
            .GetAccreditationAsync(default!, default!, default, default);
    }

    [Theory]
    [MemberData(nameof(Routes))]
    public async Task GetLinkedDefraOrganisationAsync_RoutesByPrefix(
        string organisationId,
        bool toStub
    )
    {
        var expected = ReExResult<LinkedDefraOrganisationResult>.Success(
            new LinkedDefraOrganisationResult { OrganisationId = organisationId },
            200
        );
        Chosen(toStub)
            .GetLinkedDefraOrganisationAsync(organisationId, Arg.Any<CancellationToken>())
            .Returns(expected);

        var result = await _sut.GetLinkedDefraOrganisationAsync(
            organisationId,
            TestContext.Current.CancellationToken
        );

        result.Should().BeSameAs(expected);
        await Other(toStub)
            .DidNotReceiveWithAnyArgs()
            .GetLinkedDefraOrganisationAsync(default!, TestContext.Current.CancellationToken);
    }

    [Theory]
    [MemberData(nameof(Routes))]
    public async Task GetOrganisationNumberAsync_RoutesByPrefix(string organisationId, bool toStub)
    {
        var expected = ReExResult<int?>.Success(912345, 200);
        Chosen(toStub)
            .GetOrganisationNumberAsync(organisationId, Arg.Any<CancellationToken>())
            .Returns(expected);

        var result = await _sut.GetOrganisationNumberAsync(
            organisationId,
            TestContext.Current.CancellationToken
        );

        result.Should().BeSameAs(expected);
        await Other(toStub).DidNotReceiveWithAnyArgs().GetOrganisationNumberAsync(default!, TestContext.Current.CancellationToken);
    }

    [Theory]
    [MemberData(nameof(Routes))]
    public async Task GetNationAsync_RoutesByPrefix(string organisationId, bool toStub)
    {
        var expected = ReExResult<Nation>.Success(Nation.Wales, 200);
        Chosen(toStub)
            .GetNationAsync(organisationId, "reg", Arg.Any<CancellationToken>())
            .Returns(expected);

        var result = await _sut.GetNationAsync(
            organisationId,
            "reg",
            TestContext.Current.CancellationToken
        );

        result.Should().BeSameAs(expected);
        await Other(toStub).DidNotReceiveWithAnyArgs().GetNationAsync(default!, default!, TestContext.Current.CancellationToken);
    }

    private IReExApiAdapter Chosen(bool toStub) => toStub ? _stub : _real;

    private IReExApiAdapter Other(bool toStub) => toStub ? _real : _stub;
}
