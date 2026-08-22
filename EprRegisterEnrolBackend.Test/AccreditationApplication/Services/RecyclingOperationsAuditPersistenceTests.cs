using EprRegisterEnrolBackend.AccreditationApplication.Models;
using EprRegisterEnrolBackend.AccreditationApplication.Services;
using EprRegisterEnrolBackend.Utils.Mongo;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using MongoDB.Driver;
using NSubstitute;

namespace EprRegisterEnrolBackend.Test.AccreditationApplication.Services;

// RA-469 AC15/AC19: covers RecyclingOperationsAuditPersistence.RecordAsync in isolation. The
// class extends MongoService<T> (constructor calls
// IMongoDbClientFactory.GetClient()/GetCollection<T>()), so IMongoDbClientFactory and the
// resulting IMongoCollection<T> are substituted with NSubstitute rather than hitting a real
// database - mirrors RegulatoryNumberSequenceCounterPersistenceTests and
// AccreditationApplicationPersistenceTests' CreateSut pattern. IMongoCollection<T> is a plain
// interface, so its InsertOneAsync member (a real interface method, not an extension) can be
// configured/verified directly.
public class RecyclingOperationsAuditPersistenceTests
{
    private static RecyclingOperationsAuditPersistence CreateSut(
        out IMongoCollection<RecyclingOperationsAuditRecord> collection
    )
    {
        var factory = Substitute.For<IMongoDbClientFactory>();
        var mongoCollection = Substitute.For<IMongoCollection<RecyclingOperationsAuditRecord>>();
        var databaseNamespace = new DatabaseNamespace("test-db");
        var mongoDatabase = Substitute.For<IMongoDatabase>();
        mongoDatabase.DatabaseNamespace.Returns(databaseNamespace);
        mongoCollection.Database.Returns(mongoDatabase);
        mongoCollection.CollectionNamespace.Returns(
            new CollectionNamespace(databaseNamespace, "recyclingOperationsAudit")
        );
        factory.GetClient().Returns(Substitute.For<IMongoClient>());
        factory
            .GetCollection<RecyclingOperationsAuditRecord>("recyclingOperationsAudit")
            .Returns(mongoCollection);

        var loggerFactory = Substitute.For<ILoggerFactory>();
        loggerFactory.CreateLogger(Arg.Any<string>()).Returns(Substitute.For<ILogger>());

        collection = mongoCollection;
        return new RecyclingOperationsAuditPersistence(factory, loggerFactory);
    }

    [Fact]
    public async Task RecordAsync_InsertsARecordCarryingAllTheSuppliedFields()
    {
        var sut = CreateSut(out var collection);
        var before = new List<string> { "R3" };
        var after = new List<string> { "R3", "R4" };
        var record = new RecyclingOperationsAuditRecord
        {
            CdpUserId = "user-1",
            CdpUserName = "Regulator One",
            OrganisationId = "org-1",
            ApplicationId = "app-1",
            SiteId = 5,
            BeforeCodes = before,
            AfterCodes = after,
        };

        await sut.RecordAsync(record, TestContext.Current.CancellationToken);

        await collection
            .Received(1)
            .InsertOneAsync(
                Arg.Is<RecyclingOperationsAuditRecord>(r =>
                    r.CdpUserId == "user-1"
                    && r.CdpUserName == "Regulator One"
                    && r.OrganisationId == "org-1"
                    && r.ApplicationId == "app-1"
                    && r.SiteId == 5
                    && r.BeforeCodes.SequenceEqual(before)
                    && r.AfterCodes.SequenceEqual(after)
                ),
                Arg.Any<InsertOneOptions>(),
                Arg.Any<CancellationToken>()
            );
    }

    [Fact]
    public async Task RecordAsync_StampsTheTimestampAtPersistenceTimeRatherThanTrustingTheCaller()
    {
        var sut = CreateSut(out var collection);
        var record = new RecyclingOperationsAuditRecord
        {
            OrganisationId = "org-1",
            ApplicationId = "app-1",
            SiteId = 5,
            // Simulates a record built well before it's actually persisted (e.g. queued) - the
            // service must not trust this stale caller-supplied value.
            Timestamp = DateTime.UtcNow.AddDays(-1),
        };
        var before = DateTime.UtcNow;

        await sut.RecordAsync(record, TestContext.Current.CancellationToken);

        var after = DateTime.UtcNow;
        await collection
            .Received(1)
            .InsertOneAsync(
                Arg.Is<RecyclingOperationsAuditRecord>(r =>
                    r.Timestamp >= before && r.Timestamp <= after
                ),
                Arg.Any<InsertOneOptions>(),
                Arg.Any<CancellationToken>()
            );
    }

    [Fact]
    public async Task RecordAsync_BeforeAndAfterCodesAreStoredAsIndependentListsNotAliased()
    {
        var sut = CreateSut(out var collection);
        var before = new List<string> { "R3" };
        var after = new List<string> { "R4" };
        var record = new RecyclingOperationsAuditRecord
        {
            OrganisationId = "org-1",
            ApplicationId = "app-1",
            SiteId = 5,
            BeforeCodes = before,
            AfterCodes = after,
        };

        await sut.RecordAsync(record, TestContext.Current.CancellationToken);

        await collection
            .Received(1)
            .InsertOneAsync(
                Arg.Is<RecyclingOperationsAuditRecord>(r =>
                    !ReferenceEquals(r.BeforeCodes, r.AfterCodes)
                ),
                Arg.Any<InsertOneOptions>(),
                Arg.Any<CancellationToken>()
            );

        // Mutating the caller's original "before" list after the call must not retroactively
        // change what was captured as "after" (i.e. RecordAsync never assigned one reference to
        // the other).
        before.Add("R99");
        after.Should().NotContain("R99");
    }
}
