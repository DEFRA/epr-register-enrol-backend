using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace EprRegisterEnrolBackend.DomainModel.Persistence;

public class DomainModelSchemaStatus : IDomainModelSchemaStatus
{
    private readonly Lock _gate = new();
    private HealthCheckResult _result = HealthCheckResult.Unhealthy(
        "Domain model schema has not been checked yet."
    );

    public HealthCheckResult Result
    {
        get
        {
            lock (_gate)
            {
                return _result;
            }
        }
    }

    public void Record(HealthCheckResult result)
    {
        lock (_gate)
        {
            _result = result;
        }
    }
}
