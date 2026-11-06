using EprRegisterEnrolBackend.AccreditationApplication.Models;
using EprRegisterEnrolBackend.DomainModel.Entities;
using MongoDB.Bson;
using MongoDB.Bson.Serialization;

namespace EprRegisterEnrolBackend.DomainModel.Persistence;

/// <summary>
/// The <c>$jsonSchema</c> validator on <c>applicationVersions</c> (RA-746, design doc §4.2). It is a backstop, not the
/// model: it checks the envelope's required fields and their types, that <c>applicationType</c> is a known type, that
/// <c>details</c> carries <c>_t</c> and <c>schemaVersion</c>, and, per type, that <c>details</c> has that type's
/// required fields and matching <c>_t</c>. It deliberately does not copy every field of the C# classes, which would
/// have to change with them. Element names come from the driver's class maps and enum values from the C# enums, so
/// neither can drift from what the code stores.
/// </summary>
public static class ApplicationVersionSchema
{
    public static BsonDocument Validator()
    {
        var envelope = new (string Member, BsonValue Type)[]
        {
            (nameof(ApplicationEntity.EntityId), "objectId"),
            (nameof(ApplicationEntity.Version), "long"),
            (nameof(ApplicationEntity.ValidFrom), "date"),
            (nameof(ApplicationEntity.ValidTo), "date"),
            (nameof(ApplicationEntity.IsCurrent), "bool"),
            (nameof(ApplicationEntity.LastChange), "object"),
            (nameof(ApplicationEntity.ReExOrganisationId), "string"),
            (nameof(ApplicationEntity.ApplicationType), "string"),
            (nameof(ApplicationEntity.Status), "string"),
            (nameof(ApplicationEntity.DateLastEdited), "date"),
            (nameof(ApplicationEntity.Details), "object"),
        };
        var properties = new BsonDocument(
            envelope.Select(f => new BsonElement(
                Element<ApplicationEntity>(f.Member),
                new BsonDocument("bsonType", f.Type)
            ))
        );
        properties[Element<ApplicationEntity>(nameof(ApplicationEntity.ApplicationType))]["enum"] =
            Names<ApplicationType>();
        properties[Element<ApplicationEntity>(nameof(ApplicationEntity.Status))]["enum"] =
            Names<ApplicationStatus>();
        properties[Element<ApplicationEntity>(nameof(ApplicationEntity.Details))] = new BsonDocument
        {
            { "bsonType", "object" },
            {
                "required",
                new BsonArray { Discriminator, SchemaVersion }
            },
            {
                "properties",
                new BsonDocument
                {
                    {
                        Discriminator,
                        new BsonDocument
                        {
                            { "bsonType", "string" },
                            { "enum", Names<ApplicationType>() },
                        }
                    },
                    { SchemaVersion, new BsonDocument("bsonType", "int") },
                }
            },
        };

        return new BsonDocument(
            "$jsonSchema",
            new BsonDocument
            {
                { "bsonType", "object" },
                {
                    "required",
                    new BsonArray(envelope.Select(f => Element<ApplicationEntity>(f.Member)))
                },
                { "properties", properties },
                {
                    "oneOf",
                    new BsonArray
                    {
                        Branch<AccreditationDetails>(
                            ApplicationType.Accreditation,
                            nameof(AccreditationDetails.Kind),
                            nameof(AccreditationDetails.Year)
                        ),
                        Branch<OrsChangeDetails>(
                            ApplicationType.OrsChange,
                            nameof(OrsChangeDetails.TargetAccreditationId),
                            nameof(OrsChangeDetails.OverseasSiteId),
                            nameof(OrsChangeDetails.ChangeKinds),
                            nameof(OrsChangeDetails.Site)
                        ),
                        Branch<TonnageChangeDetails>(
                            ApplicationType.TonnageChange,
                            nameof(TonnageChangeDetails.TargetAccreditationId),
                            nameof(TonnageChangeDetails.FromBand),
                            nameof(TonnageChangeDetails.ToBand)
                        ),
                        Branch<NewRegistrationDetails>(
                            ApplicationType.NewRegistration,
                            nameof(NewRegistrationDetails.Sections)
                        ),
                        Branch<AnnualChargeDetails>(
                            ApplicationType.AnnualCharge,
                            nameof(AnnualChargeDetails.ChargeYear)
                        ),
                    }
                },
            }
        );
    }

    // The element the driver writes each details class's discriminator to (the "_t" convention).
    private static string Discriminator =>
        BsonSerializer.LookupDiscriminatorConvention(typeof(ApplicationDetails)).ElementName;

    private static string SchemaVersion =>
        Element<ApplicationDetails>(nameof(ApplicationDetails.SchemaVersion));

    // One type's branch: this applicationType, and details of that type with its required fields.
    private static BsonDocument Branch<TDetails>(ApplicationType type, params string[] required)
        where TDetails : ApplicationDetails =>
        new(
            "properties",
            new BsonDocument
            {
                {
                    Element<ApplicationEntity>(nameof(ApplicationEntity.ApplicationType)),
                    new BsonDocument("enum", new BsonArray { type.ToString() })
                },
                {
                    Element<ApplicationEntity>(nameof(ApplicationEntity.Details)),
                    new BsonDocument
                    {
                        {
                            "properties",
                            new BsonDocument(
                                Discriminator,
                                new BsonDocument("enum", new BsonArray { type.ToString() })
                            )
                        },
                        { "required", new BsonArray(required.Select(Element<TDetails>)) },
                    }
                },
            }
        );

    private static string Element<T>(string member) =>
        BsonClassMap
            .LookupClassMap(typeof(T))
            .AllMemberMaps.Single(m => m.MemberName == member)
            .ElementName;

    private static BsonArray Names<TEnum>()
        where TEnum : struct, Enum => new(Enum.GetNames<TEnum>());
}
