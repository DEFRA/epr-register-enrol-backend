using EprRegisterEnrolBackend.AccreditationApplication.Endpoints;
using EprRegisterEnrolBackend.AccreditationApplication.Models;
using EprRegisterEnrolBackend.DomainModel.ReadModel;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using MongoDB.Bson;
using NSubstitute;

namespace EprRegisterEnrolBackend.Test.AccreditationApplication.Endpoints;

/// <summary>
/// RA-705: a write's whole-application response (Ok, or a seed's Created) is served again through the reader once the
/// write has committed. Write responses are strict like reads: when the configured source doesn't hold the application
/// the answer is 404 (decided 2026-11-14, once the seed writes the initial Application version).
/// </summary>
public sealed class ApplicationResponseReaderFilterTests
{
    private readonly IApplicationReader _reader = Substitute.For<IApplicationReader>();
    private static readonly string ApplicationId = ObjectId.GenerateNewId().ToString();

    private static AccreditationApplicationModel Application(string name) =>
        new()
        {
            Id = ObjectId.GenerateNewId(),
            OrganisationId = "org",
            OrganisationName = name,
            MaterialType = MaterialType.Steel,
            Year = 2027,
        };

    private ValueTask<object?> InvokeAsync(
        string method,
        object? handlerResult,
        int? siteId = null
    )
    {
        var http = new DefaultHttpContext();
        http.Request.Method = method;
        http.Request.RouteValues["organisationId"] = "org";
        http.Request.RouteValues["applicationId"] = ApplicationId;
        if (siteId is not null)
        {
            http.Request.RouteValues["siteId"] = siteId.Value.ToString(
                System.Globalization.CultureInfo.InvariantCulture
            );
        }

        return new ApplicationResponseReaderFilter(_reader).InvokeAsync(
            new DefaultEndpointFilterInvocationContext(http),
            _ => ValueTask.FromResult(handlerResult)
        );
    }

    [Fact]
    public async Task Ok_IsServedThroughTheReader()
    {
        var legacy = Application("legacy");
        var served = Application("model");
        _reader.ServeFromSourceAsync(legacy, Arg.Any<CancellationToken>()).Returns(served);

        var result = await InvokeAsync(HttpMethods.Patch, TypedResults.Ok(legacy));

        result
            .Should()
            .BeOfType<Ok<AccreditationApplicationModel>>()
            .Which.Value.Should()
            .BeSameAs(served);
    }

    [Fact]
    public async Task Ok_NotHeldByTheSource_Answers404()
    {
        var legacy = Application("legacy");
        _reader
            .ServeFromSourceAsync(legacy, Arg.Any<CancellationToken>())
            .Returns((AccreditationApplicationModel?)null);

        var result = await InvokeAsync(HttpMethods.Patch, TypedResults.Ok(legacy));

        result.Should().BeOfType<NotFound>();
    }

    [Fact]
    public async Task Created_IsServedThroughTheReader_KeepingItsLocation()
    {
        var legacy = Application("legacy");
        var served = Application("model");
        _reader.ServeFromSourceAsync(legacy, Arg.Any<CancellationToken>()).Returns(served);

        var result = await InvokeAsync(
            HttpMethods.Post,
            TypedResults.Created("/somewhere/1", legacy)
        );

        var created = result.Should().BeOfType<Created<AccreditationApplicationModel>>().Subject;
        created.Value.Should().BeSameAs(served);
        created.Location.Should().Be("/somewhere/1");
    }

    [Fact]
    public async Task Created_NotHeldByTheSource_Answers404()
    {
        var legacy = Application("legacy");
        _reader
            .ServeFromSourceAsync(legacy, Arg.Any<CancellationToken>())
            .Returns((AccreditationApplicationModel?)null);

        var result = await InvokeAsync(
            HttpMethods.Post,
            TypedResults.Created("/somewhere/1", legacy)
        );

        result.Should().BeOfType<NotFound>();
    }

    [Fact]
    public async Task Get_IsLeftAlone()
    {
        var legacy = Application("legacy");
        var ok = TypedResults.Ok(legacy);

        var result = await InvokeAsync(HttpMethods.Get, ok);

        result.Should().BeSameAs(ok);
        await _reader.DidNotReceiveWithAnyArgs().ServeFromSourceAsync(default!, default);
    }

    [Fact]
    public async Task OtherResults_PassThrough()
    {
        var site = TypedResults.Created("/site/1", new { siteName = "A site" });

        var result = await InvokeAsync(HttpMethods.Post, site);

        result.Should().BeSameAs(site);
        await _reader.DidNotReceiveWithAnyArgs().ServeFromSourceAsync(default!, default);
    }

