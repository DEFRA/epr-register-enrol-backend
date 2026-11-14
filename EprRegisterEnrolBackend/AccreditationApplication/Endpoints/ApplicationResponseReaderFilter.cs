using EprRegisterEnrolBackend.AccreditationApplication.Models;
using EprRegisterEnrolBackend.DomainModel.ReadModel;
using Microsoft.AspNetCore.Http.HttpResults;

namespace EprRegisterEnrolBackend.AccreditationApplication.Endpoints;

/// <summary>
/// With DomainModel:ReadFromModel on, the PATCH/POST responses of the accreditation-application routes
/// (including Created from the seed) must come from the domain model. Each write handler returns a result
/// built from the legacy document before the sync has copied the change to the model, so the response is
/// served again via the reader.
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
            var served = await reader.ServeFromSourceAsync(legacy, context.HttpContext.RequestAborted);

            // Write responses are strict like reads: the source not holding the application answers 404
            // (the seed now writes the initial Application version, so a successful write always leaves it in the
            // model).
            return served is null ? TypedResults.NotFound() : TypedResults.Ok(served);
        }

        if (
            result is Created<AccreditationApplicationModel>
            {
                Value: { } created,
                Location: var location
            }
        )
        {
            var served = await reader.ServeFromSourceAsync(created, context.HttpContext.RequestAborted);

            return served is null
                ? TypedResults.NotFound()
                : TypedResults.Created(location, served);
        }

        return result;
    }
}
