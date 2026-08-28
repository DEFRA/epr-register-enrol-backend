namespace EprRegisterEnrolBackend.AccreditationApplication.Adapters;

/// <summary>
/// Thrown by <see cref="HttpCaseWorkingApiAdapter"/> when the configured "DefaultClient"
/// <see cref="HttpClient.Timeout"/> (Program.cs, 15s) elapses before ManagementBe responds.
/// Kept as a distinct type — rather than letting the underlying <see cref="TaskCanceledException"/>
/// propagate — so callers such as the Submit endpoint can translate a timeout into a clear,
/// distinguishable response for the Registration & Accreditation service FE instead of a
/// generic unhandled-exception 500 (RA-311).
/// </summary>
public sealed class CaseWorkingApiTimeoutException(string message, Exception innerException)
    : Exception(message, innerException);
