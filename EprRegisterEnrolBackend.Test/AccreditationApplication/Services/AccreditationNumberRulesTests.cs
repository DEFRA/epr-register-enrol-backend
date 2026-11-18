using EprRegisterEnrolBackend.AccreditationApplication.Models;
using EprRegisterEnrolBackend.AccreditationApplication.Services;
using FluentAssertions;

namespace EprRegisterEnrolBackend.Test.AccreditationApplication.Services;

// RA-783: the ticket's rules, as a table of worked examples, then the edge cases.
public class AccreditationNumberRulesTests
{
    private const AccreditationNumberCase Renewal = AccreditationNumberCase.ContinuousRenewal;
    private const AccreditationNumberCase FirstTime = AccreditationNumberCase.FirstTime;
    private const AccreditationNumberCase AfterBreak = AccreditationNumberCase.RenewalAfterBreak;

    // Laid out like the ticket's "Rules with examples" table. Columns:
    //   approved   year the application is approved for
    //   registration  the registration number
    //   lastYear, lastNumber  the latest earlier accreditation, or null if never accredited
    //   material   the application's material code (glass: GR remelt, GO other)
    //   draws      the random numbers (0-999) the rules are given, in order; "" means none
    //   expected   the case and the number on approval
    [Theory]
    // ---- Continuous renewal: last year's number, year changed, sequence kept ----
    // Ticket row 1: last year's number, year changed.
    [InlineData(2027, "R26ER5003240079PL", 2026, "A26ER5003240164PL", "PL", "", Renewal, "A27ER5003240164PL")]
    // Same sequence on both numbers: still last year's.
    [InlineData(2027, "R26ER5003240079PL", 2026, "A26ER5003240079PL", "PL", "", Renewal, "A27ER5003240079PL")]
    // Each agency, and an exporter.
    [InlineData(2027, "R26SR5001230011AL", 2026, "A26SR5001230042AL", "AL", "", Renewal, "A27SR5001230042AL")]
    [InlineData(2027, "R26WR5000950025PA", 2026, "A26WR5000950031PA", "PA", "", Renewal, "A27WR5000950031PA")]
    [InlineData(2027, "R26NR5004560003WO", 2026, "A26NR5004560008WO", "WO", "", Renewal, "A27NR5004560008WO")]
    [InlineData(2027, "R26EX5007770150FB", 2026, "A26EX5007770152FB", "FB", "", Renewal, "A27EX5007770152FB")]
    // A later year: 2027's number renewed for 2028.
    [InlineData(2028, "R27ER5002360007ST", 2027, "A27ER5002360583ST", "ST", "", Renewal, "A28ER5002360583ST")]
    // Ticket row 6: glass GL, other.
    [InlineData(2027, "R26ER5003240079GL", 2026, "A26ER5003240164GL", "GO", "", Renewal, "A27ER5003240164GO")]
    // Glass GL, remelt.
    [InlineData(2027, "R26ER5003240079GL", 2026, "A26ER5003240164GL", "GR", "", Renewal, "A27ER5003240164GR")]
    // Glass, process unchanged.
    [InlineData(2028, "R27ER5002260055GR", 2027, "A27ER5002260241GR", "GR", "", Renewal, "A28ER5002260241GR")]
    // Glass, process changed: this year's material, sequence kept.
    [InlineData(2028, "R27ER5002260055GR", 2027, "A27ER5002260241GR", "GO", "", Renewal, "A28ER5002260241GO")]
    [InlineData(2028, "R27ER5002260055GO", 2027, "A27ER5002260241GO", "GR", "", Renewal, "A28ER5002260241GR")]
    // ---- First-time: registration's agency, type and org; 0 + 3 random digits ----
    // Ticket row 2: first-time, registration's agency, org and material with 0 + 3 random digits.
    [InlineData(2027, "R26ER5003240079PL", null, null, "PL", "583", FirstTime, "A27ER5003240583PL")]
    // Registered the same year it's accredited.
    [InlineData(2027, "R27ER5003240012PL", null, null, "PL", "583", FirstTime, "A27ER5003240583PL")]
    // Each agency, and an exporter.
    [InlineData(2027, "R26SR5001230011AL", null, null, "AL", "7", FirstTime, "A27SR5001230007AL")]
    [InlineData(2027, "R26WR5000950025PA", null, null, "PA", "999", FirstTime, "A27WR5000950999PA")]
    [InlineData(2027, "R26NR5004560003WO", null, null, "WO", "0", FirstTime, "A27NR5004560000WO")]
    [InlineData(2027, "R26EX5007770150FB", null, null, "FB", "42", FirstTime, "A27EX5007770042FB")]
    // The draw matches the registration's sequence, so it's drawn again.
    [InlineData(2027, "R26ER5003240079PL", null, null, "PL", "79,583", FirstTime, "A27ER5003240583PL")]
    [InlineData(2027, "R26ER5003240079PL", null, null, "PL", "79,79,79,1", FirstTime, "A27ER5003240001PL")]
    // Only the registration's exact sequence is excluded: 1079 doesn't rule out 0079.
    [InlineData(2027, "R26ER5003241079PL", null, null, "PL", "79", FirstTime, "A27ER5003240079PL")]
    // Ticket rows 4 and 5: glass GL becomes GR or GO.
    [InlineData(2027, "R26ER5003240079GL", null, null, "GR", "241", FirstTime, "A27ER5003240241GR")]
    [InlineData(2027, "R26ER5003240079GL", null, null, "GO", "241", FirstTime, "A27ER5003240241GO")]
    // Glass registration already GR or GO: the application's process still decides.
    [InlineData(2027, "R26ER5002260055GR", null, null, "GR", "241", FirstTime, "A27ER5002260241GR")]
    [InlineData(2027, "R26ER5002260055GR", null, null, "GO", "241", FirstTime, "A27ER5002260241GO")]
    // ---- Renewal after a break: built as first-time ----
    // Ticket row 3: accredited 2026, not 2027, approved 2028; built as first-time.
    [InlineData(2028, "R26ER5003240079PL", 2026, "A26ER5003240164PL", "PL", "372", AfterBreak, "A28ER5003240372PL")]
    // Neither the registration's nor the earlier accreditation's sequence is reused.
    [InlineData(2028, "R26ER5003240079PL", 2026, "A26ER5003240164PL", "PL", "79,164,372", AfterBreak, "A28ER5003240372PL")]
    // A longer break.
    [InlineData(2030, "R29ER5003240079PL", 2026, "A26ER5003240164PL", "PL", "815", AfterBreak, "A30ER5003240815PL")]
    // Registration renewed during the break: the current registration is used.
    [InlineData(2028, "R27ER5003240091PL", 2026, "A26ER5003240164PL", "PL", "91,372", AfterBreak, "A28ER5003240372PL")]
    // Exporter.
    [InlineData(2028, "R26SX5001230011AL", 2026, "A26SX5001230042AL", "AL", "42,11,500", AfterBreak, "A28SX5001230500AL")]
    // Glass from GL, and glass whose process changed during the break.
    [InlineData(2028, "R26ER5003240079GL", 2026, "A26ER5003240164GL", "GR", "241", AfterBreak, "A28ER5003240241GR")]
    [InlineData(2029, "R27ER5002260055GO", 2027, "A27ER5002260241GO", "GR", "241,300", AfterBreak, "A29ER5002260300GR")]
    public void Examples(
        int approved,
        string registration,
        int? lastYear,
        string? lastNumber,
        string material,
        string draws,
        AccreditationNumberCase expectedCase,
        string expected
    )
    {
        var result = AccreditationNumberRules.Generate(
            approved,
            registration,
            lastYear is null ? null : LastYears(lastYear.Value, lastNumber!),
            material,
            Draws(draws),
            []
        );

        result.Error.Should().BeNull();
        result.Case.Should().Be(expectedCase);
        result.AccreditationNumber.Should().Be(expected);
    }

