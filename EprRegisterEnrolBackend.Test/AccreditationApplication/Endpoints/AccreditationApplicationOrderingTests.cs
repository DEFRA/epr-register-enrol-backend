using EprRegisterEnrolBackend.AccreditationApplication.Endpoints;
using EprRegisterEnrolBackend.AccreditationApplication.Models;
using FluentAssertions;
using MongoDB.Bson;

namespace EprRegisterEnrolBackend.Test.AccreditationApplication.Endpoints;

// RA-357: NewestFirst is the single definition of "the live one" — shared by the Seed idempotency
// lookup and the GetList response, and mirrored by the frontend. Tested directly here so the rule
// itself is pinned in one place; the endpoint tests then assert each call site delegates to it.
public class AccreditationApplicationOrderingTests
{
    private static AccreditationApplicationModel App(DateTime createdAt, ObjectId? id = null) =>
        new()
        {
            Id = id ?? ObjectId.GenerateNewId(),
            OrganisationId = "org-123",
            Year = 2026,
            MaterialType = MaterialType.Steel,
            CreatedAt = createdAt,
        };

    private static DateTime Utc(int month) => new(2026, month, 1, 0, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void NewestFirst_OrdersByCreatedAtDescending()
    {
        var oldest = App(Utc(1));
        var middle = App(Utc(6));
        var newest = App(Utc(9));

        // Supplied oldest-first so a pass cannot come from incidental input order.
        var ordered = new[] { oldest, middle, newest }.NewestFirst().ToList();

        ordered.Should().ContainInOrder(newest, middle, oldest);
    }

    [Fact]
    public void NewestFirst_WithEqualCreatedAt_BreaksTheTieByIdDescending()
    {
        var createdAt = Utc(4);
        var idA = ObjectId.GenerateNewId();
        var idB = ObjectId.GenerateNewId();
        var lower = idA < idB ? idA : idB;
        var higher = idA < idB ? idB : idA;

        var ordered = new[] { App(createdAt, lower), App(createdAt, higher) }
            .NewestFirst()
            .ToList();

        ordered.Select(a => a.Id).Should().ContainInOrder(higher, lower);
    }

    [Fact]
    public void NewestFirst_PrefersCreatedAtOverId()
    {
        // The newer record deliberately carries the LOWER id, so an id-only sort would invert this.
        var lowerId = ObjectId.GenerateNewId();
        var higherId = ObjectId.GenerateNewId();
        var older = App(Utc(1), higherId);
        var newer = App(Utc(2), lowerId);

        var ordered = new[] { older, newer }.NewestFirst().ToList();

        ordered.Should().ContainInOrder(newer, older);
    }

    [Fact]
    public void NewestFirst_WithNoApplications_ReturnsEmpty()
    {
        Array.Empty<AccreditationApplicationModel>().NewestFirst().Should().BeEmpty();
    }

    [Fact]
    public void NewestFirst_WithSingleApplication_ReturnsIt()
    {
        var only = App(Utc(3));

        new[] { only }.NewestFirst().Should().ContainSingle().Which.Should().BeSameAs(only);
    }

    [Fact]
    public void NewestFirst_WithNullId_SortsItLastAmongEqualCreatedAt()
    {
        // Id is nullable on the model (unset until persisted). Nullable ordering puts null lowest,
        // so descending places it last — pinned here so an unsaved record can never win the tie.
        var createdAt = Utc(5);
        var withId = App(createdAt);
        var withoutId = App(createdAt);
        withoutId.Id = null;

        var ordered = new[] { withoutId, withId }.NewestFirst().ToList();

        ordered.Should().ContainInOrder(withId, withoutId);
    }

    [Fact]
    public void NewestFirst_DoesNotMutateTheSource()
    {
        var oldest = App(Utc(1));
        var newest = App(Utc(9));
        var source = new[] { oldest, newest };

        source.NewestFirst().ToList();

        source.Should().ContainInOrder(oldest, newest);
    }
}
