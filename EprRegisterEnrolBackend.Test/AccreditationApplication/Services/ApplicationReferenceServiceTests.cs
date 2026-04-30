using EprRegisterEnrolBackend.AccreditationApplication.Services;
using FluentAssertions;
using Xunit;

namespace EprRegisterEnrolBackend.Test.AccreditationApplication.Services;

public class ApplicationReferenceServiceTests
{
    private readonly ApplicationReferenceService _service = new();

    [Fact]
    public void Generate_ReturnsCorrectFormat()
    {
        var reference = _service.Generate(2026);
        reference.Should().MatchRegex(@"^EPR-ACC-2026-[A-Z0-9]{7}$");
    }

    [Fact]
    public void Generate_ReturnsUniqueValues()
    {
        var ref1 = _service.Generate(2026);
        var ref2 = _service.Generate(2026);
        ref1.Should().NotBe(ref2);
    }

    [Fact]
    public void Generate_UsesSuppliedYear()
    {
        var reference = _service.Generate(2027);
        reference.Should().StartWith("EPR-ACC-2027-");
    }
}
