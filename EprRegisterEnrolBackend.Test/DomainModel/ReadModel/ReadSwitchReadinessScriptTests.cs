using System.Text.Json;
using EprRegisterEnrolBackend.Test.TestSupport;
using FluentAssertions;

namespace EprRegisterEnrolBackend.Test.DomainModel.ReadModel;

/// <summary>
/// RA-705: the read-switch readiness scripts in tools/domain-model are what QA runs with mongosh before
/// <c>DomainModel:ReadFromModel</c> goes on in an environment. This runs the real scripts under Jint, standing in for
/// mongosh (as LegacyDataSurveyScriptParityTests does for the survey), against a real Mongo, so they are known to work.
/// </summary>
public sealed class ReadSwitchReadinessScriptTests : IDisposable
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string Script(string name) =>
        Path.Combine(AppContext.BaseDirectory, "DomainModel", "ReadModel", name);

    private readonly TestMongoDbClientFactory _factory;
    private readonly string _databaseName;

    public ReadSwitchReadinessScriptTests(MongoIntegrationFixture fixture)
    {
        _databaseName = MongoIntegrationFixture.NewDatabaseName("readiness");
        _factory = new TestMongoDbClientFactory(fixture.ConnectionString, _databaseName);
    }

    public void Dispose() => _factory.GetClient().DropDatabase(_databaseName);

    private List<string> Run(string scriptName) => new MongoshScriptRunner(_factory, Ct).Run(Script(scriptName));

    [Fact]
    public void FixtureTest_EveryCheckFlagsItsCaseAndClearsOnceFixed()
    {
        var printed = Run("read-switch-readiness-test.js");

        printed.Should().NotContain(line => line.StartsWith("FAIL", StringComparison.Ordinal));
        printed.Last().Should().Be("ALL PASS (9)");
    }

    [Fact]
    public void ReadOnlyChecks_OnAnEmptyDatabase_RunAndFindNothingMissing()
    {
        var printed = Run("read-switch-readiness-checks.js");

        var sections = printed.Where((_, i) => i % 2 == 1).ToList();
        sections.Should().HaveCount(5, "the script prints A, B, C, E and D, each with its result");
        JsonSerializer
            .Deserialize<JsonElement[]>(sections[0])
            .Should()
            .BeEmpty("A: nothing missing");
        JsonSerializer.Deserialize<JsonElement[]>(sections[1]).Should().BeEmpty("B");
        JsonSerializer.Deserialize<JsonElement[]>(sections[2]).Should().BeEmpty("C");
        JsonSerializer.Deserialize<JsonElement[]>(sections[3]).Should().BeEmpty("E");
    }
}
