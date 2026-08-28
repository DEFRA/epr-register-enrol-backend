using EprRegisterEnrolBackend.Test.TestSupport;

// One ephemeral mongod for the entire test assembly. xUnit v3 assembly
// fixtures are created once before any test runs and torn down once after the
// last test finishes, without merging consuming classes into a single
// collection — so cross-class parallelism is unaffected, unlike
// ICollectionFixture.
[assembly: AssemblyFixture(typeof(MongoIntegrationFixture))]
