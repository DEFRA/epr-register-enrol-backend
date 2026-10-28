using EprRegisterEnrolBackend.AccreditationApplication.Models;
using EprRegisterEnrolBackend.AccreditationApplication.Services;
using FluentAssertions;

namespace EprRegisterEnrolBackend.Test.AccreditationApplication.Services;

// RA-690: the pure accreditation reference derivation rule - leading character becomes 'A',
// characters 2-3 become the year applied for (mod 100), everything else is copied unchanged.
// Source validation is shape only (17-character reference format plus a leading letter that
// suits the source kind).
public class AccreditationReferenceDerivationTests
{
    [Fact]
    public void Derive_PreviousAccreditationSource_ReplacesYearOnly()
    {
        var result = AccreditationReferenceDerivation.Derive(
            "A26ER0001230042PL",
            AccreditationReferenceSourceKind.PreviousAccreditation,
            2028
        );

        result.IsValid.Should().BeTrue();
        result.Reference.Should().Be("A28ER0001230042PL");
    }

    [Fact]
    public void Derive_RegistrationSource_ChangesLeadingRToAAndReplacesYear()
    {
        var result = AccreditationReferenceDerivation.Derive(
            "R25ER0001230042PL",
            AccreditationReferenceSourceKind.Registration,
            2026
        );

        result.IsValid.Should().BeTrue();
        result.Reference.Should().Be("A26ER0001230042PL");
    }

    [Theory]
    [InlineData("R25ER0001230042ST", "A27ER0001230042ST")]
    [InlineData("R25SX1234569876WO", "A27SX1234569876WO")]
    [InlineData("R25WR0000010001AL", "A27WR0000010001AL")]
    [InlineData("R25NX9999999999FB", "A27NX9999999999FB")]
    [InlineData("R25ER0001230042PA", "A27ER0001230042PA")]
    [InlineData("R25EX0001230042PL", "A27EX0001230042PL")]
    [InlineData("R25SR4567890123GR", "A27SR4567890123GR")]
    [InlineData("R25WX4567890123GO", "A27WX4567890123GO")]
    public void Derive_RegistrationSource_PreservesEveryCharacterAfterTheYear(
        string source,
        string expected
    )
    {
        var result = AccreditationReferenceDerivation.Derive(
            source,
            AccreditationReferenceSourceKind.Registration,
            2027
        );

        result.Reference.Should().Be(expected);
    }

    [Theory]
    [InlineData("A26NX0001230042GO", "A30NX0001230042GO")]
    [InlineData("A26SR0001230042GR", "A30SR0001230042GR")]
    public void Derive_PreviousAccreditationSource_PreservesEveryCharacterAfterTheYear(
        string source,
        string expected
    )
    {
        var result = AccreditationReferenceDerivation.Derive(
            source,
            AccreditationReferenceSourceKind.PreviousAccreditation,
            2030
        );

        result.Reference.Should().Be(expected);
    }

    [Theory]
    [InlineData(2100, "A00ER0001230042PL")]
    [InlineData(2105, "A05ER0001230042PL")]
    [InlineData(2099, "A99ER0001230042PL")]
    public void Derive_TakesYearModHundredZeroPadded(int year, string expected)
    {
        var result = AccreditationReferenceDerivation.Derive(
            "A26ER0001230042PL",
            AccreditationReferenceSourceKind.PreviousAccreditation,
            year
        );

        result.Reference.Should().Be(expected);
    }

    [Theory]
    [InlineData("R25ER0001230042P")] // too short
    [InlineData("R25ER0001230042PLX")] // too long
    [InlineData("R25FR0001230042PL")] // bad agency letter
    [InlineData("R25EA0001230042PL")] // bad process type
    [InlineData("R25ERABC1230042PL")] // non-digit OrgId
    [InlineData("R25ER00012300X2PL")] // non-digit sequence
    [InlineData("R2XER0001230042PL")] // non-digit year
    [InlineData("R25ER0001230042GL")] // unknown material code
    [InlineData("r25er0001230042pl")] // lower case
    [InlineData("R25eR0001230042PL")] // lower-case agency letter
    [InlineData("R25ER0001230042pl")] // lower-case material code
    [InlineData("R25ER\u0660\u0660\u0660\u0661\u0662\u06630042PL")] // Arabic-Indic (non-ASCII) digits
    [InlineData("STUB-REG-001")]
    [InlineData("")]
    public void Derive_MalformedRegistrationSource_IsRejectedNamingSourceAndKind(
        string source
    )
    {
        var result = AccreditationReferenceDerivation.Derive(
            source,
            AccreditationReferenceSourceKind.Registration,
            2026
        );

        result.IsValid.Should().BeFalse();
        result.Reference.Should().BeNull();
        result.Error.Should().Contain($"'{source}'").And.Contain("registration reference");
    }

    [Theory]
    [InlineData("A26ER0001230042")]
    [InlineData("A26ER0001230042GL")]
    [InlineData("")]
    public void Derive_MalformedPreviousAccreditationSource_IsRejectedNamingSourceAndKind(
        string source
    )
    {
        var result = AccreditationReferenceDerivation.Derive(
            source,
            AccreditationReferenceSourceKind.PreviousAccreditation,
            2027
        );

        result.IsValid.Should().BeFalse();
        result.Reference.Should().BeNull();
        result
            .Error.Should()
            .Contain($"'{source}'")
            .And.Contain("previous accreditation reference");
    }

    [Theory]
    [InlineData(AccreditationReferenceSourceKind.Registration, "registration reference")]
    [InlineData(
        AccreditationReferenceSourceKind.PreviousAccreditation,
        "previous accreditation reference"
    )]
    public void Derive_NullSource_IsRejectedAsMissing(
        AccreditationReferenceSourceKind kind,
        string kindDescription
    )
    {
        var result = AccreditationReferenceDerivation.Derive(null, kind, 2026);

        result.IsValid.Should().BeFalse();
        result.Reference.Should().BeNull();
        result.Error.Should().Contain(kindDescription).And.Contain("missing");
    }

    [Fact]
    public void Derive_NegativeYear_Throws()
    {
        var act = () =>
            AccreditationReferenceDerivation.Derive(
                "R25ER0001230042PL",
                AccreditationReferenceSourceKind.Registration,
                -1
            );

        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void Derive_PreviousAccreditationSourceStartingWithR_IsRejected()
    {
        var result = AccreditationReferenceDerivation.Derive(
            "R26ER0001230042PL",
            AccreditationReferenceSourceKind.PreviousAccreditation,
            2027
        );

        result.IsValid.Should().BeFalse();
        result.Reference.Should().BeNull();
        result
            .Error.Should()
            .Contain("'R26ER0001230042PL'")
            .And.Contain("previous accreditation reference");
    }

    [Fact]
    public void Derive_RegistrationSourceStartingWithA_IsRejected()
    {
        var result = AccreditationReferenceDerivation.Derive(
            "A25ER0001230042PL",
            AccreditationReferenceSourceKind.Registration,
            2026
        );

        result.IsValid.Should().BeFalse();
        result.Reference.Should().BeNull();
        result
            .Error.Should()
            .Contain("'A25ER0001230042PL'")
            .And.Contain("registration reference");
    }
}
