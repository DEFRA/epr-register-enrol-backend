using System.Diagnostics.CodeAnalysis;
using EprRegisterEnrolBackend.AccreditationApplication.Adapters;
using EprRegisterEnrolBackend.AccreditationApplication.Endpoints;
using EprRegisterEnrolBackend.AccreditationApplication.Services;
using EprRegisterEnrolBackend.Config;
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
    builder.Services.AddSingleton<IApplicationReferenceService, ApplicationReferenceService>();

    // File Uploads
    builder.Services.AddSingleton<IFileUploadPersistence, FileUploadPersistence>();

    if (builder.Environment.IsDevelopment())
    {
        builder.Services.AddSingleton<IReExApiAdapter, StubReExApiAdapter>();
        builder.Services.AddSingleton<ICaseWorkingApiAdapter, StubCaseWorkingApiAdapter>();
        builder.Services.AddSingleton<IStubApplicationPersistence, StubApplicationPersistence>();

        builder.Services.AddSingleton<OrganisationPersistence>();
        builder.Services.AddSingleton<FakeOrganisationPersistence>();
        builder.Services.AddSingleton<IOrganisationPersistence, FallbackOrganisationPersistence>();
    }
    else
    {
        // Real adapter implementations are required outside of Development (RA-xxx).
        throw new InvalidOperationException(
            "Real IReExApiAdapter and ICaseWorkingApiAdapter implementations must be registered for non-Development environments."
        );
    }
}

[ExcludeFromCodeCoverage]
static WebApplication SetupApplication(WebApplication app)
{
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
