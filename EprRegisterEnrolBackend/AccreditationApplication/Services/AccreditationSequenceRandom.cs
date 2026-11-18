using System.Security.Cryptography;

namespace EprRegisterEnrolBackend.AccreditationApplication.Services;

public sealed class AccreditationSequenceRandom : IAccreditationSequenceRandom
{
    // Cryptographic, so the sequence can't be predicted from earlier ones. Thread-safe.
    public int NextSequenceDigits() => RandomNumberGenerator.GetInt32(1000);
}
