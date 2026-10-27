using MongoDB.Bson;
using MongoDB.Driver;

namespace EprRegisterEnrolBackend.DomainModel.Diagnostics;

/// <summary>
/// Aggregate counts only: no names, addresses, contact details or free text, so a report is safe to log and to hand
/// to someone without production data access. Each count is of documents (or groups) that have the shape.
/// </summary>
public record LegacyDataSurveyReport(
    long Applications,
    long Int32EnumDocuments,
    long InterimSiteSingularOnlyDocuments,
    long NullOrsIdDocuments,
    long LegacySnInterimNumberDocuments,
    long DuplicateLiveApplicationGroups,
    long ConflictingRegistrationNumberGroups,
    long ApprovedWithoutAccreditationReference,
    long CaseManagementReferencePresent,
    long DuplicateAccreditationReferenceGroups
)
{
    public string ToLogLine() =>
        $"applications={Applications} int32Enums={Int32EnumDocuments} "
        + $"interimSiteSingularOnly={InterimSiteSingularOnlyDocuments} nullOrsIds={NullOrsIdDocuments} "
        + $"legacySnInterimNumbers={LegacySnInterimNumberDocuments} "
        + $"duplicateLiveApplicationGroups={DuplicateLiveApplicationGroups} "
        + $"conflictingRegistrationNumberGroups={ConflictingRegistrationNumberGroups} "
        + $"approvedWithoutAccreditationReference={ApprovedWithoutAccreditationReference} "
        + $"caseManagementReferencePresent={CaseManagementReferencePresent} "
        + $"duplicateAccreditationReferenceGroups={DuplicateAccreditationReferenceGroups}";
}

/// <summary>
/// B1: a read-only survey of <c>accreditationApplications</c> for the legacy shapes the migration has to handle
/// (design doc §7 R1, Q30b, A20). It reads raw documents, because an int32 enum cannot be deserialised into the
/// current model, and never writes. The status filters assume statuses are strings; documents with int32 statuses
/// are counted separately and are exactly the ones those filters cannot see.
/// </summary>
public static class LegacyDataSurvey
{
    private static readonly BsonArray s_numeric = new() { "int", "long" };

    public static async Task<LegacyDataSurveyReport> RunAsync(
        IMongoCollection<BsonDocument> applications,
        CancellationToken cancellationToken
    )
    {
        var total = await applications.CountDocumentsAsync(
            new BsonDocument(),
            cancellationToken: cancellationToken
        );

        Task<long> Count(BsonDocument filter) =>
            applications.CountDocumentsAsync(filter, cancellationToken: cancellationToken);

        var int32Enums = Count(
            Doc(
                "$or",
                new BsonArray
                {
                    Doc("applicationStatus", Doc("$type", s_numeric)),
                    Doc("materialType", Doc("$type", s_numeric)),
                    Doc("nation", Doc("$type", s_numeric)),
                }
            )
        );

        // The singular interimSite with no interimSites list: pre-RA-603 documents.
        var singularOnly = Count(
            Doc(
                "overseasSites.sites",
                Doc(
                    "$elemMatch",
                    Doc(
                        ("interimSite", Doc("$type", "object")),
                        (
                            "$or",
                            new BsonArray
                            {
                                Doc("interimSites", Doc("$exists", false)),
                                Doc("interimSites", Doc("$size", 0)),
                            }
                        )
                    )
                )
            )
        );

        var nullOrsIds = Count(
            Doc("overseasSites.sites", Doc("$elemMatch", Doc("orsId", BsonNull.Value)))
        );

        var legacySn = Count(
            Doc(
                "$or",
                new BsonArray
                {
                    Doc(
                        "overseasSites.sites",
                        Doc(
                            "$elemMatch",
                            Doc(
                                "interimSites",
                                Doc("$elemMatch", Doc("siteNumber", Doc("$regex", "^SN-")))
                            )
                        )
                    ),
                    Doc(
                        "overseasSites.sites",
                        Doc("$elemMatch", Doc("interimSite.siteNumber", Doc("$regex", "^SN-")))
                    ),
                }
            )
        );

        var approvedNoReference = Count(
            Doc(
                ("applicationStatus", "Approved"),
                (
                    "$or",
                    new BsonArray
                    {
                        Doc("accreditationReference", BsonNull.Value),
                        Doc("accreditationReference", ""),
                    }
                )
            )
        );

        var caseManagementReference = Count(
            Doc("caseManagementReference", Doc("$nin", new BsonArray { BsonNull.Value, "" }))
        );

        var duplicateLive = GroupsWithMoreThanOne(
            applications,
            Doc(
                ("applicationStatus", Doc("$nin", new BsonArray { "Withdrawn", "Rejected" })),
                ("registrationId", Doc("$nin", new BsonArray { BsonNull.Value }))
            ),
            Doc(
                ("registrationId", "$registrationId"),
                ("year", "$year"),
                ("materialType", "$materialType")
            ),
            cancellationToken
        );

        var conflictingRegistrationNumbers = ConflictingValues(
            applications,
            "registrationId",
            "registrationReference",
            cancellationToken
        );

        var duplicateAccreditationReferences = GroupsWithMoreThanOne(
            applications,
            Doc("accreditationReference", Doc("$nin", new BsonArray { BsonNull.Value, "" })),
            Doc("ref", "$accreditationReference"),
            cancellationToken
        );

        await Task.WhenAll(
            int32Enums,
            singularOnly,
            nullOrsIds,
            legacySn,
            approvedNoReference,
            caseManagementReference,
            duplicateLive,
            conflictingRegistrationNumbers,
            duplicateAccreditationReferences
        );

        return new LegacyDataSurveyReport(
            total,
            int32Enums.Result,
            singularOnly.Result,
            nullOrsIds.Result,
            legacySn.Result,
            duplicateLive.Result,
            conflictingRegistrationNumbers.Result,
            approvedNoReference.Result,
            caseManagementReference.Result,
            duplicateAccreditationReferences.Result
        );
    }

