using EprRegisterEnrolBackend.AccreditationApplication.Adapters;
using EprRegisterEnrolBackend.ReEx;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;

namespace EprRegisterEnrolBackend.Test.TestSupport;

internal static class ReExFailingFactory
{
    /// <summary>
    /// RA-697: the host with ReEx answering the registration holder check with a failure that is neither "found"
    /// nor 404: a server error unless told otherwise.
    /// </summary>
    public static WebApplicationFactory<Program> WithReExFailing(
        this WebApplicationFactory<Program> factory,
        ReExErrorKind kind = ReExErrorKind.ServerError
    )
    {
        var reEx = Substitute.For<IReExApiAdapter>();
        reEx.GetRegistrationHolderNameAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(ReExResult<string?>.Fail(new ReExError(kind), kind == ReExErrorKind.ServerError ? 500 : null));
        return factory.WithWebHostBuilder(builder =>
            builder.ConfigureServices(services => services.AddSingleton(reEx))
        );
    }
}
