using EprRegisterEnrolBackend.AccreditationApplication.Models;
using MongoDB.Bson;

namespace EprRegisterEnrolBackend.DomainModel.Entities;

/// <summary>One entry per overseas site an application touches (was <c>siteSelections[]</c>).</summary>
public class ApplicationOverseasSiteEntry
{
    /// <summary>The existing per-application int, kept so <c>{siteId}</c> routes still resolve.</summary>
    public int SiteId { get; set; }

    public ObjectId? OverseasSiteId { get; set; }
    public bool Selected { get; set; } = true;
    public bool IsNewSite { get; set; }
    public bool RegisteredNowAccredited { get; set; }

    /// <summary>An overlay of the ORS fields edited in this application, applied to the ORS only on approval.</summary>
    public BsonDocument? ProposedChanges { get; set; }

    public List<BsonDocument> PreviousSites { get; set; } = [];
    public BesEvidenceModel? BesEvidence { get; set; }
    public List<ApplicationInterimSiteEntry> InterimSites { get; set; } = [];
}
