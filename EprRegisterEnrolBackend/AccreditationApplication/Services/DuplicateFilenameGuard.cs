using EprRegisterEnrolBackend.AccreditationApplication.Models;

namespace EprRegisterEnrolBackend.AccreditationApplication.Services;

// RA-571: no two files anywhere on an application submission may share a filename, so a
// regulator can unambiguously reference one by name. "Anywhere" means the sampling plan and
// every overseas site's BES evidence together, not just the section being uploaded to — hence
// this lives beside AccreditationApplicationSections rather than inside either AddFile or
// AddBesEvidenceFile. Uniqueness is judged on the filename's base name only (AC02): "Evidence
// Rafa.pdf" and "Evidence Rafa.docx" collide despite the different extension, and the
// comparison is ordinal-case-insensitive so "A.pdf" and "a.PDF" collide too.
public static class DuplicateFilenameGuard
{
    public const string DuplicateFilenameMessage =
        "A file with this name has already been uploaded. Please rename the file so that each uploaded document has a unique filename.";

    /// <summary>
    /// The base name (no extension), trimmed and upper-invariant, used to compare two filenames
    /// for AC02 purposes. Returns "" for a null/blank filename, which never matches anything —
    /// callers should treat that as "nothing to compare" rather than a collision.
    /// </summary>
    public static string NormaliseFilenameKey(string? filename)
    {
        if (string.IsNullOrWhiteSpace(filename))
            return string.Empty;

        var trimmed = filename.Trim();
        var dotIndex = trimmed.LastIndexOf('.');
        // A dot at index 0 (".gitignore"-style) has no "extension" to strip — the whole
        // string is the name.
        var stem = dotIndex > 0 ? trimmed[..dotIndex] : trimmed;
        return stem.Trim().ToUpperInvariant();
    }

    /// <summary>
    /// Every filename currently attached to <paramref name="application"/> — sampling plan
    /// files plus every overseas site's BES evidence uploads. Deleted files are already absent
    /// from these lists by the time this runs, so a freed-up name is available again.
    /// </summary>
    public static IEnumerable<string?> ExistingFilenames(AccreditationApplicationModel application)
    {
        var samplingPlanFilenames = application.SamplingPlan.Files.Select(f => f.Filename);
        var besEvidenceFilenames = (application.OverseasSites?.Sites ?? [])
            .SelectMany(s => s.BesEvidence?.BesEvidenceUploads ?? [])
            .Select(f => f.Filename);
        return samplingPlanFilenames.Concat(besEvidenceFilenames);
    }

    public static bool IsDuplicate(string? filename, AccreditationApplicationModel application)
    {
        var key = NormaliseFilenameKey(filename);
        if (key.Length == 0)
            return false;

        return ExistingFilenames(application).Any(existing => NormaliseFilenameKey(existing) == key);
    }
}
