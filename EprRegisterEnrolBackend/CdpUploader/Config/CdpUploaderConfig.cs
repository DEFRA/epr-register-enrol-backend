namespace EprRegisterEnrolBackend.CdpUploader.Config;

public class CdpUploaderConfig
{
    public string Url { get; set; } = "http://localhost:7337";
    public string SamplingPlanBucket { get; set; } = "epr-register-enrol-sampling-plans";
    public string BesEvidenceBucket { get; set; } = "epr-register-enrol-bes-evidence";
    public string GenericFilesBucket { get; set; } = "epr-register-enrol-file-uploads";
}

public class AppConfig
{
    public string BaseUrl { get; set; } = "http://localhost:5000";
}
