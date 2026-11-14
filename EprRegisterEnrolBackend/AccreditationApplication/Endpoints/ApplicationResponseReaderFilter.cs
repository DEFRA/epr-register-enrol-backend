using EprRegisterEnrolBackend.AccreditationApplication.Models;
using EprRegisterEnrolBackend.DomainModel.ReadModel;
using Microsoft.AspNetCore.Http.HttpResults;

namespace EprRegisterEnrolBackend.AccreditationApplication.Endpoints;

/// <summary>
/// With DomainModel:ReadFromModel on, the PATCH/POST responses of the accreditation-application routes
/// must come from the domain model. Each write handler returns a result built from the legacy document
/// before the sync has copied the change to the model, so the response is served again via the reader.
/// </summary>
public sealed class ApplicationResponseReaderFilter(IApplicationReader reader) : IEndpointFilter
{
    public async ValueTask<object?> InvokeAsync(
        EndpointFilterInvocationContext context,
        EndpointFilterDelegate next
    )
    {
        var result = await next(context);

        if (HttpMethods.IsGet(context.HttpContext.Request.Method))
        {
            return result;
        }

        if (result is Ok<AccreditationApplicationModel> { Value: { } legacy })
        {
            var served = await reader.ServeAsync(legacy, context.HttpContext.RequestAborted);

            // Keep the legacy body when the model doesn't hold the application: the write itself succeeded.
            return served is null ? result : TypedResults.Ok(served);
        }

        return result;
    }
}
