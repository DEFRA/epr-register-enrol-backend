using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using EprRegisterEnrolBackend.AccreditationApplication.Models;
using FluentAssertions;
using MongoDB.Bson;
using NSubstitute;
using NSubstitute.ClearExtensions;

namespace EprRegisterEnrolBackend.Test.AccreditationApplication.Endpoints;

// RA-448: registration and accreditation regenerate are NOT the same mechanism -
// registration draws a brand-new sequence from the atomic counter, accreditation
// ("reapply for accreditation") only increments the existing number's YY segment
// in place and never touches the counter. Both endpoints' own test files already
// cover each mechanism individually; this file asserts the two behaviours side
// by side in one place so a future refactor that accidentally collapses them
// back into a single mechanism fails CI immediately, rather than only showing up
// as a subtle assertion mismatch buried in one of the two separate test files.
public class RegenerateMechanismContrastTests : IClassFixture<AccreditationApplicationTestFactory>
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly AccreditationApplicationTestFactory _factory;
    private readonly HttpClient _client;

    public RegenerateMechanismContrastTests(AccreditationApplicationTestFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
    }

    private void Reset()
    {
        _factory.FakePersistence.Clear();
        _factory.FakeCounters.Clear();
        _factory.MockReExAdapter.ClearSubstitute(ClearOptions.All);
        _factory.MockCaseWorkingAdapter.ClearSubstitute(ClearOptions.All);
        _factory.MockCdpUploaderService.ClearSubstitute(ClearOptions.All);
    }

    [Fact]
    public async Task Regenerate_uses_a_different_mechanism_for_registration_than_for_accreditation()
    {
        Reset();
        _factory.FakeCounters.Seed("R-ER", 1);
        var app = new AccreditationApplicationModel
        {
            Id = ObjectId.GenerateNewId(),
            OrganisationId = "org-contrast",
            Year = 2026,
            MaterialType = MaterialType.Wood,
            ApplicationStatus = ApplicationStatus.Saved,
            RegistrationReference = "R26ER5000270001WO",
            AccreditationReference = "A26ER5000270063WO",
        };
        _factory.FakePersistence.Seed(app);

        var registrationResponse = await _client.PostAsJsonAsync(
            $"/api/v1/accreditation-applications/{app.OrganisationId}/{app.ApplicationId}/registration-number",
            new
            {
                nation = "England",
                orgId = 500027,
                year = 2026,
                regenerate = true,
            }
        );
        var accreditationResponse = await _client.PostAsJsonAsync(
            $"/api/v1/accreditation-applications/{app.OrganisationId}/{app.ApplicationId}/accreditation-number",
            new { regenerate = true }
        );

        var registrationBody =
            await registrationResponse.Content.ReadFromJsonAsync<AccreditationApplicationModel>(
                JsonOptions
            );
        var accreditationBody =
            await accreditationResponse.Content.ReadFromJsonAsync<AccreditationApplicationModel>(
                JsonOptions
            );

        // Registration regenerate: brand-new sequence drawn from the counter, so the
        // sequence digits (index 11-14) change and R-ER's CurrentMax advances.
        registrationBody!.RegistrationReference.Should().Be("R26ER5000270002WO");
        (await _factory.FakeCounters.GetCurrentMaxAsync("R-ER")).Should().Be(2);

        // Accreditation regenerate: only the YY segment (index 1-2) changes, every
        // other segment - including the sequence - is byte-identical, and A-ER's
        // CurrentMax is untouched (no counter draw at all).
        accreditationBody!.AccreditationReference.Should().Be("A27ER5000270063WO");
        (await _factory.FakeCounters.GetCurrentMaxAsync("A-ER")).Should().BeNull();
    }
}
