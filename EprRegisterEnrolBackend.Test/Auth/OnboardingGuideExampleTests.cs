using EprRegisterEnrolBackend.Auth;
using FluentAssertions;

namespace EprRegisterEnrolBackend.Test.Auth;

// The worked example in docs/reex-api-onboarding.md must be what the service really computes.
public class OnboardingGuideExampleTests
{
    private const string Signature = "mJPc+QOEbTNBzB60Pj2NjSOy8bOYmvoCjCwPQS2CSTk=";

    [Fact]
    public void WorkedExample_MatchesTheServicesSignature()
    {
        CaseManagementAuthenticationHandler
            .ComputeSignature(
                "example-secret-do-not-use",
                "example-client",
                null,
                null,
                "2027-01-15T10:30:00Z",
                "bm9uY2UtZXhhbXBsZS0wMDAx"
            )
            .Should()
            .Be(Signature);
    }

    [Fact]
    public void Guide_ContainsTheSameExample()
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null && !File.Exists(Path.Combine(dir, "docs", "reex-api-onboarding.md")))
            dir = Path.GetDirectoryName(dir);

        dir.Should().NotBeNull();
        File.ReadAllText(Path.Combine(dir!, "docs", "reex-api-onboarding.md"))
            .Should()
            .Contain(Signature);
    }
}
