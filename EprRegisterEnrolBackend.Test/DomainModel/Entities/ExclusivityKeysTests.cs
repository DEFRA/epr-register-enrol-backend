using EprRegisterEnrolBackend.AccreditationApplication.Models;
using EprRegisterEnrolBackend.DomainModel.Entities;
using FluentAssertions;
using MongoDB.Bson;

namespace EprRegisterEnrolBackend.Test.DomainModel.Entities;

/// <summary>
/// The exclusivity key each application type holds while open, in the formats and with the release rules of
/// register-enrol-application-types.md ("Rules and indexes"). Nothing writes the key yet (RA-747).
/// </summary>
public class ExclusivityKeysTests
{
    private static readonly ObjectId s_registrationId = ObjectId.Parse("6512a0f0c0ffee0000000001");
    private static readonly ObjectId s_siteId = ObjectId.Parse("6512a0f0c0ffee0000000002");
    private static readonly ObjectId s_accreditationId = ObjectId.Parse("6512a0f0c0ffee0000000003");

    private static ApplicationEntity Application(
        ApplicationDetails details,
        ObjectId? registrationId = null,
        ApplicationStatus status = ApplicationStatus.Submitted
    ) => DomainModelBuilders.Application(registrationId ?? s_registrationId, details, status);

    // One details instance per type, so a test can run for every ApplicationType. A new type fails here until it is
    // added, which is the point.
    private static ApplicationDetails DetailsOf(ApplicationType type) =>
        type switch
        {
            ApplicationType.Accreditation => DomainModelBuilders.AccreditationDetails(),
            ApplicationType.OrsChange => DomainModelBuilders.OrsChangeDetails(s_siteId),
            ApplicationType.TonnageChange => DomainModelBuilders.TonnageChangeDetails(
                s_accreditationId
            ),
            ApplicationType.NewRegistration => DomainModelBuilders.NewRegistrationDetails(),
            ApplicationType.AnnualCharge => DomainModelBuilders.AnnualChargeDetails(),
            _ => throw new ArgumentOutOfRangeException(nameof(type), type, null),
        };

    // --- formats --------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(AccreditationKind.Reapply)]
    [InlineData(AccreditationKind.New)]
    public void Build_Accreditation_IsRegistrationAndYear_WhateverTheKind(AccreditationKind kind)
    {
        var details = DomainModelBuilders.AccreditationDetails();
        details.Kind = kind;

        ExclusivityKeys
            .Build(Application(details))
            .Should()
            .Be("accreditation|6512a0f0c0ffee0000000001|2027");
    }

    [Fact]
    public void Build_OrsChange_IsTheOverseasSite()
    {
        ExclusivityKeys
            .Build(Application(DomainModelBuilders.OrsChangeDetails(s_siteId)))
            .Should()
            .Be("ors-change|6512a0f0c0ffee0000000002");
    }

    [Fact]
    public void Build_TonnageChange_IsTheTargetAccreditation()
    {
        ExclusivityKeys
            .Build(Application(DomainModelBuilders.TonnageChangeDetails(s_accreditationId)))
            .Should()
            .Be("tonnage-change|6512a0f0c0ffee0000000003");
    }

    [Fact]
    public void Build_AnnualCharge_IsRegistrationAndChargeYear()
    {
        ExclusivityKeys
            .Build(Application(DomainModelBuilders.AnnualChargeDetails(2028)))
            .Should()
            .Be("annual-charge|6512a0f0c0ffee0000000001|2028");
    }

    [Fact]
    public void Build_NewRegistration_HasNoKeyUntilTheJourneySaysHowASiteIsIdentified()
    {
        ExclusivityKeys
            .Build(Application(DomainModelBuilders.NewRegistrationDetails()))
            .Should()
            .BeNull();
    }

