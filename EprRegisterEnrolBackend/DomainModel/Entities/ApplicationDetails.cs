using MongoDB.Bson.Serialization.Attributes;

namespace EprRegisterEnrolBackend.DomainModel.Entities;

/// <summary>
/// The part of an Application whose shape depends on its type (option C1, register-enrol-application-types.md).
/// Each subclass is stored under <c>details</c> with a <c>_t</c> discriminator equal to its
/// <see cref="ApplicationType"/>, so a reader can tell the shape without the envelope. Every subclass must be listed
/// in <see cref="BsonKnownTypesAttribute"/>: the driver can only read back a type it has been told about.
/// </summary>
[BsonKnownTypes(typeof(AccreditationDetails))]
public abstract class ApplicationDetails
{
    /// <summary>The type this shape belongs to. Stored once, as the discriminator, so it is not a stored field here.</summary>
    [BsonIgnore]
    public abstract ApplicationType ApplicationType { get; }

    /// <summary>The version of this type's <c>details</c> shape, so it can change without touching the envelope or other types.</summary>
    public int SchemaVersion { get; set; } = 1;

    /// <summary>
    /// The overseas sites this application holds while it is open, so no other open application can take them
    /// (<c>HeldInFlightElsewhereAsync</c>). Types that touch no overseas site hold none.
    /// </summary>
    public virtual IEnumerable<ApplicationOverseasSiteEntry> HeldSites() => [];
}
