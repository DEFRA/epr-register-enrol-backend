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

    private static AccreditationApplicationModel Application(string name) =>
        new()
        {
            Id = ObjectId.GenerateNewId(),
            OrganisationId = "org",
            OrganisationName = name,
            MaterialType = MaterialType.Steel,
            Year = 2027,
        };

    private ValueTask<object?> InvokeAsync(string method, object? handlerResult)
    {
        var http = new DefaultHttpContext();
        http.Request.Method = method;
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
        _reader.ServeAsync(legacy, Arg.Any<CancellationToken>()).Returns(served);

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
            .ServeAsync(legacy, Arg.Any<CancellationToken>())
            .Returns((AccreditationApplicationModel?)null);

        var result = await InvokeAsync(HttpMethods.Patch, TypedResults.Ok(legacy));

        result.Should().BeOfType<NotFound>();
    }

    [Fact]
    public async Task Created_IsServedThroughTheReader_KeepingItsLocation()
    {
        var legacy = Application("legacy");
        var served = Application("model");
        _reader.ServeAsync(legacy, Arg.Any<CancellationToken>()).Returns(served);

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
            .ServeAsync(legacy, Arg.Any<CancellationToken>())
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
        await _reader.DidNotReceiveWithAnyArgs().ServeAsync(default!, default);
    }

    [Fact]
    public async Task OtherResults_PassThrough()
    {
        var site = TypedResults.Created("/site/1", new { siteName = "A site" });

        var result = await InvokeAsync(HttpMethods.Post, site);

        result.Should().BeSameAs(site);
        await _reader.DidNotReceiveWithAnyArgs().ServeAsync(default!, default);
    }
}