    private static async Task<long> GroupsWithMoreThanOne(
        IMongoCollection<BsonDocument> applications,
        BsonDocument match,
        BsonDocument groupKey,
        CancellationToken cancellationToken
    )
    {
        var pipeline = new[]
        {
            Doc("$match", match),
            Doc("$group", Doc(("_id", groupKey), ("n", Doc("$sum", 1)))),
            Doc("$match", Doc("n", Doc("$gt", 1))),
            Doc("$count", "groups"),
        };
        return await SingleCount(applications, pipeline, cancellationToken);
    }

    // Groups whose documents carry more than one distinct non-empty value of `field`.
    private static async Task<long> ConflictingValues(
        IMongoCollection<BsonDocument> applications,
        string groupBy,
        string field,
        CancellationToken cancellationToken
    )
    {
        var pipeline = new[]
        {
            Doc(
                "$match",
                Doc(
                    (groupBy, Doc("$nin", new BsonArray { BsonNull.Value })),
                    (field, Doc("$nin", new BsonArray { BsonNull.Value, "" }))
                )
            ),
            Doc("$group", Doc(("_id", "$" + groupBy), ("values", Doc("$addToSet", "$" + field)))),
            Doc("$match", Doc("$expr", Doc("$gt", new BsonArray { Doc("$size", "$values"), 1 }))),
            Doc("$count", "groups"),
        };
        return await SingleCount(applications, pipeline, cancellationToken);
    }

    private static async Task<long> SingleCount(
        IMongoCollection<BsonDocument> applications,
        BsonDocument[] pipeline,
        CancellationToken cancellationToken
    )
    {
        using var cursor = await applications.AggregateAsync<BsonDocument>(
            PipelineDefinition<BsonDocument, BsonDocument>.Create(pipeline),
            cancellationToken: cancellationToken
        );
        var result = await cursor.FirstOrDefaultAsync(cancellationToken);
        return result is null ? 0 : result["groups"].ToInt64();
    }

    private static BsonDocument Doc(string name, BsonValue value) => new(name, value);

    private static BsonDocument Doc(params (string Name, BsonValue Value)[] elements)
    {
        var doc = new BsonDocument();
        foreach (var (name, value) in elements)
        {
            doc.Add(name, value);
        }

        return doc;
    }
}
