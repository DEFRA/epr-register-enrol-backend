using Microsoft.AspNetCore.Diagnostics;

namespace EprRegisterEnrolBackend.Utils.Logging;

public sealed class ExceptionLoggingHandler(
    ILogger<ExceptionLoggingHandler> logger,
    IProblemDetailsService problemDetailsService
) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(
        HttpContext httpContext,
        Exception exception,
        CancellationToken cancellationToken
    )
    {
        logger.LogError(exception, "Unhandled exception");
        httpContext.Response.StatusCode = StatusCodes.Status500InternalServerError;
        return await problemDetailsService.TryWriteAsync(
            new ProblemDetailsContext { HttpContext = httpContext, Exception = exception }
        );
    }
}
