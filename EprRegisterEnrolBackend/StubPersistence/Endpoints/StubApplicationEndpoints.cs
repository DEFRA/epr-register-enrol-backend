using System.Text.Json;
using EprRegisterEnrolBackend.StubPersistence.Models;
using EprRegisterEnrolBackend.StubPersistence.Services;
using MongoDB.Bson;
using MongoDB.Bson.IO;

namespace EprRegisterEnrolBackend.StubPersistence.Endpoints;

public static class StubApplicationEndpoints
{
    private static readonly JsonWriterSettings BsonJsonSettings =
        new() { OutputMode = JsonOutputMode.RelaxedExtendedJson };

    public static void UseStubApplicationEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("api/v1/stub/accreditation-applications");

        group.MapGet("{orgId}", GetList);
        group.MapGet("{orgId}/{appId}", GetById);
        group.MapPut("{orgId}/{appId}", Upsert);
    }

    private static async Task<IResult> GetList(string orgId, IStubApplicationPersistence persistence)
    {
        var documents = await persistence.GetByOrgAsync(orgId);
        var jsonArray = "[" + string.Join(",", documents.Select(d => d.Data.ToJson(BsonJsonSettings))) + "]";
        return Results.Text(jsonArray, "application/json");
    }

    private static async Task<IResult> GetById(string orgId, string appId, IStubApplicationPersistence persistence)
    {
        var document = await persistence.GetByIdAsync(orgId, appId);
        if (document is null)
            return Results.NotFound();

        return Results.Text(document.Data.ToJson(BsonJsonSettings), "application/json");
    }

    private static async Task<IResult> Upsert(string orgId, string appId, JsonElement body, IStubApplicationPersistence persistence)
    {
        var document = new StubApplicationDocument
        {
            StubApplicationId = appId,
            OrganisationId = orgId,
            SiteId = body.TryGetProperty("siteId", out var siteId) ? siteId.GetString() : null,
            MaterialType = body.TryGetProperty("materialType", out var materialType) ? materialType.GetString() ?? string.Empty : string.Empty,
            Year = body.TryGetProperty("year", out var year) && year.TryGetInt32(out var yearValue) ? yearValue : 0,
            Data = BsonDocument.Parse(body.GetRawText())
        };

        await persistence.UpsertAsync(document);
        return Results.NoContent();
    }
}
