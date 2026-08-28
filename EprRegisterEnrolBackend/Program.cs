using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using EprRegisterEnrolBackend.AccreditationApplication.Adapters;
using EprRegisterEnrolBackend.AccreditationApplication.Endpoints;
using EprRegisterEnrolBackend.AccreditationApplication.Services;
using EprRegisterEnrolBackend.AccreditationApplication.Startup;
using EprRegisterEnrolBackend.Auth;
using EprRegisterEnrolBackend.CdpUploader.Config;
using EprRegisterEnrolBackend.CdpUploader.Services;
using EprRegisterEnrolBackend.Config;
using EprRegisterEnrolBackend.Organisation.Endpoints;
using EprRegisterEnrolBackend.Organisation.Services;
using EprRegisterEnrolBackend.ReEx;
using EprRegisterEnrolBackend.ReEx.Config;
using EprRegisterEnrolBackend.StubPersistence.Endpoints;
using EprRegisterEnrolBackend.StubPersistence.Services;
using EprRegisterEnrolBackend.Utils;
using EprRegisterEnrolBackend.Utils.Health;
using EprRegisterEnrolBackend.Utils.Http;
using EprRegisterEnrolBackend.Utils.Logging;
using EprRegisterEnrolBackend.Utils.Mongo;
using FluentValidation;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.Extensions.Diagnostics.HealthChecks;
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

    // Suppress the "Server: Kestrel" response header. It's a minor
    // fingerprinting aid with no direct exploit path, but there's no
    // reason to advertise the server stack (pentest 2026-08-08, L4).
    builder.WebHost.ConfigureKestrel(options => options.AddServerHeader = false);

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

    // Default HTTP Client. Used (among other things) for the Registration & Accreditation
    // service BE -> ManagementBe submit call (HttpCaseWorkingApiAdapter.SubmitApplicationAsync).
    // Without an explicit Timeout this defaults to .NET's 100s, which can leave the
    // Registration & Accreditation service BE still waiting long after the Registration &
    // Accreditation service FE's own ~20s submit-call budget has already given up, producing a
    // false-failure page for the operator (RA-311). 15s keeps this comfortably under that
    // budget with margin; it is a principled starting point, not measured Case Management
    // service BE latency.
    builder
        .Services.AddHttpClient(
            "DefaultClient",
            client => client.Timeout = TimeSpan.FromSeconds(15)
        )
        .AddHeaderPropagation();

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
    // "ready" is a separate tag/endpoint (see SetupApplication) — required config gaps
    // (RA-441) degrade readiness, not liveness, so a broken deploy is visible without
    // making ECS crash-loop a task that would otherwise serve its working endpoints fine.
    builder
        .Services.AddHealthChecks()
        .AddCheck<RequiredConfigHealthCheck>("required-config", tags: ["ready"])
        // RA-448: keeps /health/ready unhealthy until the counter backfill completes,
        // so the platform doesn't route real traffic to the number-generation
        // endpoints before the 16 pools are seeded (see RegulatoryNumberSequenceBackfillService).
        .AddCheck<RegulatoryNumberBackfillHealthCheck>(
            "regulatory-number-backfill",
            tags: ["ready"]
        );
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

    // RA-448: registration/accreditation number generation
    builder.Services.AddSingleton<
        IRegulatoryNumberSequenceCounterPersistence,
        RegulatoryNumberSequenceCounterPersistence
    >();
    builder.Services.AddSingleton<IRegulatoryNumberGenerator, RegulatoryNumberGenerator>();
    builder.Services.AddSingleton<
        IRegulatoryNumberBackfillStatus,
        RegulatoryNumberBackfillStatus
    >();

    // RA-469: audit trail for the regulator-facing recycling-operations PATCH endpoint (AC15/AC19).
    builder.Services.AddSingleton<
        IRecyclingOperationsAuditPersistence,
        RecyclingOperationsAuditPersistence
    >();
    // Launch blocker (AC4): must run in every environment, not just Development -
    // seeds the counters so the first number this endpoint issues doesn't collide
    // with one already in the real register. Idempotent, safe on every startup.
    builder.Services.AddHostedService<RegulatoryNumberSequenceBackfillService>();

    // Build the Mongo-backed persistences' indexes at startup (off the request
    // path) rather than on whichever request first resolves the singleton.
    builder.Services.AddHostedService<MongoIndexInitializerService>();

    // CDP Uploader
    builder.Services.Configure<CdpUploaderConfig>(builder.Configuration.GetSection("CdpUploader"));
    builder.Services.Configure<AppConfig>(builder.Configuration.GetSection("App"));
    builder.Services.AddSingleton<ICdpUploaderService, CdpUploaderService>();
    builder.Services.AddSingleton<IPendingUploadService, PendingUploadService>();

    // CaseManagement inbound auth: verifies pushes from ManagementBe (RA-311 OBE-2).
    // SharedSecret is deliberately NOT part of the CaseManagementAuth__* section —
    // CDP's secrets naming convention is a flat UPPER_SNAKE_CASE name, not the
    // nested Section__Property form non-secret config uses — so it's sourced from
    // AUTH_SHARED_SECRET__MANAGEMENT_BE instead, extending the same AUTH_SHARED_SECRET__*
    // per-caller family ManagementBe itself uses for its own inbound callers
    // (AUTH_SHARED_SECRET__MANAGEMENT_FE / AUTH_SHARED_SECRET__BACKEND) — this service
    // has exactly one known caller (ManagementBe) today, but naming by caller rather
    // than by feature keeps room to add another without renaming this one.
    //
    // Config-key form, NOT the literal env var name: EnvironmentVariablesConfigurationProvider
    // rewrites "__" to ":" while loading, so the real env var
    // AUTH_SHARED_SECRET__MANAGEMENT_BE is stored under config key
    // "AUTH_SHARED_SECRET:MANAGEMENT_BE" — a GetValue call using the literal
    // double-underscore string never matches it (see ManagementBe's own
    // ClientIdAuthentication BuildClientSecrets for the same gotcha).
    builder.Services.AddMemoryCache();
    builder.Services.Configure<CaseManagementAuthConfig>(config =>
    {
        builder.Configuration.GetSection("CaseManagementAuth").Bind(config);
        config.SharedSecret = builder.Configuration.GetValue<string>(
            "AUTH_SHARED_SECRET:MANAGEMENT_BE"
        );
    });
    // Frontend inbound auth: verifies calls from epr-register-enrol-frontend, the only
    // caller of the ReEx-backed organisation endpoints. Same flat-env-var convention as
    // CaseManagementAuth above — sourced from AUTH_SHARED_SECRET__FRONTEND, not a nested
    // FrontendAuth__* key.
    builder.Services.Configure<FrontendAuthConfig>(config =>
    {
        config.SharedSecret = builder.Configuration.GetValue<string>("AUTH_SHARED_SECRET:FRONTEND");
    });

    builder
        .Services.AddAuthentication()
        .AddScheme<CaseManagementAuthenticationOptions, CaseManagementAuthenticationHandler>(
            CaseManagementAuthenticationHandler.SchemeName,
            _ => { }
        )
        .AddScheme<FrontendAuthenticationOptions, FrontendAuthenticationHandler>(
            FrontendAuthenticationHandler.SchemeName,
            _ => { }
        );
    builder.Services.AddAuthorization();

    // CaseWorking: config-driven stub/real switch (default: stub). SharedSecret
    // is deliberately NOT part of the CaseWorking__* section — CDP's secrets
    // naming convention is a flat UPPER_SNAKE_CASE name (e.g. AUTH_SHARED_SECRET,
    // NOTIFY_API_KEY), not the nested Section__Property form non-secret config
    // uses, so it's sourced from CASE_MANAGEMENT_API_SHARED_SECRET instead.
    builder.Services.Configure<CaseWorkingApiConfig>(config =>
    {
        builder.Configuration.GetSection("CaseWorking").Bind(config);
        config.SharedSecret = builder.Configuration.GetValue<string>(
            "CASE_MANAGEMENT_API_SHARED_SECRET"
        );
    });
    var caseWorkingConfig = new CaseWorkingApiConfig();
    builder.Configuration.GetSection("CaseWorking").Bind(caseWorkingConfig);
    caseWorkingConfig.SharedSecret = builder.Configuration.GetValue<string>(
        "CASE_MANAGEMENT_API_SHARED_SECRET"
    );
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

        // Fixtures for StubReExApiAdapter's dev-mode responses — not tied to
        // any persistence interface, this is the only place it's used.
        builder.Services.AddSingleton<FakeOrganisationPersistence>();
    }
    else
    {
        builder.Services.AddSingleton<IReExApiAdapter, HttpReExApiAdapter>();
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

    if (string.IsNullOrWhiteSpace(cdpConfig.Url))
        startupLogger.LogWarning(
            "CDP_UPLOADER_URL (CdpUploader:Url) is not configured — file uploads will fail at runtime."
        );
    if (string.IsNullOrWhiteSpace(appCfg.BaseUrl))
        startupLogger.LogWarning(
            "APP_BASE_URL (App:BaseUrl) is not configured — CDP callback and status URLs will be incorrect."
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

    var caseWorkingCfg = app
        .Services.GetRequiredService<Microsoft.Extensions.Options.IOptions<CaseWorkingApiConfig>>()
        .Value;
    if (!caseWorkingCfg.UseStub && string.IsNullOrWhiteSpace(caseWorkingCfg.Url))
        startupLogger.LogWarning(
            "CaseWorking__Url is not configured — case working API calls will fail at runtime."
        );
    if (
        !app.Environment.IsDevelopment()
        && !caseWorkingCfg.UseStub
        && string.IsNullOrWhiteSpace(caseWorkingCfg.SharedSecret)
    )
        startupLogger.LogWarning(
            "CASE_MANAGEMENT_API_SHARED_SECRET is not configured — outbound case working API calls will be unsigned."
        );
    if (!app.Environment.IsDevelopment() && caseWorkingCfg.UseStub)
        startupLogger.LogWarning(
            "CaseWorking__UseStub is still true outside Development — case working submissions will be stubbed, not sent."
        );

    var caseManagementAuthCfg = app
        .Services.GetRequiredService<Microsoft.Extensions.Options.IOptions<CaseManagementAuthConfig>>()
        .Value;
    if (
        !app.Environment.IsDevelopment()
        && string.IsNullOrWhiteSpace(caseManagementAuthCfg.SharedSecret)
    )
        startupLogger.LogWarning(
            "AUTH_SHARED_SECRET__MANAGEMENT_BE is not configured — inbound CaseManagement-authenticated requests will be rejected."
        );

    var frontendAuthCfg = app
        .Services.GetRequiredService<Microsoft.Extensions.Options.IOptions<FrontendAuthConfig>>()
        .Value;
    if (!app.Environment.IsDevelopment() && string.IsNullOrWhiteSpace(frontendAuthCfg.SharedSecret))
        startupLogger.LogWarning(
            "AUTH_SHARED_SECRET__FRONTEND is not configured — inbound Frontend-authenticated requests will be rejected."
        );

    app.UseExceptionHandler();
    app.UseHeaderPropagation();
    app.UseRouting();
    // No app.UseAuthentication(): CaseManagement is the only scheme registered, so ASP.NET Core
    // treats it as the implicit default and this middleware would eagerly run
    // CaseManagementAuthenticationHandler against every request — including /health — logging a
    // "Missing x-cdp-client-id header" warning on every liveness probe. Nothing reads
    // HttpContext.User outside the two case-management endpoints below, and their authorization
    // policy already names the scheme explicitly via AddAuthenticationSchemes, so
    // UseAuthorization() authenticates them directly without needing the default-scheme pass.
    app.UseAuthorization();

    // Plain liveness probe — this is the CDP/ECS-facing endpoint (see Dockerfile), so it
    // must reflect only "is the process up", never application config state. A config gap
    // belongs on /health/ready instead: failing liveness on it would crash-loop a task that
    // would otherwise serve its working endpoints fine (RA-441).
    app.MapHealthChecks("/health", new HealthCheckOptions { Predicate = _ => false })
        .WithMetadata(new HttpMethodMetadata(new[] { HttpMethods.Get, HttpMethods.Head }));
    app.MapHealthChecks(
            "/health/ready",
            new HealthCheckOptions
            {
                Predicate = check => check.Tags.Contains("ready"),
                ResponseWriter = WriteReadinessResponse,
            }
        )
        .WithMetadata(new HttpMethodMetadata(new[] { HttpMethods.Get, HttpMethods.Head }));

    // Enable Swagger UI so the API can be explored in the browser
    app.UseSwagger();
    app.UseSwaggerUI();

    // ReEx-backed organisation endpoints (live ReEx lookups, e.g. Defra org link)
    app.UseReExOrganisationEndpoints();
    // Accreditation application endpoints
    app.UseAccreditationApplicationEndpoints();

    if (app.Environment.IsDevelopment())
    {
        app.UseStubApplicationEndpoints();
    }

    return app;
}

// Surfaces which config keys are missing (names only — RequiredConfigHealthCheck never
// puts secret values in a description) instead of the framework default's bare "Unhealthy"
// body, which hid the whole point of the check from anyone curling /health/ready (RA-441).
// Suppresses the description on any entry that threw — HealthCheckService puts the
// exception message there, and this endpoint is unauthenticated, so a future check
// throwing must never turn into exception details being exposed on it.
static Task WriteReadinessResponse(HttpContext context, HealthReport report)
{
    context.Response.ContentType = "application/json";
    var payload = new
    {
        status = report.Status.ToString(),
        checks = report.Entries.Select(entry => new
        {
            name = entry.Key,
            status = entry.Value.Status.ToString(),
            description = entry.Value.Exception is null ? entry.Value.Description : null,
        }),
    };
    return context.Response.WriteAsync(JsonSerializer.Serialize(payload));
}