    // RA-783 AC04: a new sequence avoids every sequence the organisation's other sites use under the
    // same agency, process type and OrgId, in any year or material, so it can't take the number
    // another site's renewal will need. Other agencies, process types and orgs don't count.
    [Theory]
    [InlineData("A26ER5003240164PL", "164,583", "A27ER5003240583PL")]
    [InlineData("A25ER5003240164WO", "164,583", "A27ER5003240583PL")]
    [InlineData("A26ER5003240164GL", "164,583", "A27ER5003240583PL")]
    [InlineData("A26SR5003240164PL", "164", "A27ER5003240164PL")]
    [InlineData("A26EX5003240164PL", "164", "A27ER5003240164PL")]
    [InlineData("A26ER5003250164PL", "164", "A27ER5003240164PL")]
    [InlineData("A26ER500324164PL", "164", "A27ER5003240164PL")]
    public void NewSequence_AvoidsTheOrganisationsOtherSites(
        string otherSitesNumber,
        string draws,
        string expected
    ) =>
        AccreditationNumberRules
            .Generate(2027, "R26ER5003240079PL", null, "PL", Draws(draws), [otherSitesNumber])
            .AccreditationNumber.Should()
            .Be(expected);

    [Fact]
    public void ContinuousRenewal_IgnoresTheOrganisationsOtherSites()
    {
        var result = AccreditationNumberRules.Generate(
            2027,
            "R26ER5003240079PL",
            LastYears(2026, "A26ER5003240164PL"),
            "PL",
            Draws(""),
            ["A26ER5003240164PL", "A27ER5003240164PL"]
        );

        result.AccreditationNumber.Should().Be("A27ER5003240164PL");
    }

