using System.Diagnostics.CodeAnalysis;
using EprRegisterEnrolBackend.AccreditationApplication.Adapters;
using EprRegisterEnrolBackend.ReEx;
using EprRegisterEnrolBackend.ReEx.Config;
using EprRegisterEnrolBackend.AccreditationApplication.Endpoints;
using EprRegisterEnrolBackend.AccreditationApplication.Services;
using EprRegisterEnrolBackend.CdpUploader.Config;
using EprRegisterEnrolBackend.CdpUploader.Services;
using EprRegisterEnrolBackend.Config;
using EprRegisterEnrolBackend.FileUpload.Config;
using EprRegisterEnrolBackend.FileUpload.Endpoints;
using EprRegisterEnrolBackend.FileUpload.Services;
using EprRegisterEnrolBackend.Organisation.Endpoints;
using EprRegisterEnrolBackend.Organisation.Services;
using EprRegisterEnrolBackend.StubPersistence.Endpoints;
using EprRegisterEnrolBackend.StubPersistence.Services;
using EprRegisterEnrolBackend.Utils;
using EprRegisterEnrolBackend.Utils.Http;
using EprRegisterEnrolBackend.Utils.Logging;
using EprRegisterEnrolBackend.Utils.Mongo;
using FluentValidation;
using MongoDB.Driver;
using MongoDB.Driver.Authentication.AWS;
using Serilog;

var app = CreateWebApplication(args);
await app.RunAsync();
return;

[ExcludeFromCodeCoverage]
static WebApplication CreateWebApplication(string[] args)
{
    var builder = WebApplication.CreateBuilder(args);
    ConfigureBuilder(builder);

    var app = builder.Build();
    return SetupApplication(app);
}

[ExcludeFromCodeCoverage]
static void ConfigureBuilder(WebApplicationBuilder builder)
{
    builder.Configuration.AddEnvironmentVariables();

    // Load certificates into Trust Store - Note must happen before Mongo and Http client connections.
    builder.Services.AddCustomTrustStore();

    // Configure logging to use the CDP Platform standards.
    builder.Services.AddHttpContextAccessor();
    builder.Host.UseSerilog(CdpLogging.Configuration);

    // Default HTTP Client
    builder.Services.AddHttpClient("DefaultClient").AddHeaderPropagation();

    // Proxy HTTP Client
    builder.Services.AddTransient<ProxyHttpMessageHandler>();
    builder
        .Services.AddHttpClient("proxy")
        .ConfigurePrimaryHttpMessageHandler<ProxyHttpMessageHandler>();

    // Propagate trace header.
    builder.Services.AddHeaderPropagation(options =>
    {
        var traceHeader = builder.Configuration.GetValue<string>("TraceHeader");
        if (!string.IsNullOrWhiteSpace(traceHeader))
        {
            options.Headers.Add(traceHeader);
        }
    });

    // Set up the MongoDB client. Config and credentials are injected automatically at runtime.
    // Guard against duplicate registration when the factory is instantiated multiple times in tests.
    try
    {
        MongoClientSettings.Extensions.AddAWSAuthentication();
    }
    catch (ArgumentException)
    { /* already registered */
    }
    builder.Services.Configure<MongoConfig>(builder.Configuration.GetSection("Mongo"));
    builder.Services.AddSingleton<IMongoDbClientFactory, MongoDbClientFactory>();

    builder.Services.AddExceptionHandler<ExceptionLoggingHandler>();
    builder.Services.AddProblemDetails();

    // Add healthcheck, this is required for the platform to know your service is alive.
    builder.Services.AddHealthChecks();
    // Swagger/OpenAPI
    builder.Services.AddEndpointsApiExplorer();
    builder.Services.AddSwaggerGen();

    // Allow enum values to be serialised/deserialised by name (e.g. "Wood") rather than index.
    builder.Services.ConfigureHttpJsonOptions(options =>
        options.SerializerOptions.Converters.Add(
            new System.Text.Json.Serialization.JsonStringEnumConverter()
        )
    );
    builder.Services.AddValidatorsFromAssemblyContaining<Program>();
    builder.Services.ConfigureHttpJsonOptions(options =>
        options.SerializerOptions.PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase
    );

    // Accreditation Application
    builder.Services.AddSingleton<
        IAccreditationApplicationPersistence,
        AccreditationApplicationPersistence
    >();
    // File Uploads
    builder.Services.AddSingleton<IFileUploadPersistence, FileUploadPersistence>();
    builder.Services.Configure<S3Config>(builder.Configuration.GetSection("S3"));
    builder.Services.AddSingleton<IS3Service, S3Service>();

    // CDP Uploader
    builder.Services.Configure<CdpUploaderConfig>(builder.Configuration.GetSection("CdpUploader"));
    builder.Services.Configure<AppConfig>(builder.Configuration.GetSection("App"));
    builder.Services.AddSingleton<ICdpUploaderService, CdpUploaderService>();
    builder.Services.AddSingleton<IPendingUploadService, PendingUploadService>();

    // CaseWorking: config-driven stub/real switch (default: stub).
    builder.Services.Configure<CaseWorkingApiConfig>(
        builder.Configuration.GetSection("CaseWorking")
    );
    var caseWorkingConfig =
        builder.Configuration.GetSection("CaseWorking").Get<CaseWorkingApiConfig>()
        ?? new CaseWorkingApiConfig();
    if (caseWorkingConfig.UseStub)
        builder.Services.AddSingleton<ICaseWorkingApiAdapter, StubCaseWorkingApiAdapter>();
    else
        builder.Services.AddSingleton<ICaseWorkingApiAdapter, HttpCaseWorkingApiAdapter>();

    // ReEx API client (org + overseas-sites)
    builder.Services.AddReExClients(builder.Configuration);

    if (builder.Environment.IsDevelopment())
    {
        builder.Services.AddSingleton<IReExApiAdapter, StubReExApiAdapter>();

        builder.Services.AddHostedService<EprRegisterEnrolBackend.CdpUploader.Services.DevScanAutoCompleteService>();
        builder.Services.AddSingleton<IStubApplicationPersistence, StubApplicationPersistence>();

        builder.Services.AddSingleton<OrganisationPersistence>();
        builder.Services.AddSingleton<FakeOrganisationPersistence>();
        builder.Services.AddSingleton<IOrganisationPersistence, FallbackOrganisationPersistence>();
    }
    else
    {
        builder.Services.AddSingleton<IReExApiAdapter, HttpReExApiAdapter>();

        builder.Services.AddSingleton<IOrganisationPersistence, OrganisationPersistence>();
    }
}

