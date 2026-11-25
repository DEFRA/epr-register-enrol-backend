using System.Text.Json.Nodes;
using EprRegisterEnrolBackend.AccreditationApplication.Adapters;
using EprRegisterEnrolBackend.AccreditationApplication.Models;
using EprRegisterEnrolBackend.DomainModel.Entities;
using EprRegisterEnrolBackend.ReEx;
using EprRegisterEnrolBackend.Test.Utils.Logging;
using FluentAssertions;
using Microsoft.Extensions.Logging;

namespace EprRegisterEnrolBackend.Test.AccreditationApplication.Adapters;

// RA-806: the matched registration's ReEx status reaches the seed; a missing or unknown one fails the lookup, as the
// adapter's other data-integrity failures do, so the seed fails. ReExRegistrationStatusMapperTests covers the log text.
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

    private static async Task<(
        ReExResult<ReExAccreditationDto> Result,
        CapturingLogger<HttpReExApiAdapter> Logger
    )> ReprocessorWithStatusAsync(string? rawStatus)
    {
        var logger = new CapturingLogger<HttpReExApiAdapter>();
        var sut = BuildSut(OrganisationJsonWithReprocessorStatus(rawStatus), logger: logger);

        var result = await sut.GetAccreditationAsync(
            StatusOrganisationId,
            "reg-reprocessor-1",
            MaterialType.Aluminium,
            2026
        );

        return (result, logger);
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
        var (result, logger) = await ReprocessorWithStatusAsync(rawStatus);

        result.IsSuccess.Should().BeTrue(because: result.Error?.Message);
        result.Value!.RegistrationStatus.Should().Be(expected);
        logger.Entries.Should().NotContain(e => e.LogLevel == LogLevel.Error);
    }

    // Missing, and malformed: suspended is an accreditation status, not a registration one (PAE-1705).
    [Theory]
    [InlineData(null)]
    [InlineData("suspended")]
    [InlineData("1")]
    [InlineData("")]
    public async Task GetAccreditationAsync_RegistrationStatusMissingOrUnrecognised_FailsWithOneErrorLog(
        string? rawStatus
    )
    {
        var (result, logger) = await ReprocessorWithStatusAsync(rawStatus);

        result.IsSuccess.Should().BeFalse();
        result.IsClientError.Should().BeTrue();
        result.StatusCode.Should().Be(500);
        var entry = logger.Entries.Should().ContainSingle(e => e.LogLevel == LogLevel.Error).Which;
        entry.Message.Should().Contain(StatusOrganisationId).And.Contain("reg-reprocessor-1");
    }

    // The status is the matched registration's own, never another registration's in the same organisation.
    [Fact]
    public async Task GetAccreditationAsync_OtherRegistrationsStatus_IsNotUsed()
    {
        var sut = BuildSut(OrganisationJsonWithReprocessorStatus("created"));

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
