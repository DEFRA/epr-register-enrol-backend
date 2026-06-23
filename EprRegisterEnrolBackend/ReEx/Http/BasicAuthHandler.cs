using System.Net.Http.Headers;
using System.Text;
using EprRegisterEnrolBackend.ReEx.Config;
using Microsoft.Extensions.Options;

namespace EprRegisterEnrolBackend.ReEx.Http;

public class BasicAuthHandler : DelegatingHandler
{
    private readonly string _encodedCredentials;

    public BasicAuthHandler(IOptions<ReExCredentials> credentials)
    {
        var creds = credentials.Value;
        _encodedCredentials = Convert.ToBase64String(
            Encoding.UTF8.GetBytes($"{creds.Username}:{creds.Password}")
        );
    }

    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken
    )
    {
        request.Headers.Authorization = new AuthenticationHeaderValue("Basic", _encodedCredentials);
        return base.SendAsync(request, cancellationToken);
    }
}
