using EprRegisterEnrolBackend.AccreditationApplication.Models;
using EprRegisterEnrolBackend.AccreditationApplication.Services;
using FluentAssertions;

namespace EprRegisterEnrolBackend.Test.AccreditationApplication.Services;

// RA-292 AC03. Newness of an authority-to-issue contact is derived here and nowhere else, so
// every branch is pinned: the client's own isNew must never be able to reach persistence, in
// either direction.
public class PrnsAuthoriserMergeTests
{
    private static PrnsAuthoriser Authoriser(string fullName, string email, bool isNew = false) =>
        new()
        {
            FullName = fullName,
            Email = email,
            IsNew = isNew,
        };

    [Fact]
    public void Merge_EmailNotPreviouslyPersisted_FlagsAsNew()
    {
        var persisted = new[] { Authoriser("Old Hand", "old@example.com") };
        var incoming = new[]
        {
            Authoriser("Old Hand", "old@example.com"),
            Authoriser("Fresh Face", "fresh@example.com"),
        };

        var result = PrnsAuthoriserMerge.Merge(persisted, incoming);

        result.Should().HaveCount(2);
        result[0].IsNew.Should().BeFalse();
        result[1].IsNew.Should().BeTrue();
    }

    [Fact]
    public void Merge_EmptyPersistedList_FlagsEveryIncomingAuthoriserAsNew()
    {
        var result = PrnsAuthoriserMerge.Merge(
            [],
            [Authoriser("A", "a@example.com"), Authoriser("B", "b@example.com")]
        );

        result.Should().OnlyContain(a => a.IsNew);
    }

    [Fact]
    public void Merge_NullPersistedList_FlagsEveryIncomingAuthoriserAsNew()
    {
        var result = PrnsAuthoriserMerge.Merge(null, [Authoriser("A", "a@example.com")]);

        result.Should().ContainSingle().Which.IsNew.Should().BeTrue();
    }

    [Fact]
    public void Merge_NullIncomingList_ReturnsEmptyList()
    {
        var result = PrnsAuthoriserMerge.Merge([Authoriser("A", "a@example.com")], null);

        result.Should().BeEmpty();
    }

    [Fact]
    public void Merge_BothListsNull_ReturnsEmptyList()
    {
        PrnsAuthoriserMerge.Merge(null, null).Should().BeEmpty();
    }

    [Fact]
    public void Merge_ExistingEmailPreviouslyFlaggedNew_KeepsItFlaggedNew()
    {
        // A contact added earlier in this application stays "new" to the regulator across every
        // later save of the section, not just the save that introduced it.
        var persisted = new[] { Authoriser("Fresh Face", "fresh@example.com", isNew: true) };

        var result = PrnsAuthoriserMerge.Merge(
            persisted,
            [Authoriser("Fresh Face", "fresh@example.com")]
        );

        result.Should().ContainSingle().Which.IsNew.Should().BeTrue();
    }

    [Theory]
    [InlineData("OLD@EXAMPLE.COM")]
    [InlineData("Old@Example.Com")]
    [InlineData("  old@example.com  ")]
    [InlineData("\tOLD@EXAMPLE.COM\n")]
    public void Merge_EmailDiffersOnlyByCaseOrWhitespace_TreatedAsSameContact(string incomingEmail)
    {
        var persisted = new[] { Authoriser("Old Hand", "old@example.com") };

        var result = PrnsAuthoriserMerge.Merge(persisted, [Authoriser("Old Hand", incomingEmail)]);

        result.Should().ContainSingle().Which.IsNew.Should().BeFalse();
    }

    [Fact]
    public void Merge_PersistedEmailHasSurroundingWhitespace_StillMatches()
    {
        var persisted = new[] { Authoriser("Old Hand", "  old@example.com ") };

        var result = PrnsAuthoriserMerge.Merge(
            persisted,
            [Authoriser("Old Hand", "old@example.com")]
        );

        result.Should().ContainSingle().Which.IsNew.Should().BeFalse();
    }

    [Fact]
    public void Merge_ClientClaimsNotNewForUnknownEmail_CannotDowngradeServerDerivedTrue()
    {
        var result = PrnsAuthoriserMerge.Merge(
            [Authoriser("Old Hand", "old@example.com")],
            [Authoriser("Fresh Face", "fresh@example.com", isNew: false)]
        );

        result.Should().ContainSingle().Which.IsNew.Should().BeTrue();
    }

    [Fact]
    public void Merge_ClientClaimsNotNewForEmailPersistedAsNew_CannotDowngradeIt()
    {
        var persisted = new[] { Authoriser("Fresh Face", "fresh@example.com", isNew: true) };

        var result = PrnsAuthoriserMerge.Merge(
            persisted,
            [Authoriser("Fresh Face", "fresh@example.com", isNew: false)]
        );

        result.Should().ContainSingle().Which.IsNew.Should().BeTrue();
    }

    [Fact]
    public void Merge_ClientClaimsNewForAlreadyKnownEmail_CannotUpgradeIt()
    {
        // The other direction matters too: a client must not be able to invent a "New" badge for
        // a contact the regulator has already seen.
        var persisted = new[] { Authoriser("Old Hand", "old@example.com") };

        var result = PrnsAuthoriserMerge.Merge(
            persisted,
            [Authoriser("Old Hand", "old@example.com", isNew: true)]
        );

        result.Should().ContainSingle().Which.IsNew.Should().BeFalse();
    }