    [Theory]
    [InlineData(2027, null, AccreditationNumberCase.FirstTime)]
    [InlineData(2027, 2026, AccreditationNumberCase.ContinuousRenewal)]
    [InlineData(2028, 2026, AccreditationNumberCase.RenewalAfterBreak)]
    [InlineData(2030, 2026, AccreditationNumberCase.RenewalAfterBreak)]
    public void CaseFor_ChoosesTheRuleFromTheLatestAccreditedYear(
        int yearAppliedFor,
        int? latestAccreditedYear,
        AccreditationNumberCase expected
    ) =>
        AccreditationNumberRules.CaseFor(yearAppliedFor, latestAccreditedYear).Should().Be(expected);

    [Fact]
    public void GlIsNeverIssued()
    {
        var result = AccreditationNumberRules.Generate(
            2027,
            "R26ER5003240079GL",
            null,
            "GL",
            Draws("241"),
            []
        );

        result.IsValid.Should().BeFalse();
        result.Error.Should().Contain("'GL'");
    }

    [Theory]
    [InlineData("A26ER5003240079PL")]
    [InlineData("R26ER500324079PL")]
    [InlineData("r26ER5003240079PL")]
    [InlineData("R26ER5003240079XX")]
    [InlineData("")]
    [InlineData(null)]
    public void FirstTime_RejectsAnInvalidRegistrationNumber(string? registrationNumber)
    {
        var result = AccreditationNumberRules.Generate(
            2027,
            registrationNumber,
            null,
            "PL",
            Draws("583"),
            []
        );

        result.IsValid.Should().BeFalse();
        result.Error.Should().Contain("registration number");
    }

    [Theory]
    [InlineData("R26ER5003240164PL")]
    [InlineData("A26ER500324164PL")]
    [InlineData("")]
    public void ContinuousRenewal_RejectsAnInvalidLastYearsNumber_WithoutFallingBack(
        string lastYears
    )
    {
        var result = AccreditationNumberRules.Generate(
            2027,
            "R26ER5003240079PL",
            LastYears(2026, lastYears),
            "PL",
            Draws(""),
            []
        );

        result.IsValid.Should().BeFalse();
        result.Error.Should().Contain("Last year's accreditation number");
    }

    [Theory]
    [InlineData(2027)]
    [InlineData(2028)]
    public void RejectsAnEarlierAccreditationThatIsNotBeforeTheYearAppliedFor(int priorYear)
    {
        var result = AccreditationNumberRules.Generate(
            2027,
            "R26ER5003240079PL",
            LastYears(priorYear, "A27ER5003240164PL"),
            "PL",
            Draws(""),
            []
        );

        result.IsValid.Should().BeFalse();
        result.Error.Should().Contain("isn't before 2027");
    }

