using EprRegisterEnrolBackend.AccreditationApplication.Adapters;
using EprRegisterEnrolBackend.ReEx;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;

namespace EprRegisterEnrolBackend.Test.TestSupport;

internal static class ReExFailingFactory
{
    /// <summary>
    /// RA-697: the host with ReEx answering the registration holder check with a server error, the kind of failure
    /// that is neither "found" nor 404.
    /// </summary>
    public static WebApplicationFactory<Program> WithReExFailing(this WebApplicationFactory<Program> factory)
    {
        var reEx = Substitute.For<IReExApiAdapter>();
        reEx.GetRegistrationHolderNameAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(ReExResult<string?>.Fail(new ReExError(ReExErrorKind.ServerError), 500));
        return factory.WithWebHostBuilder(builder =>
            builder.ConfigureServices(services => services.AddSingleton(reEx))
        );
    }
}
