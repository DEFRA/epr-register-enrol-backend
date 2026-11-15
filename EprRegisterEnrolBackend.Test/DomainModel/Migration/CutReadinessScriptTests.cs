using System.Text.Json;
using EprRegisterEnrolBackend.Test.TestSupport;
using FluentAssertions;

namespace EprRegisterEnrolBackend.Test.DomainModel.Migration;

/// <summary>
/// RA-706: the cut readiness scripts in tools/domain-model are what QA runs with mongosh before
/// <c>DomainModel:LegacyWrites</c> goes off in an environment. They run here under Jint against a real Mongo, so they
/// are known to work.
/// </summary>
public sealed class CutReadinessScriptTests : IDisposable
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string Script(string name) =>
        Path.Combine(AppContext.BaseDirectory, "DomainModel", "Migration", name);

    private readonly TestMongoDbClientFactory _factory;
    private readonly string _databaseName;

    public CutReadinessScriptTests(MongoIntegrationFixture fixture)
    {
        _databaseName = MongoIntegrationFixture.NewDatabaseName("cutreadiness");
        _factory = new TestMongoDbClientFactory(fixture.ConnectionString, _databaseName);
    }

    public void Dispose() => _factory.GetClient().DropDatabase(_databaseName);

    private List<string> Run(string scriptName) => new MongoshScriptRunner(_factory, Ct).Run(Script(scriptName));

    [Fact]
    public void FixtureTest_EveryCheckFlagsItsCaseAndClearsOnceFixed()
    {
        var printed = Run("cut-readiness-test.js");

        printed.Should().NotContain(line => line.StartsWith("FAIL", StringComparison.Ordinal));
        printed.Last().Should().Be("ALL PASS (5)");
    }

    [Fact]
    public void ReadOnlyChecks_OnAnEmptyDatabase_RunAndFindNothing()
    {
        var printed = Run("cut-readiness-checks.js");

        var sections = printed.Where((_, i) => i % 2 == 1).ToList();
        sections.Should().HaveCount(2, "the script prints A and B, each with its result");
        JsonSerializer.Deserialize<JsonElement[]>(sections[0]).Should().BeEmpty("A");
        JsonSerializer.Deserialize<JsonElement[]>(sections[1]).Should().BeEmpty("B");
    }
}