    // Site and interim-site write responses (ORS add, update, promote, revert and recycling operations; interim add,
    // update and restore) are served from the source's application too: the site with the same SiteId, and for an
    // interim site the one with the same SiteId under the route's ORS.
    private static OverseasSiteModel Site(int siteId, string name, params InterimSiteModel[] interims) =>
        new()
        {
            SiteId = siteId,
            SiteName = name,
            InterimSites = [.. interims],
        };

    private static InterimSiteModel Interim(int siteId, string name) =>
        new()
        {
            SiteId = siteId,
            SiteNumber = "00" + siteId,
            Country = "Belgium",
            SiteName = name,
            AddressLine1 = "1 Quai",
            TownOrCity = "Antwerp",
            ContactName = "Contact",
            ContactEmail = "contact@example.com",
            ContactPhone = "000",
        };

    private void SourceHolds(params OverseasSiteModel[] sites)
    {
        var application = Application("model");
        application.OverseasSites = new AccreditationApplicationOverseasSites { Sites = [.. sites] };
        _reader.GetByIdAsync("org", ApplicationId, Arg.Any<CancellationToken>()).Returns(application);
    }

    [Fact]
    public async Task Site_Ok_IsTheSourcesSite()
    {
        SourceHolds(Site(1, "other"), Site(2, "from the model"));

        var result = await InvokeAsync(HttpMethods.Patch, TypedResults.Ok(Site(2, "from legacy")), siteId: 2);

        result
            .Should()
            .BeOfType<Ok<OverseasSiteModel>>()
            .Which.Value!.SiteName.Should()
            .Be("from the model");
    }

    [Fact]
    public async Task Site_Created_IsTheSourcesSite_KeepingItsLocation()
    {
        SourceHolds(Site(4, "from the model"));

        var result = await InvokeAsync(
            HttpMethods.Post,
            TypedResults.Created("/site/4", Site(4, "from legacy"))
        );

        var created = result.Should().BeOfType<Created<OverseasSiteModel>>().Subject;
        created.Value!.SiteName.Should().Be("from the model");
        created.Location.Should().Be("/site/4");
    }

    [Fact]
    public async Task Site_NotInTheSourcesApplication_Answers404()
    {
        SourceHolds(Site(1, "other"));

        var result = await InvokeAsync(HttpMethods.Patch, TypedResults.Ok(Site(2, "from legacy")), siteId: 2);

        result.Should().BeOfType<NotFound>();
    }

    [Fact]
    public async Task Site_ApplicationNotInTheSource_Answers404()
    {
        _reader
            .GetByIdAsync("org", ApplicationId, Arg.Any<CancellationToken>())
            .Returns((AccreditationApplicationModel?)null);

        var result = await InvokeAsync(HttpMethods.Patch, TypedResults.Ok(Site(2, "from legacy")), siteId: 2);

        result.Should().BeOfType<NotFound>();
    }

    [Fact]
    public async Task Interim_Ok_IsTheSourcesInterimUnderTheRoutesSite()
    {
        SourceHolds(
            Site(1, "a", Interim(7, "under another site")),
            Site(2, "b", Interim(7, "from the model"))
        );

        var result = await InvokeAsync(
            HttpMethods.Patch,
            TypedResults.Ok(Interim(7, "from legacy")),
            siteId: 2
        );

        result
            .Should()
            .BeOfType<Ok<InterimSiteModel>>()
            .Which.Value!.SiteName.Should()
            .Be("from the model");
    }

    [Fact]
    public async Task Interim_Created_IsTheSourcesInterim_KeepingItsLocation()
    {
        SourceHolds(Site(2, "b", Interim(9, "from the model")));

        var result = await InvokeAsync(
            HttpMethods.Post,
            TypedResults.Created("/interim/9", Interim(9, "from legacy")),
            siteId: 2
        );

        var created = result.Should().BeOfType<Created<InterimSiteModel>>().Subject;
        created.Value!.SiteName.Should().Be("from the model");
        created.Location.Should().Be("/interim/9");
    }

    [Fact]
    public async Task Interim_NotInTheSourcesApplication_Answers404()
    {
        SourceHolds(Site(2, "b"));

        var result = await InvokeAsync(
            HttpMethods.Patch,
            TypedResults.Ok(Interim(7, "from legacy")),
            siteId: 2
        );

        result.Should().BeOfType<NotFound>();
    }
}
