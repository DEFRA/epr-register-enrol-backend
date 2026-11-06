namespace EprRegisterEnrolBackend.AccreditationApplication.Adapters;

/// <summary>
/// Thrown by <see cref="HttpCaseWorkingApiAdapter"/> when no CaseWorking API URL is configured, so a caller can
/// tell "Case Management cannot be reached from this deployment" apart from any other
/// <see cref="InvalidOperationException"/>. It derives from <see cref="InvalidOperationException"/>, which the adapter
/// threw before, so existing handling is unchanged.
/// </summary>
public sealed class CaseWorkingNotConfiguredException()
    : InvalidOperationException("CaseWorking API URL is not configured.");
