using EprRegisterEnrolBackend.AccreditationApplication.Models;
using MongoDB.Bson;

namespace EprRegisterEnrolBackend.AccreditationApplication.Services;

/// <summary>
/// RA-783: a number <see cref="AccreditationNumberIssuer"/> chose, no other live application
/// holding it, and what the issue log needs to explain it.
/// </summary>
/// <param name="SourceKind">A renewal's source is the number it renews; a new number's is the
/// registration it's built on.</param>
/// <param name="SourceApplicationId">The earlier application renewed, when the source is one
/// held here.</param>
/// <param name="WithdrawnHolderIds">Withdrawn applications already holding the number (only
/// ever a continuous renewal's), so support can see why two records share it.</param>
/// <param name="GlassProcessUnknown">True when glass was issued as other (GO) only because no
/// process was held or carried by an earlier number.</param>
public sealed record IssuedAccreditationNumber(
    string AccreditationNumber,
    AccreditationNumberCase Case,
    AccreditationReferenceSourceKind SourceKind,
    string SourceReference,
    string? SourceApplicationId,
    IReadOnlyList<ObjectId> WithdrawnHolderIds,
    bool GlassProcessUnknown
);
