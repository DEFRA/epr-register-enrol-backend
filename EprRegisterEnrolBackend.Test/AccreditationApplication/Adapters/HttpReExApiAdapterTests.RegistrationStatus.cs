using System.Text.Json.Nodes;
using EprRegisterEnrolBackend.AccreditationApplication.Adapters;
using EprRegisterEnrolBackend.AccreditationApplication.Models;
using EprRegisterEnrolBackend.DomainModel.Entities;
using EprRegisterEnrolBackend.Test.Utils.Logging;
using FluentAssertions;
using Microsoft.Extensions.Logging;

namespace EprRegisterEnrolBackend.Test.AccreditationApplication.Adapters;

// RA-806: the matched registration's ReEx status reaches the seed; a missing or unknown one is logged at Error and
// carried as null, without failing the lookup.
public partial class HttpReExApiAdapterTests
{
    private const string StatusOrganisationId = "6a2fcd74e16883c137d01188";

    // OrganisationJson with reg-reprocessor-1's status set to rawStatus, or removed when rawStatus is null.
    private static string OrganisationJsonWithReprocessorStatus(string? rawStatus)
    {
        var organisation = JsonNode.Parse(OrganisationJson)!.AsObject();
        var registration = organisation["registrations"]!
            .AsArray()
            .Single(r => (string?)r!["id"] == "reg-reprocessor-1")!
            .AsObject();
        registration.Remove("status");
        if (rawStatus is not null)
            registration["status"] = rawStatus;
        return organisation.ToJsonString();
    }

    private static async Task<(ReExAccreditationDto Dto, CapturingLogger<HttpReExApiAdapter> Logger)> ReprocessorWithStatusAsync(
        string? rawStatus
    )
    {
        var logger = new CapturingLogger<HttpReExApiAdapter>();
        var sut = BuildSut(OrganisationJsonWithReprocessorStatus(rawStatus), logger: logger);

        var result = await sut.GetAccreditationAsync(
            StatusOrganisationId,
            "reg-reprocessor-1",
            MaterialType.Aluminium,
            2026
        );

        result.IsSuccess.Should().BeTrue(because: result.Error?.Message);
        return (result.Value!, logger);
    }

    [Theory]
    [InlineData("created", RegistrationStatus.Created)]
    [InlineData("approved", RegistrationStatus.Approved)]
    [InlineData("rejected", RegistrationStatus.Rejected)]
    [InlineData("cancelled", RegistrationStatus.Cancelled)]
    [InlineData("Approved", RegistrationStatus.Approved)]
    public async Task GetAccreditationAsync_RegistrationStatus_IsCarriedOntoTheDto(
        string rawStatus,
        RegistrationStatus expected
    )
    {
        var (dto, logger) = await ReprocessorWithStatusAsync(rawStatus);

        dto.RegistrationStatus.Should().Be(expected);
        logger.Entries.Should().NotContain(e => e.LogLevel == LogLevel.Error);
    }

    [Fact]
    public async Task GetAccreditationAsync_RegistrationStatusMissing_IsNullAndLoggedAtError()
    {
        var (dto, logger) = await ReprocessorWithStatusAsync(null);

        dto.RegistrationStatus.Should().BeNull();
        var entry = logger.Entries.Should().ContainSingle(e => e.LogLevel == LogLevel.Error).Which;
        entry.Message.Should().Contain("registration status is missing or unrecognised");
        entry.Message.Should().Contain(StatusOrganisationId);
        entry.Message.Should().Contain("reg-reprocessor-1");
    }

    // suspended is an accreditation status, not a registration one (registrations cannot be suspended, PAE-1705).
    [Theory]
    [InlineData("suspended")]
    [InlineData("1")]
    [InlineData("")]
    public async Task GetAccreditationAsync_RegistrationStatusUnrecognised_IsNullAndLoggedAtErrorWithTheRawValue(
        string rawStatus
    )
    {
        var (dto, logger) = await ReprocessorWithStatusAsync(rawStatus);

        dto.RegistrationStatus.Should().BeNull();
        var entry = logger.Entries.Should().ContainSingle(e => e.LogLevel == LogLevel.Error).Which;
        entry.Message.Should().Contain("registration status is missing or unrecognised");
        entry.Message.Should().Contain(StatusOrganisationId);
        entry.Message.Should().Contain("reg-reprocessor-1");
        entry.Message.Should().EndWith($": {rawStatus}");
    }

    // The status is the matched registration's own, never another registration's in the same organisation.
    [Fact]
    public async Task GetAccreditationAsync_OtherRegistrationsStatus_IsNotUsed()
    {
        var organisation = JsonNode.Parse(OrganisationJsonWithReprocessorStatus("created"))!;
        var sut = BuildSut(organisation.ToJsonString());

        var result = await sut.GetAccreditationAsync(
            StatusOrganisationId,
            "reg-exporter-1",
            MaterialType.Aluminium,
            2026
        );

        result.IsSuccess.Should().BeTrue(because: result.Error?.Message);
        result.Value!.RegistrationStatus.Should().Be(RegistrationStatus.Approved);
    }
}
