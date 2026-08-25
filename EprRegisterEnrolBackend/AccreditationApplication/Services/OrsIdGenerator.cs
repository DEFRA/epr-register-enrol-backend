namespace EprRegisterEnrolBackend.AccreditationApplication.Services;

/// <summary>
/// Computes the next ORS (Overseas Reprocessing Site) id: max(existing numeric ids) + 1,
/// zero-padded to 3 digits, starting at "001".
/// </summary>
///
/// <remarks>
/// RA-482: deliberately scope-agnostic and I/O-free -- the caller decides which OrsId strings
/// are "in scope" (the current application's own sites, or every site across every application
/// under a RegistrationId) and passes the flattened result in. Mirrors the style of the
/// existing local NextSiteId helper in AccreditationApplicationEndpoints.cs, but this one also
/// tolerates null/non-numeric entries (a mix of well-formed and legacy/malformed OrsId values
/// is expected once ReEx-seeded and cross-year data are included) and enforces the format's
/// 3-digit ceiling instead of growing past it.
/// </remarks>
public static class OrsIdGenerator
{
    private const int MaxOrsId = 999;

    public static OrsIdGenerationResult GenerateNext(IEnumerable<string?> existingOrsIds)
    {
        var max = 0;
        foreach (var id in existingOrsIds)
        {
            if (int.TryParse(id, out var parsed) && parsed > max)
                max = parsed;
        }

        var next = max + 1;
        return next > MaxOrsId
            ? new OrsIdGenerationResult(OrsId: null, CapacityExceeded: true)
            : new OrsIdGenerationResult(next.ToString("D3"), CapacityExceeded: false);
    }
}

public record OrsIdGenerationResult(string? OrsId, bool CapacityExceeded);
