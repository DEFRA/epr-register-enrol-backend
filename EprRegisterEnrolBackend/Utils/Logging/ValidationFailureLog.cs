using FluentValidation.Results;

namespace EprRegisterEnrolBackend.Utils.Logging;

/// <summary>
/// RA-620: logs which fields a request failed validation on, so a 400 returned to the frontend
/// can be diagnosed from CDP. Only each failure's property name and error code are written -
/// never <see cref="ValidationFailure.AttemptedValue"/> (it carries contact names, emails and
/// phone numbers) and never the error message (some default FluentValidation messages echo the
/// value back). They go into the rendered message rather than a structured property because
/// CDP's OpenSearch field allow-list always indexes <c>message</c>, so this is the part that
/// is guaranteed to be searchable.
/// </summary>
public static class ValidationFailureLog
{
    public static void LogValidationFailure(
        ILogger logger,
        string operation,
        string applicationId,
        ValidationResult validation
    )
    {
        if (!logger.IsEnabled(LogLevel.Warning))
            return;

        logger.LogWarning(
            "{Operation} validation failed for applicationId={ApplicationId}: {FailedFields}",
            operation,
            applicationId,
            DescribeFailures(validation.Errors)
        );
    }

    /// <summary>
    /// For a 400 decided by a check outside FluentValidation. Same message shape as the
    /// <see cref="ValidationResult"/> overload so one CDP search finds both; the caller owns
    /// keeping <paramref name="reason"/> free of personal data.
    /// </summary>
    public static void LogValidationFailure(
        ILogger logger,
        string operation,
        string applicationId,
        string reason
    )
    {
        if (!logger.IsEnabled(LogLevel.Warning))
            return;

        logger.LogWarning(
            "{Operation} validation failed for applicationId={ApplicationId}: {FailedFields}",
            operation,
            applicationId,
            reason
        );
    }

    public static string DescribeFailures(IEnumerable<ValidationFailure> failures) =>
        string.Join(", ", failures.Select(f => $"{f.PropertyName} ({f.ErrorCode})"));
}
