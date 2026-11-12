using System.Text.RegularExpressions;
using EprRegisterEnrolBackend.AccreditationApplication.Startup;
using FluentAssertions;

namespace EprRegisterEnrolBackend.Test.AccreditationApplication.Startup;

// RA-763: pins the 5 November 2026 public register snapshot, so any change to the
// data, and to its known errors, is a deliberate one.
public class PublicRegister2026Tests
{
    private static readonly Regex RegistrationNumber = new(
        "^R26[ESWN][RX][0-9]{6}[0-9]{4}(ST|WO|AL|FB|PA|PL|GR|GO)$"
    );
    private static readonly Regex AccreditationNumber = new(
        "^A26[ESWN][RX][0-9]{6}[0-9]{4}(ST|WO|AL|FB|PA|PL|GR|GO)$"
    );

    private const string TruncatedRegistration = "R26WR5000950025P";
    private const string JpRecyclingRegistration = "R26WR5003020027PL";
    private const string ComhairleRegistration = "R26SR5005410022GO";
    private const string EncircRegistration = "R26ER5002260055GR";
    private const string ReddyPaiSteelRegistration = "R26WX5002360007ST";
    private const string ReddyPaiAluminiumRegistration = "R26WX5002360008AL";

    private static IReadOnlyDictionary<string, PublicRegister2026Entry> Register =>
        PublicRegister2026.ByRegistrationNumber;

    [Fact]
    public void Holds_every_row_of_the_register()
    {
        Register.Should().HaveCount(394);
        Register.Values.Count(e => e.AccreditationNumber is null).Should().Be(19);
    }

    [Fact]
    public void Accreditation_numbers_are_unique()
    {
        var numbers = Register
            .Values.Select(e => e.AccreditationNumber)
            .Where(n => n is not null)
            .ToList();
        numbers.Should().OnlyHaveUniqueItems();
    }

    [Fact]
    public void Every_row_names_its_organisation()
    {
        Register.Values.Should().OnlyContain(e => !string.IsNullOrWhiteSpace(e.OrganisationName));
    }

    [Fact]
    public void Holds_Clearfield_Envirotech()
    {
        Register["R26ER5003240079PL"]
            .Should()
            .Be(new PublicRegister2026Entry("A26ER5003240164PL", 500324, "CLEARFIELD ENVIROTECH LIMITED"));
    }

    [Fact]
    public void Every_row_except_the_known_errors_is_well_formed_and_consistent()
    {
        var knownErrors = new[]
        {
            TruncatedRegistration,
            JpRecyclingRegistration,
            ComhairleRegistration,
            EncircRegistration,
            ReddyPaiSteelRegistration,
            ReddyPaiAluminiumRegistration,
        };

        foreach (var (registration, entry) in Register)
        {
            if (knownErrors.Contains(registration))
            {
                continue;
            }

            RegistrationNumber.IsMatch(registration).Should().BeTrue(registration);
            registration[5..11].Should().Be(entry.OrgId.ToString("D6"), registration);

            if (entry.AccreditationNumber is not { } accreditation)
            {
                continue;
            }

            AccreditationNumber.IsMatch(accreditation).Should().BeTrue(accreditation);
            // Same agency, org and material; only the sequence may differ.
            accreditation[3..11].Should().Be(registration[3..11], registration);
            accreditation[^2..].Should().Be(registration[^2..], registration);
        }
    }

    [Fact]
    public void Known_register_errors_are_kept_as_published()
    {
        Register[TruncatedRegistration].AccreditationNumber.Should().BeNull();
        Register[JpRecyclingRegistration].AccreditationNumber.Should().Be("A26WR5005320025PL");
        Register[ComhairleRegistration].AccreditationNumber.Should().Be("A26SR5005410022GL");
        Register[EncircRegistration].OrgId.Should().Be(500069);
        Register[ReddyPaiSteelRegistration].OrgId.Should().Be(500263);
        Register[ReddyPaiAluminiumRegistration].OrgId.Should().Be(500263);
    }
}