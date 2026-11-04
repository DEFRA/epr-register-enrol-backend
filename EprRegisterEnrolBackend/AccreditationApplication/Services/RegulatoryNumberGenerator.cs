using EprRegisterEnrolBackend.AccreditationApplication.Models;

namespace EprRegisterEnrolBackend.AccreditationApplication.Services;

public class RegulatoryNumberGenerator(IRegulatoryNumberSequenceCounterPersistence counters)
    : IRegulatoryNumberGenerator
{
    public async Task<string> GenerateAsync(
        RegulatoryNumberSpec spec,
        CancellationToken ct = default
    )
    {
        // The format is pure (no I/O) and resolved before the atomic increment,
        // so an invalid combination throws without ever touching a counter (AC6) -
        // a defence-in-depth backstop even though the calling endpoint validates
        // first too.
        var format = RegulatoryNumberFormat.For(spec);
        var poolKey = $"R-{format.AgencyType}";

        var sequence = await counters.IncrementAsync(poolKey, ct);

        return format.RegistrationNumber(sequence);
    }
}
