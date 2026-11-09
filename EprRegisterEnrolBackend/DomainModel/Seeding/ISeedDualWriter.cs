using EprRegisterEnrolBackend.AccreditationApplication.Adapters;
using EprRegisterEnrolBackend.AccreditationApplication.Models;

namespace EprRegisterEnrolBackend.DomainModel.Seeding;

/// <summary>
/// Phase 2, seed dual-write (design doc §6, B6): after Seed has created the legacy application, writes the
/// Organisation, Registration and overseas sites from the same ReEx response and stamps their ids on the
/// application.
/// </summary>
/// <remarks>
/// Fails gracefully: a failed model write rolls back all of its own writes, is logged and counted, and is
/// never thrown, so the seed itself still succeeds. Phase 3's ensure-migrated routine builds the model for an
/// application left without one.
/// </remarks>
public interface ISeedDualWriter
{
    Task WriteAsync(SeedDualWrite seed, CancellationToken cancellationToken = default);
}

/// <param name="ReExOrganisationId">The seed route's <c>organisationId</c>.</param>
/// <param name="ReExRegistrationId">The seed route's <c>registrationId</c>.</param>
/// <param name="Application">The legacy application Seed has just created.</param>
public sealed record SeedDualWrite(
    string ReExOrganisationId,
    string ReExRegistrationId,
    MaterialType Material,
    ReExAccreditationDto ReEx,
    AccreditationApplicationModel Application
);
