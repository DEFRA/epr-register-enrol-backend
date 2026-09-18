using EprRegisterEnrolBackend.AccreditationApplication.Models;
using EprRegisterEnrolBackend.AccreditationApplication.Services;

namespace EprRegisterEnrolBackend.Test.AccreditationApplication.Services;

public class DuplicateFilenameGuardTests
{
    // ------------------------------ NormaliseFilenameKey ------------------------------

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void NormaliseFilenameKey_ReturnsEmpty_ForNullOrWhitespace(string? filename)
    {
        Assert.Equal(string.Empty, DuplicateFilenameGuard.NormaliseFilenameKey(filename));
    }

    [Fact]
    public void NormaliseFilenameKey_StripsExtension()
    {
        Assert.Equal(
            "EVIDENCE RAFA",
            DuplicateFilenameGuard.NormaliseFilenameKey("Evidence Rafa.pdf")
        );
    }

    // AC02: same base name, different extension — must normalise to the same key.
    [Fact]
    public void NormaliseFilenameKey_IgnoresExtension_SoDifferentFormatsCollide()
    {
        Assert.Equal(
            DuplicateFilenameGuard.NormaliseFilenameKey("Evidence Rafa.pdf"),
            DuplicateFilenameGuard.NormaliseFilenameKey("Evidence Rafa.docx")
        );
    }

    [Fact]
    public void NormaliseFilenameKey_IsCaseInsensitive()
    {
        Assert.Equal(
            DuplicateFilenameGuard.NormaliseFilenameKey("Evidence Rafa.PDF"),
            DuplicateFilenameGuard.NormaliseFilenameKey("evidence rafa.pdf")
        );
    }

    [Fact]
    public void NormaliseFilenameKey_TreatsALeadingDotAsPartOfTheName()
    {
        // No extension to strip — ".gitignore" has no dot after index 0.
        Assert.Equal(".GITIGNORE", DuplicateFilenameGuard.NormaliseFilenameKey(".gitignore"));
    }

    [Fact]
    public void NormaliseFilenameKey_HandlesAFilenameWithNoExtension()
    {
        Assert.Equal("README", DuplicateFilenameGuard.NormaliseFilenameKey("README"));
    }

    // ------------------------------ ExistingFilenames / IsDuplicate ------------------------------

    private static AccreditationApplicationModel BuildApplication(
        IReadOnlyList<string>? samplingPlanFilenames = null,
        IReadOnlyList<(int SiteId, IReadOnlyList<string> Filenames)>? siteFilenames = null
    )
    {
        var app = new AccreditationApplicationModel
        {
            OrganisationId = "org-123",
            Year = 2026,
            MaterialType = MaterialType.Steel,
        };

        foreach (var filename in samplingPlanFilenames ?? [])
            app.SamplingPlan.Files.Add(
                new AccreditationApplicationFile
                {
                    FileId = Guid.NewGuid().ToString(),
                    Filename = filename,
                    ContentType = "application/pdf",
                    UploadedByUserId = string.Empty,
                    S3Key = Guid.NewGuid().ToString(),
                }
            );

        if (siteFilenames is { Count: > 0 })
        {
            app.OverseasSites = new AccreditationApplicationOverseasSites
            {
                Sites = siteFilenames
                    .Select(s => new OverseasSiteModel
                    {
                        SiteId = s.SiteId,
                        SiteName = $"Site {s.SiteId}",
                        BesEvidence = new BesEvidenceModel
                        {
                            BesEvidenceUploads = s
                                .Filenames.Select(f => new BesEvidenceFileModel
                                {
                                    FileId = Guid.NewGuid().ToString(),
                                    Filename = f,
                                    S3Key = Guid.NewGuid().ToString(),
                                })
                                .ToList(),
                        },
                    })
                    .ToList(),
            };
        }

        return app;
    }

    [Fact]
    public void IsDuplicate_ReturnsFalse_WhenNoFilesExist()
    {
        var app = BuildApplication();
        Assert.False(DuplicateFilenameGuard.IsDuplicate("new.pdf", app));
    }

    [Fact]
    public void IsDuplicate_ReturnsFalse_ForANewFilename()
    {
        var app = BuildApplication(samplingPlanFilenames: ["plan.pdf"]);
        Assert.False(DuplicateFilenameGuard.IsDuplicate("evidence.pdf", app));
    }

    [Fact]
    public void IsDuplicate_ReturnsTrue_ForAnExactMatchWithinTheSameSection()
    {
        var app = BuildApplication(samplingPlanFilenames: ["plan.pdf"]);
        Assert.True(DuplicateFilenameGuard.IsDuplicate("plan.pdf", app));
    }

    // AC02
    [Fact]
    public void IsDuplicate_ReturnsTrue_ForTheSameBaseNameWithADifferentExtension()
    {
        var app = BuildApplication(samplingPlanFilenames: ["Evidence Rafa.pdf"]);
        Assert.True(DuplicateFilenameGuard.IsDuplicate("Evidence Rafa.docx", app));
    }

    // "within a single application submission" — a sampling-plan filename collides with one
    // already uploaded as BES evidence on an overseas site, and vice versa.
    [Fact]
    public void IsDuplicate_ReturnsTrue_AcrossSamplingPlanAndBesEvidenceOnADifferentSite()
    {
        var app = BuildApplication(
            samplingPlanFilenames: ["plan.pdf"],
            siteFilenames: [(1, ["Evidence Rafa.pdf"])]
        );

        Assert.True(DuplicateFilenameGuard.IsDuplicate("plan.pdf", app));
        Assert.True(DuplicateFilenameGuard.IsDuplicate("Evidence Rafa.docx", app));
    }

    [Fact]
    public void IsDuplicate_ReturnsTrue_AcrossTwoDifferentOverseasSites()
    {
        var app = BuildApplication(
            siteFilenames: [(1, ["Evidence Rafa.pdf"]), (2, ["Other.docx"])]
        );

        Assert.True(DuplicateFilenameGuard.IsDuplicate("evidence rafa.PDF", app));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void IsDuplicate_ReturnsFalse_ForANullOrBlankCandidateFilename(string? filename)
    {
        var app = BuildApplication(samplingPlanFilenames: ["plan.pdf"]);
        Assert.False(DuplicateFilenameGuard.IsDuplicate(filename, app));
    }

    [Fact]
    public void IsDuplicate_ToleratesAnApplicationWithNoOverseasSites()
    {
        var app = BuildApplication(samplingPlanFilenames: ["plan.pdf"]);
        app.OverseasSites = null;

        Assert.False(DuplicateFilenameGuard.IsDuplicate("other.pdf", app));
        Assert.True(DuplicateFilenameGuard.IsDuplicate("plan.pdf", app));
    }

    [Fact]
    public void IsDuplicate_ToleratesASiteWithNoBesEvidenceYet()
    {
        var app = BuildApplication();
        app.OverseasSites = new AccreditationApplicationOverseasSites
        {
            Sites = [new OverseasSiteModel { SiteId = 1, SiteName = "Site 1" }],
        };

        Assert.False(DuplicateFilenameGuard.IsDuplicate("evidence.pdf", app));
    }
}