[ExcludeFromCodeCoverage]
static WebApplication SetupApplication(WebApplication app)
{
    var startupLogger = app.Services.GetRequiredService<ILogger<Program>>();
    var cdpConfig = app
        .Services.GetRequiredService<Microsoft.Extensions.Options.IOptions<CdpUploaderConfig>>()
        .Value;
    var appCfg = app
        .Services.GetRequiredService<Microsoft.Extensions.Options.IOptions<AppConfig>>()
        .Value;
    var s3Cfg = app
        .Services.GetRequiredService<Microsoft.Extensions.Options.IOptions<S3Config>>()
        .Value;

    if (string.IsNullOrWhiteSpace(cdpConfig.Url))
        startupLogger.LogWarning(
            "CDP_UPLOADER_URL (CdpUploader:Url) is not configured — file uploads will fail at runtime."
        );
    if (string.IsNullOrWhiteSpace(appCfg.BaseUrl))
        startupLogger.LogWarning(
            "APP_BASE_URL (App:BaseUrl) is not configured — CDP callback and status URLs will be incorrect."
        );
    if (string.IsNullOrWhiteSpace(s3Cfg.Region))
        startupLogger.LogWarning(
            "S3_REGION (S3:Region) is not configured — file downloads may fail at runtime."
        );

    var reExConfig = app
        .Services.GetRequiredService<Microsoft.Extensions.Options.IOptions<ReExConfig>>()
        .Value;
    if (string.IsNullOrWhiteSpace(reExConfig.BaseUrl))
        startupLogger.LogWarning(
            "ReExApi__BaseUrl is not configured — ReEx API calls will fail at runtime."
        );

    var reExCreds = app
        .Services.GetRequiredService<Microsoft.Extensions.Options.IOptions<ReExCredentials>>()
        .Value;
    if (string.IsNullOrWhiteSpace(reExCreds.Username))
        startupLogger.LogWarning(
            "REEX_API_BASIC_AUTH_USERNAME is not configured — ReEx API calls will be unauthenticated."
        );
    if (string.IsNullOrWhiteSpace(reExCreds.Password))
        startupLogger.LogWarning(
            "REEX_API_BASIC_AUTH_PASSWORD is not configured — ReEx API calls will be unauthenticated."
        );

    app.UseExceptionHandler();
    app.UseHeaderPropagation();
    app.UseRouting();
    app.MapHealthChecks("/health");

    // Enable Swagger UI so the API can be explored in the browser
    app.UseSwagger();
    app.UseSwaggerUI();

    // Organisation endpoints
    app.UseOrganisationEndpoints();
    // Accreditation application endpoints
    app.UseAccreditationApplicationEndpoints();
    // File upload endpoints
    app.UseFileUploadEndpoints();

    if (app.Environment.IsDevelopment())
    {
        app.UseStubApplicationEndpoints();
    }

    return app;
}
