using EprRegisterEnrolBackend.DomainModel.Entities;

namespace EprRegisterEnrolBackend.Test.DomainModel.Entities;

// RA-763: the one rule for a Registration's accreditation numbers, used by the seed and the migrator.
public class RegistrationEntityAccreditationNumbersTests
{
    private static (int, string)[] Numbers(RegistrationEntity registration) =>
        [.. registration.AccreditationNumbers.Select(a => (a.Year, a.AccreditationNumber))];

    [Theory]
    [InlineData(HeldAccreditationNumber.Replace)]
    [InlineData(HeldAccreditationNumber.Keep)]
    public void A_year_not_held_is_added_oldest_first(HeldAccreditationNumber whenHeld)
    {
        var registration = new RegistrationEntity();
        registration.SetAccreditationNumber(2027, "A27NUMBER", whenHeld);

        var differed = registration.SetAccreditationNumber(2026, "A26NUMBER", whenHeld);

        Assert.False(differed);
        Assert.Equal([(2026, "A26NUMBER"), (2027, "A27NUMBER")], Numbers(registration));
    }

    [Theory]
    [InlineData(HeldAccreditationNumber.Replace)]
    [InlineData(HeldAccreditationNumber.Keep)]
    public void The_same_number_again_is_no_difference(HeldAccreditationNumber whenHeld)
    {
        var registration = new RegistrationEntity();
        registration.SetAccreditationNumber(2026, "A26NUMBER", whenHeld);

        var differed = registration.SetAccreditationNumber(2026, "A26NUMBER", whenHeld);

        Assert.False(differed);
        Assert.Equal([(2026, "A26NUMBER")], Numbers(registration));
    }

    [Fact]
    public void Replace_takes_a_different_number_for_a_held_year_and_says_so()
    {
        var registration = new RegistrationEntity();
        registration.SetAccreditationNumber(2026, "A26OLD", HeldAccreditationNumber.Replace);

        var differed = registration.SetAccreditationNumber(
            2026,
            "A26NEW",
            HeldAccreditationNumber.Replace
        );

        Assert.True(differed);
        Assert.Equal([(2026, "A26NEW")], Numbers(registration));
    }

    [Fact]
    public void Keep_leaves_the_held_number_for_the_year_and_says_they_differ()
    {
        var registration = new RegistrationEntity();
        registration.SetAccreditationNumber(2026, "A26HELD", HeldAccreditationNumber.Keep);

        var differed = registration.SetAccreditationNumber(
            2026,
            "A26OTHER",
            HeldAccreditationNumber.Keep
        );

        Assert.True(differed);
        Assert.Equal([(2026, "A26HELD")], Numbers(registration));
    }
}