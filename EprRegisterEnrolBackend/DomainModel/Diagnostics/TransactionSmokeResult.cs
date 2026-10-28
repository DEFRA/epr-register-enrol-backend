namespace EprRegisterEnrolBackend.DomainModel.Diagnostics;

/// <summary>
/// The result of <see cref="TransactionSmokeTest"/>. <see cref="ErrorKind"/> names an exception type (and server
/// code) only, never a message, so it is safe to log.
/// </summary>
public record TransactionSmokeResult(bool CommitWorks, bool AbortWorks, string? ErrorKind)
{
    public bool Supported => CommitWorks && AbortWorks;
}
