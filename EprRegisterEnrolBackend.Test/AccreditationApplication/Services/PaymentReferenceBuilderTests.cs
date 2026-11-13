using EprRegisterEnrolBackend.AccreditationApplication.Models;
using EprRegisterEnrolBackend.AccreditationApplication.Services;
using FluentAssertions;

namespace EprRegisterEnrolBackend.Test.AccreditationApplication.Services;

// RA-000: the formats ported from buildPaymentReference in epr-register-enrol-frontend's
// src/server/common/helpers/paymentDetails.js - one case per nation x exporter/reprocessor.
public class PaymentReferenceBuilderTests
{
    [Theory]
    [InlineData(Nation.NorthernIreland, true, "NI/PR/REEX/500500")]
    [InlineData(Nation.NorthernIreland, false, "NI/PR/REEX/500500")]
    [InlineData(Nation.Wales, true, "PREX/500500")]
    [InlineData(Nation.Wales, false, "PREX/500500")]
    [InlineData(Nation.Scotland, true, "E800 81581/500500")]
    [InlineData(Nation.Scotland, false, "E800 81581/500500")]
    [InlineData(Nation.England, true, "PR/PK/EXP/500500")]
    [InlineData(Nation.England, false, "PR/PK/REP/500500")]
    public void Build_UsesTheNationsFormat(Nation nation, bool isExporter, string expected) =>
        PaymentReferenceBuilder.Build(nation, 500500, isExporter).Should().Be(expected);

    [Fact]
    public void Build_WritesTheNumberAsPlainDigits()
    {
        var previous = Thread.CurrentThread.CurrentCulture;
        try
        {
            // A culture with a group separator must not change the reference.
            Thread.CurrentThread.CurrentCulture = new System.Globalization.CultureInfo("de-DE");
            PaymentReferenceBuilder.Build(Nation.England, 1234567, false).Should().Be("PR/PK/REP/1234567");
        }
        finally
        {
            Thread.CurrentThread.CurrentCulture = previous;
        }
    }

    [Fact]
    public void Build_UnknownNation_Throws()
    {
        var act = () => PaymentReferenceBuilder.Build((Nation)99, 500500, false);

        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Theory]
    [InlineData(null, Nation.England)]
    [InlineData(500500, null)]
    [InlineData(null, null)]
    public void TryBuild_MissingOrgNumberOrNation_ReturnsNull(int? orgId, Nation? nation) =>
        PaymentReferenceBuilder
            .TryBuild(
                new AccreditationApplicationModel
                {
                    OrganisationId = "org-1",
                    Year = 2026,
                    MaterialType = MaterialType.Steel,
                    OrgId = orgId,
                    Nation = nation,
                }
            )
            .Should()
            .BeNull();
}
