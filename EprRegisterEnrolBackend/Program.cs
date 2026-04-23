using EprRegisterEnrolBackend.Example.Endpoints;
using EprRegisterEnrolBackend.Example.Services;
using EprRegisterEnrolBackend.Organisation.Endpoints;
using EprRegisterEnrolBackend.Organisation.Services;
using EprRegisterEnrolBackend.Utils;
using EprRegisterEnrolBackend.Utils.Http;
using EprRegisterEnrolBackend.Utils.Mongo;
using EprRegisterEnrolBackend.Auth.Endpoints;
using EprRegisterEnrolBackend.Auth.Services;
using FluentValidation;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.Tokens;
using System.Diagnostics.CodeAnalysis;
using EprRegisterEnrolBackend.Config;
using EprRegisterEnrolBackend.Utils.Logging;
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
    builder.Services
        .AddHttpClient("DefaultClient")
        .AddHeaderPropagation();

    // Proxy HTTP Client
    builder.Services.AddTransient<ProxyHttpMessageHandler>();
    builder.Services
        .AddHttpClient("proxy")
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
    MongoClientSettings.Extensions.AddAWSAuthentication();
    builder.Services.Configure<MongoConfig>(builder.Configuration.GetSection("Mongo"));
    builder.Services.AddSingleton<IMongoDbClientFactory, MongoDbClientFactory>();

    // Add healthcheck, this is required for the platform to know your service is alive.
    builder.Services.AddHealthChecks();
    // Swagger/OpenAPI
    builder.Services.AddEndpointsApiExplorer();
    builder.Services.AddSwaggerGen(c =>
    {
        c.SwaggerDoc("v1", new Microsoft.OpenApi.Models.OpenApiInfo { Title = "EPR Register Enrol API", Version = "v1" });
        // Allow entering a bearer token in Swagger UI (paste "Bearer <token>")
        c.AddSecurityDefinition("Bearer", new Microsoft.OpenApi.Models.OpenApiSecurityScheme
        {
            Description = "JWT Authorization header using the Bearer scheme. Enter 'Bearer {token}'",
            Name = "Authorization",
            In = Microsoft.OpenApi.Models.ParameterLocation.Header,
            Type = Microsoft.OpenApi.Models.SecuritySchemeType.ApiKey,
            Scheme = "Bearer",
            BearerFormat = "JWT"
        });

        c.AddSecurityRequirement(new Microsoft.OpenApi.Models.OpenApiSecurityRequirement
        {
            {
                new Microsoft.OpenApi.Models.OpenApiSecurityScheme
                {
                    Reference = new Microsoft.OpenApi.Models.OpenApiReference { Type = Microsoft.OpenApi.Models.ReferenceType.SecurityScheme, Id = "Bearer" }
                },
                new string[] { }
            }
        });
    });

    // Token service for OAuth2 client credentials
    builder.Services.AddScoped<ITokenService, TokenService>();

    // Authentication via JWT Bearer tokens (issued via client credentials flow)
    var signingKey = builder.Configuration.GetValue<string>("OAuth:SigningKey");
    var authorityCfg = builder.Configuration.GetValue<string>("Authentication:Authority");
    var audienceCfg = builder.Configuration.GetValue<string>("Authentication:Audience");

    builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
        .AddJwtBearer(options =>
        {
            var key = GetTokenSigningKey(signingKey);

            if (string.IsNullOrWhiteSpace(authorityCfg))
            {
                // Use symmetric key validation when Authority is not configured
                options.TokenValidationParameters = new TokenValidationParameters
                {
                    ValidateIssuerSigningKey = true,
                    IssuerSigningKey = key,
                    ValidateIssuer = false,
                    ValidateAudience = string.IsNullOrWhiteSpace(audienceCfg) ? false : true,
                    ValidAudience = audienceCfg,
                    ValidateLifetime = true,
                    ClockSkew = TimeSpan.Zero
                };
            }
            else
            {
                options.Authority = authorityCfg;
                options.RequireHttpsMetadata = false;
                if (!string.IsNullOrWhiteSpace(audienceCfg))
                {
                    options.TokenValidationParameters = new TokenValidationParameters { ValidAudience = audienceCfg };
                }
            }
        });

    // No role-based authorization required; only authenticated clients
    builder.Services.AddAuthorization();

    builder.Services.AddValidatorsFromAssemblyContaining<Program>();

    // Set up the endpoints and their dependencies
    builder.Services.AddSingleton<IExamplePersistence, ExamplePersistence>();
    // Use the in-memory fake persistence for organisation during development
    builder.Services.AddSingleton<IOrganisationPersistence, FakeOrganisationPersistence>();
}

[ExcludeFromCodeCoverage]
static SymmetricSecurityKey GetTokenSigningKey(string? signingKey)
{
    if (string.IsNullOrWhiteSpace(signingKey))
    {
        // Default key for development only (should be overridden in production)
        signingKey = "your-256-bit-secret-key-change-this-in-production-12345";
    }

    // Ensure key is at least 32 bytes (256 bits) for HS256
    var keyBytes = System.Text.Encoding.UTF8.GetBytes(signingKey);
    if (keyBytes.Length < 32)
    {
        throw new InvalidOperationException("OAuth:SigningKey must be at least 32 bytes (256 bits)");
    }

    return new SymmetricSecurityKey(keyBytes.Take(32).ToArray());
}

[ExcludeFromCodeCoverage]
static WebApplication SetupApplication(WebApplication app)
{
    app.UseHeaderPropagation();

    // Enable Swagger UI so the API can be explored in the browser (no auth)
    app.UseSwagger();
    app.UseSwaggerUI(c =>
    {
        c.SwaggerEndpoint("/swagger/v1/swagger.json", "EPR Register Enrol API V1");
    });

    // Authentication & Authorization (applies to API endpoints)
    app.UseAuthentication();
    app.UseAuthorization();

    app.MapHealthChecks("/health");

    // Token endpoint (OAuth2 client credentials)
    app.UseTokenEndpoints();

    // Example module, remove before deploying!
    app.UseExampleEndpoints();
    // Organisation endpoints
    app.UseOrganisationEndpoints();

    app.MapGet("/debug-auth", (HttpContext ctx) => {
        var auth = ctx.Request.Headers["Authorization"].ToString();
        return Results.Ok(new
        {
            receivedHeader = auth,
            hasToken = auth.StartsWith("Bearer ")
        });
    });
    return app;
}