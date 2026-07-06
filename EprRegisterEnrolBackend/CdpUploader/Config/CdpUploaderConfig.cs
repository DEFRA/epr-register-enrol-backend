namespace EprRegisterEnrolBackend.CdpUploader.Config;

public class CdpUploaderConfig
{
    public string Url { get; set; } = "http://localhost:7337";
    public string SamplingPlanBucket { get; set; } = "sampling-plans";
    public string BesEvidenceBucket { get; set; } = "bes-evidence";
    public string GenericFilesBucket { get; set; } = "file-uploads";
}

public class AppConfig
{
    public string BaseUrl { get; set; } = "http://localhost:5000";
}
