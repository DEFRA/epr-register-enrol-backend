using EprRegisterEnrolBackend.DomainModel.Entities;
using EprRegisterEnrolBackend.DomainModel.Status;

namespace EprRegisterEnrolBackend.Test.DomainModel.Status;

public class StatusTransitionsTests
{
    private static readonly (AccreditationStatus, AccreditationStatus)[] s_legalAccreditation =
    [
        (AccreditationStatus.Approved, AccreditationStatus.Suspended),
        (AccreditationStatus.Suspended, AccreditationStatus.Approved),
        (AccreditationStatus.Suspended, AccreditationStatus.Cancelled),
        (AccreditationStatus.Cancelled, AccreditationStatus.Approved),
    ];

    private static readonly (AccreditedSiteStatus, AccreditedSiteStatus)[] s_legalAccredited =
    [
        (AccreditedSiteStatus.Approved, AccreditedSiteStatus.Suspended),
        (AccreditedSiteStatus.Suspended, AccreditedSiteStatus.Approved),
    ];

    private static readonly (OverseasSiteStatus, OverseasSiteStatus)[] s_legalRegistered =
    [
        (OverseasSiteStatus.Proposed, OverseasSiteStatus.Active),
        (OverseasSiteStatus.Proposed, OverseasSiteStatus.Discarded),
    ];

    public static TheoryData<AccreditationStatus, AccreditationStatus> LegalAccreditation =>
        new(s_legalAccreditation);

    public static TheoryData<AccreditationStatus, AccreditationStatus> IllegalAccreditation =>
        new(AllPairsExcept(s_legalAccreditation));

    public static TheoryData<AccreditedSiteStatus, AccreditedSiteStatus> LegalAccredited =>
        new(s_legalAccredited);

    public static TheoryData<AccreditedSiteStatus, AccreditedSiteStatus> IllegalAccredited =>
        new(AllPairsExcept(s_legalAccredited));

    public static TheoryData<OverseasSiteStatus, OverseasSiteStatus> LegalRegistered =>
        new(s_legalRegistered);

    public static TheoryData<OverseasSiteStatus, OverseasSiteStatus> IllegalRegistered =>
        new(AllPairsExcept(s_legalRegistered));

    // Every ordered pair of the enum's values, staying put included, that is not in the legal list.
    private static IEnumerable<(T, T)> AllPairsExcept<T>((T, T)[] legal)
        where T : struct, Enum =>
        Enum.GetValues<T>()
            .SelectMany(from => Enum.GetValues<T>(), (from, to) => (from, to))
            .Except(legal);

    [Theory]
    [MemberData(nameof(LegalAccreditation))]
    public void Accreditation_LegalTransition_IsAllowed(
        AccreditationStatus from,
        AccreditationStatus to
    ) => Assert.True(AccreditationTransitions.Status.CanTransition(from, to));

    [Theory]
    [MemberData(nameof(IllegalAccreditation))]
    public void Accreditation_IllegalTransition_IsRejected(
        AccreditationStatus from,
        AccreditationStatus to
    ) => Assert.False(AccreditationTransitions.Status.CanTransition(from, to));

    [Theory]
    [MemberData(nameof(LegalAccredited))]
    public void AccreditedSite_LegalTransition_IsAllowed(
        AccreditedSiteStatus from,
        AccreditedSiteStatus to
    ) => Assert.True(OverseasSiteTransitions.Accredited.CanTransition(from, to));

    [Theory]
    [MemberData(nameof(IllegalAccredited))]
    public void AccreditedSite_IllegalTransition_IsRejected(
        AccreditedSiteStatus from,
        AccreditedSiteStatus to
    ) => Assert.False(OverseasSiteTransitions.Accredited.CanTransition(from, to));

    [Theory]
    [MemberData(nameof(LegalRegistered))]
    public void RegisteredSite_LegalTransition_IsAllowed(
        OverseasSiteStatus from,
        OverseasSiteStatus to
    ) => Assert.True(OverseasSiteTransitions.Registered.CanTransition(from, to));

    [Theory]
    [MemberData(nameof(IllegalRegistered))]
    public void RegisteredSite_IllegalTransition_IsRejected(
        OverseasSiteStatus from,
        OverseasSiteStatus to
    ) => Assert.False(OverseasSiteTransitions.Registered.CanTransition(from, to));

    [Fact]
    public void EnsureCanTransition_IllegalPair_ThrowsWithTheKindAndBothStatuses()
    {
        var ex = Assert.Throws<IllegalStatusTransitionException>(() =>
            AccreditationTransitions.Status.EnsureCanTransition(
                AccreditationStatus.Approved,
                AccreditationStatus.Cancelled
            )
        );

        Assert.Equal("accreditation", ex.Kind);
        Assert.Equal("Approved", ex.From);
        Assert.Equal("Cancelled", ex.To);
        Assert.Equal(
            "Cannot transition accreditation status from Approved to Cancelled.",
            ex.Message
        );
    }

    [Fact]
    public void EnsureCanTransition_LegalPair_DoesNotThrow()
    {
        var ex = Record.Exception(() =>
            AccreditationTransitions.Status.EnsureCanTransition(
                AccreditationStatus.Approved,
                AccreditationStatus.Suspended
            )
        );

        Assert.Null(ex);
    }

    [Fact]
    public void CanTransition_EmptyTable_AllowsNothing()
    {
        var transitions = new StatusTransitions<AccreditationStatus>(
            "test",
            new Dictionary<AccreditationStatus, AccreditationStatus[]>()
        );

        Assert.All(
            AllPairsExcept<AccreditationStatus>([]),
            pair => Assert.False(transitions.CanTransition(pair.Item1, pair.Item2))
        );
    }
}