    // An organisation using nearly every sequence can run out of draws. That's a conflict the
    // endpoint returns as a 409, not bad data and not an exception (a 500).
    [Fact]
    public void ReportsNoSequenceLeft_WhenEveryDrawIsAnExcludedSequence()
    {
        var result = AccreditationNumberRules.Generate(
            2027,
            "R26ER5003240079PL",
            null,
            "PL",
            () => 79,
            []
        );

        result.IsValid.Should().BeFalse();
        result.NoSequenceLeft.Should().BeTrue();
        result.AccreditationNumber.Should().BeNull();
    }

    [Fact]
    public void AnInvalidSource_IsNotReportedAsNoSequenceLeft() =>
        AccreditationNumberRules
            .Generate(2027, "R26ER500324079PL", null, "PL", Draws("583"), [])
            .NoSequenceLeft.Should()
            .BeFalse();

    // RA-783: for a glass application holding no recycling process, GR or GO is taken from the
    // latest accreditation number, then the registration number. 2026 numbers from ReEx end in GL,
    // which says neither.
    [Theory]
    [InlineData("A26ER5003240164GR", "R26ER5003240079GO", "GR")]
    [InlineData("A26ER5003240164GO", "R26ER5003240079GR", "GO")]
    [InlineData("A26ER5003240164GL", "R26ER5003240079GR", "GR")]
    [InlineData(null, "R26ER5003240079GO", "GO")]
    [InlineData("A26ER5003240164GL", "R26ER5003240079GL", null)]
    [InlineData(null, "R26ER5003240079GL", null)]
    [InlineData(null, null, null)]
    [InlineData("A26ER500324164GR", "R26ER500324079GO", null)]
    public void GlassMaterialCodeFrom_TakesGrOrGoFromTheEarlierNumbers(
        string? latestNumber,
        string? registrationNumber,
        string? expected
    ) =>
        AccreditationNumberRules
            .GlassMaterialCodeFrom(
                latestNumber is null ? null : LastYears(2026, latestNumber),
                registrationNumber
            )
            .Should()
            .Be(expected);

    [Theory]
    [InlineData("A26ER500324164PL")]
    [InlineData("R26ER5003240164PL")]
    public void RenewalAfterBreak_RejectsAnInvalidEarlierNumber_RatherThanIgnoringIt(string earlier)
    {
        var result = AccreditationNumberRules.Generate(
            2028,
            "R26ER5003240079PL",
            LastYears(2026, earlier),
            "PL",
            Draws("372"),
            []
        );

        result.IsValid.Should().BeFalse();
        result.Error.Should().Contain(earlier);
    }

    [Theory]
    [InlineData(AccreditationReferenceSourceKind.LastYearsAccreditationNumber, "Last year's accreditation number")]
    [InlineData(AccreditationReferenceSourceKind.PreviousAccreditation, "The previous accreditation reference")]
    public void AnInvalidEarlierNumber_IsNamedByItsSource(
        AccreditationReferenceSourceKind source,
        string expected
    )
    {
        var result = AccreditationNumberRules.Generate(
            2027,
            "R26ER5003240079PL",
            new PriorAccreditation(2026, "A26ER500324164PL", source),
            "PL",
            Draws(""),
            []
        );

        result.Error.Should().StartWith(expected);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(1000)]
    public void Throws_WhenTheRandomSourceIsOutOfRange(int draw)
    {
        var act = () =>
            AccreditationNumberRules.Generate(
                2027,
                "R26ER5003240079PL",
                null,
                "PL",
                () => draw,
                []
            );

        act.Should().Throw<InvalidOperationException>();
    }

    private static PriorAccreditation LastYears(int year, string number) =>
        new(year, number, AccreditationReferenceSourceKind.LastYearsAccreditationNumber);

    // Returns the given comma-separated draws in turn. A renewal must draw nothing, so "" fails
    // the test if a draw is asked for.
    private static Func<int> Draws(string draws)
    {
        var queue = new Queue<int>(
            draws.Split(',', StringSplitOptions.RemoveEmptyEntries).Select(int.Parse)
        );
        return () =>
            queue.Count > 0
                ? queue.Dequeue()
                : throw new InvalidOperationException("Drew more random numbers than expected.");
    }
}