    // A missing part would otherwise build the same key for every application missing it, and they would collide.
    [Fact]
    public void Build_AnyIdMissing_HasNoKey()
    {
        var unlinked = Application(DomainModelBuilders.AccreditationDetails());
        unlinked.RegistrationId = null;
        var unlinkedCharge = Application(DomainModelBuilders.AnnualChargeDetails());
        unlinkedCharge.RegistrationId = null;

        ExclusivityKeys.Build(unlinked).Should().BeNull();
        ExclusivityKeys.Build(unlinkedCharge).Should().BeNull();
        ExclusivityKeys
            .Build(Application(DomainModelBuilders.OrsChangeDetails(ObjectId.Empty)))
            .Should()
            .BeNull();
        ExclusivityKeys
            .Build(Application(DomainModelBuilders.TonnageChangeDetails(ObjectId.Empty)))
            .Should()
            .BeNull();
    }

    // The years are plain ints, so an unset one is 0. It is a missing part like an unset id, or every application
    // with no year would share the key for year 0.
    [Fact]
    public void Build_YearNotSet_HasNoKey()
    {
        var accreditation = DomainModelBuilders.AccreditationDetails();
        accreditation.Year = 0;

        ExclusivityKeys.Build(Application(accreditation)).Should().BeNull();
        ExclusivityKeys
            .Build(Application(DomainModelBuilders.AnnualChargeDetails(0)))
            .Should()
            .BeNull();
    }

    // --- release rules --------------------------------------------------------------------------------------------

    // Accreditation keeps its key once approved: one application per registration and year, and a cancelled
    // Accreditation is reinstated as a new version of itself, never replaced (Q36). In-year changes release theirs on
    // approval, because the next change to the same ORS or Accreditation is a new application.
    [Theory]
    [InlineData(ApplicationType.Accreditation, ApplicationStatus.Approved, true)]
    [InlineData(ApplicationType.Accreditation, ApplicationStatus.Rejected, false)]
    [InlineData(ApplicationType.Accreditation, ApplicationStatus.Withdrawn, false)]
    [InlineData(ApplicationType.OrsChange, ApplicationStatus.Approved, false)]
    [InlineData(ApplicationType.OrsChange, ApplicationStatus.Rejected, false)]
    [InlineData(ApplicationType.OrsChange, ApplicationStatus.Withdrawn, false)]
    [InlineData(ApplicationType.TonnageChange, ApplicationStatus.Approved, false)]
    [InlineData(ApplicationType.TonnageChange, ApplicationStatus.Withdrawn, false)]
    [InlineData(ApplicationType.NewRegistration, ApplicationStatus.Approved, true)]
    [InlineData(ApplicationType.NewRegistration, ApplicationStatus.Rejected, false)]
    [InlineData(ApplicationType.AnnualCharge, ApplicationStatus.Approved, true)]
    [InlineData(ApplicationType.AnnualCharge, ApplicationStatus.Withdrawn, false)]
    public void HoldsKey_AtAClosingStatus_FollowsTheTypesReleaseRule(
        ApplicationType type,
        ApplicationStatus status,
        bool holds
    )
    {
        ExclusivityKeys.HoldsKey(DetailsOf(type), status).Should().Be(holds);
    }

    [Fact]
    public void HoldsKey_EveryOpenStatus_HoldsForEveryType()
    {
        var open = Enum.GetValues<ApplicationStatus>().Where(s => s.IsInFlight());

        foreach (var type in Enum.GetValues<ApplicationType>())
        {
            foreach (var status in open)
            {
                ExclusivityKeys
                    .HoldsKey(DetailsOf(type), status)
                    .Should()
                    .BeTrue($"{type} at {status}");
            }
        }
    }

    [Fact]
    public void For_ReleasedApplication_HasNoKey_ButAnOpenOneDoes()
    {
        var open = Application(DomainModelBuilders.OrsChangeDetails(s_siteId));
        var approved = Application(
            DomainModelBuilders.OrsChangeDetails(s_siteId),
            status: ApplicationStatus.Approved
        );

        ExclusivityKeys.For(open).Should().Be("ors-change|6512a0f0c0ffee0000000002");
        ExclusivityKeys.For(approved).Should().BeNull();
    }
}
