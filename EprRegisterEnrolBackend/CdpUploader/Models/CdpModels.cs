namespace EprRegisterEnrolBackend.CdpUploader.Models;

public class CdpInitiateRequest
{
    public required string Redirect { get; set; }
    public string? Callback { get; set; }
    public required string S3Bucket { get; set; }
    public required string S3Path { get; set; }
    public List<string> MimeTypes { get; set; } = [];
    public long? MaxFileSize { get; set; }
    public Dictionary<string, string>? Metadata { get; set; }
}

public record CdpInitiateResponse
{
    public required string UploadId { get; init; }
    public required string UploadUrl { get; init; }
    public required string StatusUrl { get; init; }
}

/// <summary>Webhook body posted by CDP uploader when a scan completes.</summary>
public class CdpCallbackPayload
{
    public CdpCallbackForm? Form { get; set; }
    public Dictionary<string, string>? Metadata { get; set; }
}

public class CdpCallbackForm
{
    public CdpCallbackFile? File { get; set; }
}

public class CdpCallbackFile
{
    public string? FileId { get; set; }
    public string? Filename { get; set; }

    /// <summary>complete | rejected | pending</summary>
    public string? FileStatus { get; set; }

    public string? S3Bucket { get; set; }
    public string? S3Key { get; set; }
    public string? ContentType { get; set; }
    public string? DetectedContentType { get; set; }
    public string? ErrorMessage { get; set; }
}

/// <summary>Lifecycle states of a file upload through the backend state machine.</summary>
public enum FileProcessingStatus
{
    Preprocessing,
    Validated,
    Rejected,
}

/// <summary>
/// CDP-compatible status shape returned by the backend status endpoint.
/// Frontend controllers read this format unchanged.
/// </summary>
public class CdpStatusResponse
{
    /// <summary>ready | pending</summary>
    public required string UploadStatus { get; set; }

    /// <summary>preprocessing | validated | rejected</summary>
    public string ProcessingStatus { get; set; } = "preprocessing";

    public CdpCallbackForm? Form { get; set; }
    public Dictionary<string, string>? Metadata { get; set; }
}

public class InitiateUploadRequest
{
    public required string RedirectUrl { get; set; }
    public required string S3Path { get; set; }
    public List<string> MimeTypes { get; set; } = [];
    public long? MaxFileSize { get; set; }
    public Dictionary<string, string>? Metadata { get; set; }
}

public class InitiateUploadResponse
{
    public required string FileUploadId { get; set; }
    public required string UploadUrl { get; set; }
    public required string StatusUrl { get; set; }
}
