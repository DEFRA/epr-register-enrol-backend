using System.Reflection;
using EprRegisterEnrolBackend.DomainModel.Entities;
using EprRegisterEnrolBackend.DomainModel.Persistence;
using EprRegisterEnrolBackend.DomainModel.Versioning;
using EprRegisterEnrolBackend.Test.TestSupport;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;
using MongoDB.Driver;

namespace EprRegisterEnrolBackend.Test.DomainModel.Persistence;

/// <summary>The business uniqueness rules the schema enforces, and the round-trip of the entities, on a real replica set.</summary>
public sealed class DomainModelIndexRuleTests : IDisposable
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static ChangeInfo Change() => new() { Reason = ChangeReason.Migration };

    private readonly TestMongoDbClientFactory _factory;
    private readonly string _databaseName;
    private readonly TestClock _clock = new(new DateTime(2027, 1, 1, 9, 0, 0, DateTimeKind.Utc));
    private readonly IMongoCollection<OrganisationEntity> _organisations;
    private readonly RegistrationPersistence _registrations;
    private readonly OverseasSitePersistence _sites;
    private readonly ApplicationVersionPersistence _applications;
    private readonly AccreditationPersistence _accreditations;

    public DomainModelIndexRuleTests(MongoIntegrationFixture fixture)
    {
        _databaseName = MongoIntegrationFixture.NewDatabaseName("indexrules");
        _factory = new TestMongoDbClientFactory(fixture.ConnectionString, _databaseName);
        _ = new OrganisationPersistence(_factory, NullLoggerFactory.Instance);
        _organisations = _factory.GetCollection<OrganisationEntity>(DomainModelCollections.Organisations);
        _registrations = new RegistrationPersistence(_factory, NullLoggerFactory.Instance, _clock);
        _sites = new OverseasSitePersistence(_factory, NullLoggerFactory.Instance, _clock);
        _applications = new ApplicationVersionPersistence(
            _factory,
            NullLoggerFactory.Instance,
            _clock
        );
        _accreditations = new AccreditationPersistence(
            _factory,
            NullLoggerFactory.Instance,
            _clock
        );
    }

    public void Dispose() => _factory.GetClient().DropDatabase(_databaseName);

    private static async Task ShouldBeRejectedAsDuplicate(Func<Task> write)
    {
        var ex = await Assert.ThrowsAsync<MongoWriteException>(write);
        ex.WriteError.Category.Should().Be(ServerErrorCategory.DuplicateKey);
    }

    [Fact]
    public async Task OrganisationIndexes_DuplicateNumberOrReExId_AreRejectedButMissingNumbersDoNotCollide()
    {
        var first = DomainModelBuilders.Organisation(600001);
        await _organisations.InsertOneAsync(first, cancellationToken: Ct);

        var sameNumber = DomainModelBuilders.Organisation(600001);
        await ShouldBeRejectedAsDuplicate(() =>
            _organisations.InsertOneAsync(sameNumber, cancellationToken: Ct)
        );
        var sameReExId = DomainModelBuilders.Organisation();
        sameReExId.ReExOrganisationId = first.ReExOrganisationId;
        await ShouldBeRejectedAsDuplicate(() =>
            _organisations.InsertOneAsync(sameReExId, cancellationToken: Ct)
        );

        var noNumberA = DomainModelBuilders.Organisation();
        noNumberA.OrganisationNumber = null;
        var noNumberB = DomainModelBuilders.Organisation();
        noNumberB.OrganisationNumber = null;
        await _organisations.InsertOneAsync(noNumberA, cancellationToken: Ct);
        await _organisations.InsertOneAsync(noNumberB, cancellationToken: Ct);
    }

    [Fact]
    public async Task RegistrationIndexes_RegistrationNumber_IsUniqueAmongCurrentVersionsOnly()
    {
        var org = ObjectId.GenerateNewId();
        var first = await _registrations.Versions.InsertAsync(
            DomainModelBuilders.Registration(org, "R27EX0000001P"),
            Change(),
            cancellationToken: Ct
        );
        _clock.Advance(TimeSpan.FromMinutes(1));
        var edited = DomainModelBuilders.Registration(org, "R27EX0000001P");
        edited.EntityId = first.EntityId;
        edited.ReExRegistrationId = first.ReExRegistrationId;
        edited.Status = RegistrationStatus.Cancelled;
        await _registrations.Versions.UpdateAsync(edited, 1, Change(), cancellationToken: Ct);

        await ShouldBeRejectedAsDuplicate(() =>
            _registrations.Versions.InsertAsync(
                DomainModelBuilders.Registration(org, "R27EX0000001P"),
                Change(),
                cancellationToken: Ct
            )
        );

        // A registration with no number yet is allowed more than once.
        var a = DomainModelBuilders.Registration(org);
        a.RegistrationNumber = null;
        var b = DomainModelBuilders.Registration(org);
        b.RegistrationNumber = null;
        await _registrations.Versions.InsertAsync(a, Change(), cancellationToken: Ct);
        await _registrations.Versions.InsertAsync(b, Change(), cancellationToken: Ct);
    }

    [Fact]
    public async Task OverseasSiteIndexes_OrsId_IsUniquePerRegistrationAmongCurrentVersions()
    {
        var registration = ObjectId.GenerateNewId();
        await _sites.Versions.InsertAsync(
            DomainModelBuilders.OverseasSite(registration, "001"),
            Change(),
            cancellationToken: Ct
        );

        await ShouldBeRejectedAsDuplicate(() =>
            _sites.Versions.InsertAsync(
                DomainModelBuilders.OverseasSite(registration, "001"),
                Change(),
                cancellationToken: Ct
            )
        );
        await _sites.Versions.InsertAsync(
            DomainModelBuilders.OverseasSite(ObjectId.GenerateNewId(), "001"),
            Change(),
            cancellationToken: Ct
        );

        // Legacy sites with no orsId may repeat.
        var legacyA = DomainModelBuilders.OverseasSite(registration);
        legacyA.OrsId = null;
        var legacyB = DomainModelBuilders.OverseasSite(registration);
        legacyB.OrsId = null;
        await _sites.Versions.InsertAsync(legacyA, Change(), cancellationToken: Ct);
        await _sites.Versions.InsertAsync(legacyB, Change(), cancellationToken: Ct);
    }

    [Fact]
    public async Task OverseasSiteIndexes_InterimSiteNumber_IsUniqueAcrossARegistrationsCurrentOrss()
    {
        var registration = ObjectId.GenerateNewId();
        await _sites.Versions.InsertAsync(
            DomainModelBuilders.OverseasSite(registration, "001", "001", "002"),
            Change(),
            cancellationToken: Ct
        );

        await ShouldBeRejectedAsDuplicate(() =>
            _sites.Versions.InsertAsync(
                DomainModelBuilders.OverseasSite(registration, "002", "002"),
                Change(),
                cancellationToken: Ct
            )
        );

        // The same number on another registration, and ORSs with no interim sites, do not collide.
        await _sites.Versions.InsertAsync(
            DomainModelBuilders.OverseasSite(ObjectId.GenerateNewId(), "001", "002"),
            Change(),
            cancellationToken: Ct
        );
        await _sites.Versions.InsertAsync(
            DomainModelBuilders.OverseasSite(registration, "003"),
            Change(),
            cancellationToken: Ct
        );
        await _sites.Versions.InsertAsync(
            DomainModelBuilders.OverseasSite(registration, "004"),
            Change(),
            cancellationToken: Ct
        );
    }

    [Fact]
    public async Task OverseasSiteIndexes_ClosedOrsVersion_ReleasesItsInterimSiteNumbers()
    {
        var registration = ObjectId.GenerateNewId();
        var first = await _sites.Versions.InsertAsync(
            DomainModelBuilders.OverseasSite(registration, "001", "001"),
            Change(),
            cancellationToken: Ct
        );
        _clock.Advance(TimeSpan.FromMinutes(1));
        var withoutIt = DomainModelBuilders.OverseasSite(registration, "001");
        withoutIt.EntityId = first.EntityId;
        await _sites.Versions.UpdateAsync(withoutIt, 1, Change(), cancellationToken: Ct);

        // Version 1 still carries siteNumber 001 but is closed, so another ORS may now use it.
        var second = await _sites.Versions.InsertAsync(
            DomainModelBuilders.OverseasSite(registration, "002", "001"),
            Change(),
            cancellationToken: Ct
        );

        second.IsCurrent.Should().BeTrue();
        second.InterimSites.Should().ContainSingle(i => i.SiteNumber == "001");
    }

    [Fact]
    public void DuplicateInterimSiteNumbers_RepeatInsideOneOrs_ReportsIt()
    {
        var site = DomainModelBuilders.OverseasSite(
            ObjectId.GenerateNewId(),
            "001",
            "001",
            "002",
            "001"
        );

        site.DuplicateInterimSiteNumbers().Should().Equal("001");
        DomainModelBuilders
            .OverseasSite(ObjectId.GenerateNewId(), "001", "001", "002")
            .DuplicateInterimSiteNumbers()
            .Should()
            .BeEmpty();
    }

    [Fact]
    public async Task OverseasSiteIndexes_RepeatInsideOneOrsDocument_IsNotCaught()
    {
        // Documents why the write path needs DuplicateInterimSiteNumbers: Mongo itself accepts this.
        var stored = await _sites.Versions.InsertAsync(
            DomainModelBuilders.OverseasSite(ObjectId.GenerateNewId(), "001", "001", "001"),
            Change(),
            cancellationToken: Ct
        );

        stored.DuplicateInterimSiteNumbers().Should().Equal("001");
    }

    [Fact]
    public async Task AccreditationIndexes_AccreditationNumber_IsGloballyUniqueAmongCurrentVersions()
    {
        await _accreditations.Versions.InsertAsync(
            DomainModelBuilders.Accreditation(
                ObjectId.GenerateNewId(),
                accreditationNumber: "A27EX0000001P"
            ),
            Change(),
            cancellationToken: Ct
        );

        await ShouldBeRejectedAsDuplicate(() =>
            _accreditations.Versions.InsertAsync(
                DomainModelBuilders.Accreditation(
                    ObjectId.GenerateNewId(),
                    accreditationNumber: "A27EX0000001P"
                ),
                Change(),
                cancellationToken: Ct
            )
        );
    }

    [Fact]
    public async Task AccreditationIndexes_RegistrationAndYear_AllowAtMostOneCurrentAccreditation()
    {
        var registration = ObjectId.GenerateNewId();
        await _accreditations.Versions.InsertAsync(
            DomainModelBuilders.Accreditation(registration, 2027),
            Change(),
            cancellationToken: Ct
        );

        await ShouldBeRejectedAsDuplicate(() =>
            _accreditations.Versions.InsertAsync(
                DomainModelBuilders.Accreditation(registration, 2027),
                Change(),
                cancellationToken: Ct
            )
        );
        await _accreditations.Versions.InsertAsync(
            DomainModelBuilders.Accreditation(registration, 2028),
            Change(),
            cancellationToken: Ct
        );
    }

    [Fact]
    public async Task AccreditationIndexes_RetriedApproval_CannotCreateASecondAccreditation()
    {
        var application = ObjectId.GenerateNewId();
        await _accreditations.Versions.InsertAsync(
            DomainModelBuilders.Accreditation(ObjectId.GenerateNewId(), applicationId: application),
            Change(),
            cancellationToken: Ct
        );

        await ShouldBeRejectedAsDuplicate(() =>
            _accreditations.Versions.InsertAsync(
                DomainModelBuilders.Accreditation(
                    ObjectId.GenerateNewId(),
                    applicationId: application
                ),
                Change(),
                cancellationToken: Ct
            )
        );
        // Accreditations without an application (none are imported, but the rule must not need one) may repeat.
        await _accreditations.Versions.InsertAsync(
            DomainModelBuilders.Accreditation(ObjectId.GenerateNewId()),
            Change(),
            cancellationToken: Ct
        );
        await _accreditations.Versions.InsertAsync(
            DomainModelBuilders.Accreditation(ObjectId.GenerateNewId()),
            Change(),
            cancellationToken: Ct
        );
    }

    [Fact]
    public async Task ApplicationIndexes_ApplicationReference_IsUniqueAmongCurrentVersionsWhenPresent()
    {
        await _applications.Versions.InsertAsync(
            DomainModelBuilders.Application(applicationReference: "APP-1"),
            Change(),
            cancellationToken: Ct
        );

        await ShouldBeRejectedAsDuplicate(() =>
            _applications.Versions.InsertAsync(
                DomainModelBuilders.Application(applicationReference: "APP-1"),
                Change(),
                cancellationToken: Ct
            )
        );

        var draftA = DomainModelBuilders.Application();
        draftA.ApplicationReference = null;
        var draftB = DomainModelBuilders.Application();
        draftB.ApplicationReference = null;
        await _applications.Versions.InsertAsync(draftA, Change(), cancellationToken: Ct);
        await _applications.Versions.InsertAsync(draftB, Change(), cancellationToken: Ct);
    }

    [Fact]
    public async Task InsertAsync_EveryEntity_StoresEnumsAsStringsAndRoundTrips()
    {
        var registration = await _registrations.Versions.InsertAsync(
            DomainModelBuilders.Registration(ObjectId.GenerateNewId()),
            Change(),
            cancellationToken: Ct
        );
        var site = await _sites.Versions.InsertAsync(
            DomainModelBuilders.OverseasSite(registration.EntityId, "001", "001"),
            Change(),
            cancellationToken: Ct
        );
        var accreditation = await _accreditations.Versions.InsertAsync(
            DomainModelBuilders.Accreditation(registration.EntityId),
            Change(),
            cancellationToken: Ct
        );
        var application = await _applications.Versions.InsertAsync(
            DomainModelBuilders.Application(registration.EntityId),
            Change(),
            cancellationToken: Ct
        );

        async Task<BsonDocument> Raw(string collection, ObjectId id) =>
            await _factory
                .GetCollection<BsonDocument>(collection)
                .Find(new BsonDocument("_id", id))
                .SingleAsync(Ct);

        var rawRegistration = await Raw("registrationVersions", registration.Id);
        rawRegistration["status"].Should().Be((BsonValue)"Approved");
        rawRegistration["wasteProcessingType"].Should().Be((BsonValue)"Exporter");
        rawRegistration["material"].BsonType.Should().Be(BsonType.String);
        var rawSite = await Raw("overseasSiteVersions", site.Id);
        rawSite["status"].Should().Be((BsonValue)"Active");
        rawSite["interimSites"][0]["status"].Should().Be((BsonValue)"Active");
        var rawAccreditation = await Raw("accreditationVersions", accreditation.Id);
        rawAccreditation["status"].Should().Be((BsonValue)"Approved");
        rawAccreditation["tonnageBand"].Should().Be((BsonValue)"up_to_5000");
        var rawApplication = await Raw("applicationVersions", application.Id);
        rawApplication["status"].Should().Be((BsonValue)"Submitted");
        rawApplication["lastChange"]["reason"].Should().Be((BsonValue)"Migration");
        // Enums nested in the sections are strings too, not the legacy int32s.
        rawApplication["prns"]["plannedTonnageBand"].Should().Be((BsonValue)"up_to_10000");
        rawApplication["prns"]["sectionStatus"].Should().Be((BsonValue)"Submitted");
        rawApplication["prns"]["versions"][0]["plannedTonnageBand"].Should().Be((BsonValue)"up_to_500");
        rawApplication["businessPlan"]["sectionStatus"].Should().Be((BsonValue)"Completed");
        rawApplication["samplingPlan"]["sectionStatus"].Should().Be((BsonValue)"Queried");
        rawApplication["samplingPlan"]["files"][0]["scanStatus"].Should().Be((BsonValue)"Clean");
        rawApplication["samplingPlan"]["files"][0]["documentType"].Should().Be((BsonValue)"SamplingPlan");
        rawApplication["besEvidence"]["sectionStatus"].Should().Be((BsonValue)"InProgress");
        rawApplication["overseasSitesSection"]["sectionStatus"].Should().Be((BsonValue)"NotStarted");
        rawAccreditation["samplingPlanFiles"][0]["scanStatus"].Should().Be((BsonValue)"Clean");

        var readBack = await _sites.Versions.GetCurrentAsync(site.EntityId, cancellationToken: Ct);
        readBack!.InterimSites.Should().ContainSingle().Which.SiteNumber.Should().Be("001");
        readBack.BaselOecdCodes.Should().Equal("B3010", "B3020");
        (await _applications.Versions.GetCurrentAsync(application.EntityId, cancellationToken: Ct))!
            .ApplicationOverseasSites.Should()
            .ContainSingle()
            .Which.InterimSites.Should()
            .ContainSingle();
        var readApplication = await _applications.Versions.GetCurrentAsync(
            application.EntityId,
            cancellationToken: Ct
        );
        readApplication!.Prns.PlannedTonnageBand.Should().Be(TonnageBand.UpTo10000);
        (await _accreditations.Versions.GetCurrentAsync(accreditation.EntityId, cancellationToken: Ct))!
            .TonnageBand.Should()
            .Be(TonnageBand.UpTo5000);
    }

    // Guards the rule for fields added later: every enum anywhere in a stored entity, however deeply nested,
    // must be stored as a string, which a [BsonRepresentation] or a [BsonSerializer] on the member gives.
    [Fact]
    public void EntityTypes_EveryEnumMember_IsStoredAsAString()
    {
        var offenders = new List<string>();
        var visited = new HashSet<Type>();
        foreach (
            var type in new[]
            {
                typeof(OrganisationEntity),
                typeof(RegistrationEntity),
                typeof(OverseasSiteEntity),
                typeof(ApplicationEntity),
                typeof(AccreditationEntity),
            }
        )
        {
            CollectUnmappedEnums(type, type.Name, visited, offenders);
        }

        offenders.Should().BeEmpty();
    }

    private static void CollectUnmappedEnums(
        Type type,
        string path,
        HashSet<Type> visited,
        List<string> offenders
    )
    {
        if (!visited.Add(type))
        {
            return;
        }

        foreach (var property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            var propertyType = ElementType(property.PropertyType);
            var underlying = Nullable.GetUnderlyingType(propertyType) ?? propertyType;
            if (underlying.IsEnum)
            {
                if (
                    property.GetCustomAttribute<BsonRepresentationAttribute>()?.Representation
                        != BsonType.String
                    && property.GetCustomAttribute<BsonSerializerAttribute>() is null
                )
                {
                    offenders.Add($"{path}.{property.Name}");
                }
            }
            else if (underlying.Namespace?.StartsWith("EprRegisterEnrolBackend", StringComparison.Ordinal) == true)
            {
                CollectUnmappedEnums(underlying, $"{path}.{property.Name}", visited, offenders);
            }
        }
    }

    private static Type ElementType(Type type) =>
        type.IsGenericType && type.GetGenericTypeDefinition() == typeof(List<>)
            ? type.GetGenericArguments()[0]
            : type;
}
