using EprRegisterEnrolBackend.Utils.Logging;
using FluentAssertions;
using FluentValidation.Results;
using Microsoft.Extensions.Logging;

namespace EprRegisterEnrolBackend.Test.Utils.Logging;

public class ValidationFailureLogTests
{
    private static ValidationFailure Failure(string property, string code, object attempted) =>
        new(property, $"'{property}' is not valid: {attempted}", attempted) { ErrorCode = code };

    [Fact]
    public void LogValidationFailure_WritesPropertyNamesAndCodes_NeverValuesOrMessages()
    {
        var logger = new CapturingLogger<ValidationFailureLogTests>();
        var validation = new ValidationResult([
            Failure("ContactPhone", "MaximumLengthValidator", "+44 1234 567890 ext 1234567"),
            Failure("ContactEmail", "RegularExpressionValidator", "person@example.com"),
        ]);

        ValidationFailureLog.LogValidationFailure(logger, "PromoteOverseasSite", "app-1", validation);

        var entry = logger.Entries.Should().ContainSingle().Subject;
        entry.LogLevel.Should().Be(LogLevel.Warning);
        entry
            .Message.Should()
            .Be(
                "PromoteOverseasSite validation failed for applicationId=app-1: "
                    + "ContactPhone (MaximumLengthValidator), ContactEmail (RegularExpressionValidator)"
            );
        entry.Message.Should().NotContain("567890").And.NotContain("person@example.com");
    }

    [Fact]
    public void LogValidationFailure_WarningDisabled_WritesNothing()
    {
        var logger = new WarningDisabledLogger();

        var act = () =>
            ValidationFailureLog.LogValidationFailure(
                logger,
                "AddOverseasSite",
                "app-1",
                new ValidationResult([Failure("SiteName", "NotEmptyValidator", "")])
            );

        act.Should().NotThrow();
        logger.LogCalls.Should().Be(0);
    }

    private sealed class WarningDisabledLogger : ILogger
    {
        public int LogCalls { get; private set; }

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel > LogLevel.Warning;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter
        ) => LogCalls++;
    }
}
