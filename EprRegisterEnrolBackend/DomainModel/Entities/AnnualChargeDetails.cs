using MongoDB.Bson.Serialization.Attributes;

namespace EprRegisterEnrolBackend.DomainModel.Entities;

/// <summary>The details of a registration's annual charge payment. It touches no overseas site.</summary>
[BsonDiscriminator(nameof(ApplicationType.AnnualCharge))]
public sealed class AnnualChargeDetails : ApplicationDetails
{
    public override ApplicationType ApplicationType => ApplicationType.AnnualCharge;

    /// <summary>The year the charge is for.</summary>
    public int ChargeYear { get; set; }
}
