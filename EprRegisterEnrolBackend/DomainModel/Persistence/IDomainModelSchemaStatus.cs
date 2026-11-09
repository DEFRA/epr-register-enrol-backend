using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace EprRegisterEnrolBackend.DomainModel.Persistence;

/// <summary>
/// Shared between <see cref="EprRegisterEnrolBackend.AccreditationApplication.Startup.MongoIndexInitializerService"/>
/// (writer) and <see cref="DomainModelSchemaHealthCheck"/> (reader): the result of the startup schema check.
/// Registered as a singleton so both resolve the same instance.
/// </summary>
public interface IDomainModelSchemaStatus
{
    /// <summary>Unhealthy until the startup check has recorded a result.</summary>
    HealthCheckResult Result { get; }

    void Record(HealthCheckResult result);
}
