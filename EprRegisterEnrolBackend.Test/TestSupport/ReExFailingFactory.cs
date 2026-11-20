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
        var statusCode = kind == ReExErrorKind.ServerError ? 500 : (int?)null;
        var reEx = Substitute.For<IReExApiAdapter>();
        reEx.GetRegistrationHolderNameAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(ReExResult<string?>.Fail(new ReExError(kind), statusCode));
        reEx.GetRegistrationHolderAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(ReExResult<RegistrationHolder>.Fail(new ReExError(kind), statusCode));
        return factory.WithWebHostBuilder(builder =>
            builder.ConfigureServices(services => services.AddSingleton(reEx))
        );
    }

    /// <summary>
    /// The host with ReEx saying <paramref name="holder"/> holds any registration it is asked about.
    /// </summary>
    public static WebApplicationFactory<Program> WithReExHolder(
        this WebApplicationFactory<Program> factory,
        RegistrationHolder holder
    )
    {
        var reEx = Substitute.For<IReExApiAdapter>();
        reEx.GetRegistrationHolderAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(ReExResult<RegistrationHolder>.Success(holder, 200));
        return factory.WithWebHostBuilder(builder =>
            builder.ConfigureServices(services => services.AddSingleton(reEx))
        );
    }
}
