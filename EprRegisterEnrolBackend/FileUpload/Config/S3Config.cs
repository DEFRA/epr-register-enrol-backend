namespace EprRegisterEnrolBackend.FileUpload.Config;

public class S3Config
{
    public string Endpoint { get; set; } = string.Empty;
    public string Region { get; set; } = "eu-west-2";
    public int PresignedUrlExpirySeconds { get; set; } = 300;
}
