using MongoDB.Bson.Serialization.Attributes;

namespace EprRegisterEnrolBackend.AccreditationApplication.Models;

// RA-448: one document per registration AgencyType sequence pool - 8 in use
// (R-ER, R-EX, R-NR, R-NX, R-SR, R-SX, R-WR, R-WX). Id is the composite key, e.g. "R-ER".
// RA-690: the A-* accreditation pools are retired - accreditation references are derived
// from the registration reference - but any A-* documents already in Mongo are left as-is.
// Deliberately not global and not per-organisation - see the RA-448 design
// doc's counter-scoping evidence.
public class RegulatoryNumberSequenceCounter
{
    // Explicit [BsonId] (no IdGenerator - we always supply the composite key
    // ourselves, never asking Mongo to generate one) maps this to _id, which
    // gets Mongo's mandatory built-in unique index for free - see
    // RegulatoryNumberSequenceCounterPersistence's constructor comment.
    [BsonId]
    public required string Id { get; set; }

    public int CurrentMax { get; set; }

    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
