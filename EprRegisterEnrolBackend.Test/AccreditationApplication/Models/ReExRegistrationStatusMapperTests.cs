using EprRegisterEnrolBackend.AccreditationApplication.Models;
using EprRegisterEnrolBackend.DomainModel.Entities;
using EprRegisterEnrolBackend.ReEx;
using EprRegisterEnrolBackend.Test.Utils.Logging;
using FluentAssertions;
using Microsoft.Extensions.Logging;

namespace EprRegisterEnrolBackend.Test.AccreditationApplication.Models;

public class ReExRegistrationStatusMapperTests
{
    private const string OrganisationId = "org-806";
    private const string RegistrationId = "reg-806";

    // RA-806: ReEx's REGISTRATION_STATUS values (epr-backend), by name, ignoring case.
    [Theory]
    [InlineData("created", RegistrationStatus.Created)]
    [InlineData("approved", RegistrationStatus.Approved)]
    [InlineData("rejected", RegistrationStatus.Rejected)]
    [InlineData("cancelled", RegistrationStatus.Cancelled)]
    [InlineData("APPROVED", RegistrationStatus.Approved)]
    [InlineData("Cancelled", RegistrationStatus.Cancelled)]
    public void Map_ReExRegistrationStatus_MapsToTheSameStatusWithNoErrorLog(
        string reExStatus,
        RegistrationStatus expected
    )
    {
        var logger = new CapturingLogger<ReExRegistrationStatusMapperTests>();

        var result = ReExRegistrationStatusMapper.Map(
            reExStatus,
            OrganisationId,
            RegistrationId,
            logger
        );

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().Be(expected);
        logger.Entries.Should().NotContain(e => e.LogLevel == LogLevel.Error);
    }

    // Missing, blank, not a registration status (suspended and active are accreditation and organisation statuses),
    // the enum's number, or a name with padding: none is a status ReEx sends, so the lookup fails as bad ReEx data,
    // like the adapters' other data-integrity failures, and says which registration and what it was.
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("suspended")]
    [InlineData("active")]
    [InlineData("1")]
    [InlineData(" approved")]
    public void Map_NotAReExRegistrationStatus_FailsAndLogsAtErrorWithTheRawValue(
        string? reExStatus
    )
    {
        var logger = new CapturingLogger<ReExRegistrationStatusMapperTests>();

        var result = ReExRegistrationStatusMapper.Map(
            reExStatus,
            OrganisationId,
            RegistrationId,
            logger
        );

        result.IsSuccess.Should().BeFalse();
        result.Error!.Kind.Should().Be(ReExErrorKind.ClientError);
        result.StatusCode.Should().Be(500);
        var entry = logger.Entries.Should().ContainSingle().Which;
        entry.LogLevel.Should().Be(LogLevel.Error);
        entry
            .Message.Should()
            .Be(
                $"ReEx registration status is missing or unrecognised for org={OrganisationId} reg={RegistrationId}: {reExStatus ?? "(null)"}"
            );
    }
}
