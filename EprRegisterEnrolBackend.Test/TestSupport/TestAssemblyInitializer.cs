using System.Runtime.CompilerServices;
using EprRegisterEnrolBackend.Utils.Mongo;

namespace EprRegisterEnrolBackend.Test.TestSupport;

/// <summary>
/// Registers the same Mongo BSON conventions Program.cs installs at production
/// startup before any test in this assembly touches a model serializer.
///
/// Without this, whether the camelCase element-name convention is active
/// depends on some other test having already constructed
/// <see cref="MongoDbClientFactory"/> — and xUnit test ordering across
/// collections is non-deterministic in CI. Several test classes already work
/// around this individually in their static constructors; the module
/// initializer makes it unconditional.
/// </summary>
internal static class TestAssemblyInitializer
{
    [ModuleInitializer]
    internal static void Init()
    {
        MongoDbClientFactory.EnsureConventionRegistered();
    }
}
