using Amazon.S3;
using Amazon.S3.Model;
using EprRegisterEnrolBackend.FileUpload.Config;
using Microsoft.Extensions.Options;

namespace EprRegisterEnrolBackend.FileUpload.Services;

public class S3Service(IOptions<S3Config> config) : IS3Service
{
    private readonly S3Config _config = config.Value;

    public Task<string> GeneratePresignedDownloadUrlAsync(
        string bucket,
        string s3Key,
        string filename,
        CancellationToken cancellationToken = default
    )
    {
        var s3Config = new AmazonS3Config
        {
            RegionEndpoint = Amazon.RegionEndpoint.GetBySystemName(_config.Region),
            ForcePathStyle = true,
        };

        if (!string.IsNullOrWhiteSpace(_config.Endpoint))
            s3Config.ServiceURL = _config.Endpoint;

        using var client = new AmazonS3Client(s3Config);

        var encodedFilename = Uri.EscapeDataString(filename);
        var request = new GetPreSignedUrlRequest
        {
            BucketName = bucket,
            Key = s3Key,
            Verb = HttpVerb.GET,
            Expires = DateTime.UtcNow.AddSeconds(_config.PresignedUrlExpirySeconds),
            ResponseHeaderOverrides = new ResponseHeaderOverrides
            {
                ContentDisposition =
                    $"attachment; filename=\"{filename}\"; filename*=UTF-8''{encodedFilename}",
            },
        };

        var url = client.GetPreSignedURL(request);
        return Task.FromResult(url);
    }
}
