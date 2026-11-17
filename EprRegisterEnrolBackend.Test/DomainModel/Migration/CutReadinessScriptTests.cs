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
        printed.Last().Should().Be("ALL PASS (8)");
    }

    [Fact]
    public void ReadOnlyChecks_OnAnEmptyDatabase_RunAndFindNothing()
    {
        var printed = Run("cut-readiness-checks.js");

        // A, B and C each print a heading and their result. D's search needs the time of the cut, which isn't set until
        // the cut has happened, so on its own the script says it skipped it.
        var sections = printed.Take(6).Where((_, i) => i % 2 == 1).ToList();
        sections.Should().HaveCount(3, "the script prints A, B and C, each with its result");
        JsonSerializer.Deserialize<JsonElement[]>(sections[0]).Should().BeEmpty("A");
        JsonSerializer.Deserialize<JsonElement[]>(sections[1]).Should().BeEmpty("B");
        JsonSerializer.Deserialize<JsonElement[]>(sections[2]).Should().BeEmpty("C");
        // D always prints the legacy total, which is recorded at the cut and must not change after it (a delete or an
        // untimestamped write shows only there).
        printed[6].Should().StartWith("D. legacy applications in total: 0");
        printed[7].Should().StartWith("D. skipped: set CUT_AT");
        printed.Should().HaveCount(8);
    }
}
