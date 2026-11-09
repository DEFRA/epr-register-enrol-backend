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
/// <remarks>
/// A field that is null is not stored at all (the <c>IgnoreIfNull</c> convention), so a field that can legitimately be
/// null must not be required: it would be flagged, and refused once the validator moves to <c>error</c>.
/// </remarks>
public static class ApplicationVersionSchema
{
    private const string BsonTypeKeyword = "bsonType";
    private const string PropertiesKeyword = "properties";
    private const string RequiredKeyword = "required";
    private const string EnumKeyword = "enum";
    private const string ObjectType = "object";
    private const string StringType = "string";

    public static BsonDocument Validator()
    {
        var envelope = new (string Member, BsonValue Type)[]
        {
            (nameof(ApplicationEntity.EntityId), "objectId"),
            (nameof(ApplicationEntity.Version), "long"),
            (nameof(ApplicationEntity.ValidFrom), "date"),
            (nameof(ApplicationEntity.ValidTo), "date"),
            (nameof(ApplicationEntity.IsCurrent), "bool"),
            (nameof(ApplicationEntity.LastChange), ObjectType),
            (nameof(ApplicationEntity.ReExOrganisationId), StringType),
            (nameof(ApplicationEntity.ApplicationType), StringType),
            (nameof(ApplicationEntity.Status), StringType),
            (nameof(ApplicationEntity.DateLastEdited), "date"),
            (nameof(ApplicationEntity.Details), ObjectType),
        };
        var properties = new BsonDocument(
            envelope.Select(f => new BsonElement(
                Element<ApplicationEntity>(f.Member),
                new BsonDocument(BsonTypeKeyword, f.Type)
            ))
        );
        properties[ApplicationTypeElement][EnumKeyword] = Names<ApplicationType>();
        properties[Element<ApplicationEntity>(nameof(ApplicationEntity.Status))][EnumKeyword] =
            Names<ApplicationStatus>();
        properties[DetailsElement] = new BsonDocument
        {
            { BsonTypeKeyword, ObjectType },
            {
                RequiredKeyword,
                new BsonArray { Discriminator, SchemaVersion }
            },
            {
                PropertiesKeyword,
                new BsonDocument
                {
                    {
                        Discriminator,
                        new BsonDocument
                        {
                            { BsonTypeKeyword, StringType },
                            { EnumKeyword, Names<ApplicationType>() },
                        }
                    },
                    { SchemaVersion, new BsonDocument(BsonTypeKeyword, "int") },
                }
            },
        };

        return new BsonDocument(
            "$jsonSchema",
            new BsonDocument
            {
                { BsonTypeKeyword, ObjectType },
                {
                    RequiredKeyword,
                    new BsonArray(envelope.Select(f => Element<ApplicationEntity>(f.Member)))
                },
                { PropertiesKeyword, properties },
                {
                    "oneOf",
                    new BsonArray
                    {
                        Branch<AccreditationDetails>(
                            ApplicationType.Accreditation,
                            [nameof(AccreditationDetails.Kind), nameof(AccreditationDetails.Year)]
                        ),
                        Branch<OrsChangeDetails>(
                            ApplicationType.OrsChange,
                            [
                                nameof(OrsChangeDetails.TargetAccreditationId),
                                nameof(OrsChangeDetails.OverseasSiteId),
                                nameof(OrsChangeDetails.ChangeKinds),
                                nameof(OrsChangeDetails.Site),
                            ]
                        ),
                        // FromBand and ToBand are nullable, so a null one is not stored. A change always names the
                        // band it asks for, so ToBand is required; FromBand may be absent, and is a string when there.
                        Branch<TonnageChangeDetails>(
                            ApplicationType.TonnageChange,
                            [
                                nameof(TonnageChangeDetails.TargetAccreditationId),
                                nameof(TonnageChangeDetails.ToBand),
                            ],
                            [
                                nameof(TonnageChangeDetails.FromBand),
                                nameof(TonnageChangeDetails.ToBand),
                            ]
                        ),
                        Branch<NewRegistrationDetails>(
                            ApplicationType.NewRegistration,
                            [nameof(NewRegistrationDetails.Sections)]
                        ),
                        Branch<AnnualChargeDetails>(
                            ApplicationType.AnnualCharge,
                            [nameof(AnnualChargeDetails.ChargeYear)]
                        ),
                    }
                },
            }
        );
    }

    private static string ApplicationTypeElement =>
        Element<ApplicationEntity>(nameof(ApplicationEntity.ApplicationType));

    private static string DetailsElement =>
        Element<ApplicationEntity>(nameof(ApplicationEntity.Details));

    // The element the driver writes each details class's discriminator to (the "_t" convention).
    private static string Discriminator =>
        BsonSerializer.LookupDiscriminatorConvention(typeof(ApplicationDetails)).ElementName;

    private static string SchemaVersion =>
        Element<ApplicationDetails>(nameof(ApplicationDetails.SchemaVersion));

    // One type's branch: this applicationType, and details of that type with its required fields, and a string type
    // for any field that is only checked when it is present.
    private static BsonDocument Branch<TDetails>(
        ApplicationType type,
        string[] required,
        string[]? stringsWhenPresent = null
    )
        where TDetails : ApplicationDetails
    {
        var detailsProperties = new BsonDocument(
            Discriminator,
            new BsonDocument(EnumKeyword, new BsonArray { type.ToString() })
        );
        foreach (var member in stringsWhenPresent ?? [])
        {
            detailsProperties[Element<TDetails>(member)] = new BsonDocument(
                BsonTypeKeyword,
                StringType
            );
        }

        return new BsonDocument(
            PropertiesKeyword,
            new BsonDocument
            {
                {
                    ApplicationTypeElement,
                    new BsonDocument(EnumKeyword, new BsonArray { type.ToString() })
                },
                {
                    DetailsElement,
                    new BsonDocument
                    {
                        { PropertiesKeyword, detailsProperties },
                        { RequiredKeyword, new BsonArray(required.Select(Element<TDetails>)) },
                    }
                },
            }
        );
    }

    private static string Element<T>(string member) =>
        BsonClassMap
            .LookupClassMap(typeof(T))
            .AllMemberMaps.Single(m => m.MemberName == member)
            .ElementName;

    private static BsonArray Names<TEnum>()
        where TEnum : struct, Enum => new(Enum.GetNames<TEnum>());
}
