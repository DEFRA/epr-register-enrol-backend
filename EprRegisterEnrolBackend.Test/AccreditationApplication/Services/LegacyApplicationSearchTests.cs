using EprRegisterEnrolBackend.AccreditationApplication.Models;
using EprRegisterEnrolBackend.AccreditationApplication.Services;
using EprRegisterEnrolBackend.Test.TestSupport;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using MongoDB.Bson;

namespace EprRegisterEnrolBackend.Test.AccreditationApplication.Services;

/// <summary>
/// RA-740: the admin API's search over every Legacy application, against a real mongod, so the
/// regex and $expr filters and the sort are what the server actually does.
/// </summary>
public sealed class LegacyApplicationSearchTests : IDisposable
{
    private static readonly DateTime Now = new(2026, 11, 19, 12, 0, 0, DateTimeKind.Utc);

    private readonly string _connectionString;
    private readonly string _databaseName;
    private readonly TestMongoDbClientFactory _factory;
    private readonly AccreditationApplicationPersistence _sut;

    public LegacyApplicationSearchTests(MongoIntegrationFixture fixture)
    {
        _connectionString = fixture.ConnectionString;
        _databaseName = MongoIntegrationFixture.NewDatabaseName("legacy_search");
        _factory = new TestMongoDbClientFactory(fixture.ConnectionString, _databaseName);
        _sut = new AccreditationApplicationPersistence(_factory, NullLoggerFactory.Instance);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose() => _factory.GetClient().DropDatabase(_databaseName);

    private async Task<AccreditationApplicationModel> Seed(
        string? accreditation = null,
        string? registration = null,
        int? orgId = null,
        string? name = null,
        ApplicationStatus status = ApplicationStatus.Saved,
        int year = 2027,
        DateTime? updatedAt = null,
        ObjectId? id = null
    )
    {
        var application = new AccreditationApplicationModel
        {
            Id = id,
            OrganisationId = "reex-org",
            AccreditationReference = accreditation,
            RegistrationReference = registration,
            OrgId = orgId,
            OrganisationName = name,
            ApplicationStatus = status,
            Year = year,
            MaterialType = MaterialType.Plastic,
            UpdatedAt = updatedAt ?? Now,
        };
        (await _sut.CreateAsync(application)).Should().NotBeNull();
        return application;
    }

    private async Task<IReadOnlyList<AccreditationApplicationModel>> Search(
        LegacyApplicationSearch search,
        long skip = 0,
        int limit = 100
    ) => (await _sut.SearchLegacyApplicationsAsync(search, skip, limit, cancellationToken: Ct)).Items;

    private static LegacyApplicationSearch Text(
        string text,
        params LegacyApplicationSearchField[] fields
    ) => new(text, fields);

    [Fact]
    public async Task NoCriteria_FindsEveryApplication_WhateverItsStatusOrYear()
    {
        await Seed(status: ApplicationStatus.Saved, year: 2025);
        await Seed(status: ApplicationStatus.Approved, year: 2027);
        await Seed(status: ApplicationStatus.Withdrawn, year: 2026);

        var (items, total) = await _sut.SearchLegacyApplicationsAsync(new(), 0, 50, cancellationToken: Ct);

        items.Should().HaveCount(3);
        total.Should().Be(3);
    }

    [Fact]
    public async Task AccreditationNumber_MatchesPartOfTheAccreditationReference()
    {
        var match = await Seed(accreditation: "A27ER5001120018PL");
        await Seed(accreditation: "A27ER5009990018PL", registration: "R25ER5001120018PL");

        var found = await Search(Text("50011200", LegacyApplicationSearchField.AccreditationNumber));

        found.Select(a => a.Id).Should().Equal(match.Id);
    }

    [Fact]
    public async Task RegistrationNumber_MatchesPartOfTheRegistrationReference()
    {
        var match = await Seed(registration: "R25EX5001120018PL");
        await Seed(registration: "R25ER5001120018PL", accreditation: "A27EX5001120018PL");

        var found = await Search(Text("R25EX", LegacyApplicationSearchField.RegistrationNumber));

        found.Select(a => a.Id).Should().Equal(match.Id);
    }

    [Fact]
    public async Task OrganisationNumber_MatchesPartOfTheNumber_AsText()
    {
        var match = await Seed(orgId: 500112);
        await Seed(orgId: 600212, name: "500112 Recycling");
        await Seed(orgId: null);

        var found = await Search(Text("0011", LegacyApplicationSearchField.OrganisationNumber));

        found.Select(a => a.Id).Should().Equal(match.Id);
    }

    [Fact]
    public async Task OrganisationName_MatchesPartOfTheName_IgnoringCase()
    {
        var match = await Seed(name: "Harbourside Plastics Ltd");
        await Seed(name: "Inland Plastics Ltd", registration: "R25HARBOURSIDE");

        var found = await Search(Text("HARBOURSIDE", LegacyApplicationSearchField.OrganisationName));

        found.Select(a => a.Id).Should().Equal(match.Id);
    }

    [Fact]
    public async Task NoFields_SearchesAllFour_AndAnyMatchIsEnough()
    {
        var byAccreditation = await Seed(accreditation: "A27-KEY");
        var byRegistration = await Seed(registration: "R25-key");
        var byName = await Seed(name: "Key Recycling");
        var byOrg = await Seed(orgId: 500777);
        await Seed(accreditation: "A27-OTHER", registration: "R25-OTHER", name: "Other", orgId: 500888);

        var text = await Search(new LegacyApplicationSearch("key"));
        var number = await Search(new LegacyApplicationSearch("777"));

        text.Select(a => a.Id)
            .Should()
            .BeEquivalentTo([byAccreditation.Id, byRegistration.Id, byName.Id]);
        number.Select(a => a.Id).Should().Equal(byOrg.Id);
    }

    [Fact]
    public async Task SeveralFields_MatchWhenAnyOfThemContainsTheText()
    {
        var byRegistration = await Seed(registration: "R25-ACME");
        var byName = await Seed(name: "Acme Ltd");
        await Seed(accreditation: "A27-ACME");

        var found = await Search(
            Text(
                "acme",
                LegacyApplicationSearchField.RegistrationNumber,
                LegacyApplicationSearchField.OrganisationName
            )
        );

        found.Select(a => a.Id).Should().BeEquivalentTo([byRegistration.Id, byName.Id]);
    }

    [Theory]
    [InlineData(".*")]
    [InlineData("a.c")]
    [InlineData("(x)")]
    [InlineData("[ab]")]
    [InlineData("^Ltd")]
    [InlineData("+")]
    [InlineData("\\d")]
    public async Task RegexCharacters_MatchThemselves(string text)
    {
        var literal = await Seed(name: $"Name {text} here");
        await Seed(name: "abc Ltd x 1 b");

        var found = await Search(new LegacyApplicationSearch(text));

        found.Select(a => a.Id).Should().Equal(literal.Id);
    }

    [Fact]
    public async Task Statuses_CombineWithOr()
    {
        var approved = await Seed(status: ApplicationStatus.Approved);
        var withdrawn = await Seed(status: ApplicationStatus.Withdrawn);
        await Seed(status: ApplicationStatus.Submitted);

        var found = await Search(
            new LegacyApplicationSearch(
                Statuses: [ApplicationStatus.Approved, ApplicationStatus.Withdrawn]
            )
        );

        found.Select(a => a.Id).Should().BeEquivalentTo([approved.Id, withdrawn.Id]);
    }

    [Fact]
    public async Task Year_NarrowsToThatYear()
    {
        var match = await Seed(year: 2026);
        await Seed(year: 2027);

        var found = await Search(new LegacyApplicationSearch(Year: 2026));

        found.Select(a => a.Id).Should().Equal(match.Id);
    }

    [Fact]
    public async Task TextStatusAndYear_CombineWithAnd()
    {
        var match = await Seed(name: "Acme", status: ApplicationStatus.Approved, year: 2027);
        await Seed(name: "Acme", status: ApplicationStatus.Approved, year: 2026);
        await Seed(name: "Acme", status: ApplicationStatus.Rejected, year: 2027);
        await Seed(name: "Other", status: ApplicationStatus.Approved, year: 2027);

        var found = await Search(
            new LegacyApplicationSearch("acme", null, [ApplicationStatus.Approved], 2027)
        );

        found.Select(a => a.Id).Should().Equal(match.Id);
    }

    [Fact]
    public async Task FieldsWithoutText_HaveNoEffect()
    {
        await Seed(name: "Acme");
        await Seed(name: null);

        var found = await Search(
            new LegacyApplicationSearch(null, [LegacyApplicationSearchField.OrganisationName])
        );

        found.Should().HaveCount(2);
    }

    [Fact]
    public async Task Order_IsMostRecentlyUpdatedFirst_ThenIdDescending()
    {
        var oldest = await Seed(updatedAt: Now.AddDays(-2));
        var tiedLow = await Seed(updatedAt: Now, id: ObjectId.Parse("000000000000000000000001"));
        var tiedHigh = await Seed(updatedAt: Now, id: ObjectId.Parse("000000000000000000000002"));
        var newest = await Seed(updatedAt: Now.AddDays(1));

        var found = await Search(new LegacyApplicationSearch());

        found.Select(a => a.Id).Should().Equal(newest.Id, tiedHigh.Id, tiedLow.Id, oldest.Id);
    }

    [Fact]
    public async Task Paging_ReturnsThePage_AndTheTotalAcrossAllPages()
    {
        var applications = new List<AccreditationApplicationModel>();
        for (var i = 0; i < 5; i++)
            applications.Add(await Seed(updatedAt: Now.AddMinutes(-i)));

        var (page2, total) = await _sut.SearchLegacyApplicationsAsync(new(), 2, 2, cancellationToken: Ct);
        var (past, pastTotal) = await _sut.SearchLegacyApplicationsAsync(new(), 10, 2, cancellationToken: Ct);
        var (farPast, farTotal) = await _sut.SearchLegacyApplicationsAsync(
            new(),
            (long)int.MaxValue + 1,
            2,
            cancellationToken: Ct
        );

        page2.Select(a => a.Id).Should().Equal(applications[2].Id, applications[3].Id);
        total.Should().Be(5);
        past.Should().BeEmpty();
        pastTotal.Should().Be(5);
        farPast.Should().BeEmpty();
        farTotal.Should().Be(5);
    }

    [Fact]
    public async Task TheQueryTimeLimit_IsSentWithBothQueries()
    {
        var commands = new List<BsonDocument>();
        var databaseName = MongoIntegrationFixture.NewDatabaseName("legacy_search_watch");
        var factory = TestMongoDbClientFactory.WatchingCommands(
            _connectionString,
            databaseName,
            commands
        );
        try
        {
            var sut = new AccreditationApplicationPersistence(factory, NullLoggerFactory.Instance);
            await sut.SearchLegacyApplicationsAsync(
                new("x"),
                0,
                10,
                TimeSpan.FromSeconds(3),
                Ct
            );

            lock (commands)
            {
                commands
                    .Where(c => c.Contains("aggregate") || c.Contains("find"))
                    .Should()
                    .HaveCount(2)
                    .And.OnlyContain(c => c["maxTimeMS"].ToInt64() == 3000);
            }
        }
        finally
        {
            factory.GetClient().DropDatabase(databaseName, Ct);
        }
    }
}