    [Fact]
    public void Merge_TakesFullNameAndEmailFromIncoming()
    {
        var persisted = new[] { Authoriser("Old Name", "old@example.com") };

        var result = PrnsAuthoriserMerge.Merge(
            persisted,
            [Authoriser("Renamed Person", "OLD@example.com")]
        );

        result.Should().ContainSingle();
        result[0].FullName.Should().Be("Renamed Person");
        result[0].Email.Should().Be("OLD@example.com");
    }

    [Fact]
    public void Merge_AuthoriserOmittedFromIncoming_IsNotResurrected()
    {
        var persisted = new[]
        {
            Authoriser("Kept", "kept@example.com"),
            Authoriser("Removed", "removed@example.com"),
        };

        var result = PrnsAuthoriserMerge.Merge(persisted, [Authoriser("Kept", "kept@example.com")]);

        result.Should().ContainSingle().Which.Email.Should().Be("kept@example.com");
    }

    [Fact]
    public void Merge_RemovedThenReAddedEmail_IsFlaggedNewAgain()
    {
        // Intended: once dropped, the email is no longer in the persisted list, so re-adding it
        // presents the regulator with a contact to review again.
        var afterRemoval = PrnsAuthoriserMerge.Merge(
            [Authoriser("Kept", "kept@example.com"), Authoriser("Gone", "gone@example.com")],
            [Authoriser("Kept", "kept@example.com")]
        );

        var afterReAdd = PrnsAuthoriserMerge.Merge(
            afterRemoval,
            [Authoriser("Kept", "kept@example.com"), Authoriser("Gone", "gone@example.com")]
        );

        afterReAdd[1].IsNew.Should().BeTrue();
    }

    [Fact]
    public void Merge_PersistedListHoldsDuplicateEmails_UsesFirstEntry()
    {
        var persisted = new[]
        {
            Authoriser("First", "dupe@example.com", isNew: false),
            Authoriser("Second", "dupe@example.com", isNew: true),
        };

        var result = PrnsAuthoriserMerge.Merge(
            persisted,
            [Authoriser("First", "dupe@example.com")]
        );

        result.Should().ContainSingle().Which.IsNew.Should().BeFalse();
    }

    // Email is non-nullable on the model, but `required` is satisfied by mere presence, so a body
    // of {"email": null} does reach us. Normalising null to empty keeps that from throwing.
    [Fact]
    public void Merge_IncomingEmailIsNull_DoesNotThrowAndFlagsAsNew()
    {
        var result = PrnsAuthoriserMerge.Merge(
            [Authoriser("Old Hand", "old@example.com")],
            [Authoriser("No Email", null!)]
        );

        result.Should().ContainSingle().Which.IsNew.Should().BeTrue();
    }

    [Fact]
    public void Merge_PersistedAndIncomingEmailsBothNull_TreatedAsSameContact()
    {
        var persisted = new[] { Authoriser("No Email", null!) };

        var result = PrnsAuthoriserMerge.Merge(persisted, [Authoriser("No Email", null!)]);

        result.Should().ContainSingle().Which.IsNew.Should().BeFalse();
    }

    [Fact]
    public void MarkAsExisting_EmailIsNull_DoesNotThrow()
    {
        PrnsAuthoriserMerge
            .MarkAsExisting([Authoriser("No Email", null!)])
            .Should()
            .ContainSingle()
            .Which.IsNew.Should()
            .BeFalse();
    }

    [Fact]
    public void Merge_EmptyIncomingList_ReturnsEmptyList()
    {
        PrnsAuthoriserMerge.Merge([Authoriser("A", "a@example.com")], []).Should().BeEmpty();
    }

    [Fact]
    public void Merge_DoesNotMutateThePersistedAuthorisers()
    {
        var persistedAuthoriser = Authoriser("Old Hand", "old@example.com");

        var result = PrnsAuthoriserMerge.Merge(
            [persistedAuthoriser],
            [Authoriser("Renamed", "old@example.com", isNew: true)]
        );

        persistedAuthoriser.FullName.Should().Be("Old Hand");
        persistedAuthoriser.IsNew.Should().BeFalse();
        result[0].Should().NotBeSameAs(persistedAuthoriser);
    }

    [Fact]
    public void MarkAsExisting_FlagsEveryAuthoriserAsNotNew()
    {
        var result = PrnsAuthoriserMerge.MarkAsExisting(
            [
                Authoriser("A", "a@example.com", isNew: true),
                Authoriser("B", "b@example.com", isNew: false),
            ]
        );

        result.Should().HaveCount(2);
        result.Should().OnlyContain(a => !a.IsNew);
        result[0].FullName.Should().Be("A");
        result[0].Email.Should().Be("a@example.com");
    }

    [Fact]
    public void MarkAsExisting_NullList_ReturnsEmptyList()
    {
        PrnsAuthoriserMerge.MarkAsExisting(null).Should().BeEmpty();
    }

    [Fact]
    public void MarkAsExisting_EmptyList_ReturnsEmptyList()
    {
        PrnsAuthoriserMerge.MarkAsExisting([]).Should().BeEmpty();
    }
}
