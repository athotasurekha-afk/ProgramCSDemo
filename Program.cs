using System.Text.Json;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using ProgramCSDemo.Api;
using ProgramCSDemo.Services;
using ProgramCSDemo.Storage;

var isLegacyExport = args.Length == 2 && args[0] == "--export-legacy";
var builder = WebApplication.CreateBuilder(isLegacyExport ? Array.Empty<string>() : args);

if (isLegacyExport)
{
    var sourcePath = Environment.GetEnvironmentVariable("URL_SHORTENER_DATA_PATH")
        ?? builder.Configuration["UrlShortener:DataPath"];
    if (string.IsNullOrWhiteSpace(sourcePath))
    {
        sourcePath = Path.Combine(builder.Environment.ContentRootPath, "App_Data", "links.json");
    }

    using var store = new JsonUrlShortenerStore(sourcePath);
    await store.InitializeAsync(CancellationToken.None);
    await store.ExportLegacySnapshotAsync(args[1], CancellationToken.None);
    return;
}

builder.Services.AddOpenApi();
builder.Services.AddProblemDetails();
builder.Services.ConfigureHttpJsonOptions(options =>
{
    options.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
});
builder.Services.AddRateLimiter(options =>
{
    options.AddPolicy("link-creation", context =>
    {
        var configuration = context.RequestServices.GetRequiredService<IConfiguration>();
        return CreateClientLimiter(
            context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            RateLimitConfiguration.ReadPositiveInt(configuration, "RateLimiting:Create:PermitLimit", 30),
            TimeSpan.FromSeconds(RateLimitConfiguration.ReadPositiveInt(
                configuration,
                "RateLimiting:Create:WindowSeconds",
                60)));
    });
    options.AddPolicy("short-link-redirect", context =>
    {
        var configuration = context.RequestServices.GetRequiredService<IConfiguration>();
        return CreateClientLimiter(
            context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            RateLimitConfiguration.ReadPositiveInt(configuration, "RateLimiting:Redirect:PermitLimit", 300),
            TimeSpan.FromSeconds(RateLimitConfiguration.ReadPositiveInt(
                configuration,
                "RateLimiting:Redirect:WindowSeconds",
                60)));
    });
    options.OnRejected = async (context, cancellationToken) =>
    {
        await RateLimitProblemWriter.WriteAsync(context.HttpContext, cancellationToken);
    };
});

builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<IUrlShortenerStore>(services =>
{
    var environment = services.GetRequiredService<IHostEnvironment>();
    var dataPath = Environment.GetEnvironmentVariable("URL_SHORTENER_DATA_PATH")
        ?? services.GetRequiredService<IConfiguration>()["UrlShortener:DataPath"];
    if (string.IsNullOrWhiteSpace(dataPath))
    {
        dataPath = Path.Combine(environment.ContentRootPath, "App_Data", "links.json");
    }

    return new JsonUrlShortenerStore(dataPath);
});
builder.Services.AddSingleton<IUrlShortenerService, UrlShortenerService>();
builder.Services.AddHostedService<StoreInitializationService>();
builder.Services.AddHostedService<AnalyticsCheckpointService>();

var app = builder.Build();

app.UseExceptionHandler();
app.UseStatusCodePages();
app.UseRouting();
app.UseRateLimiter();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.MapGet("/", () => TypedResults.Ok(new ServiceStatusResponse("URL Shortener", "running")))
    .WithName("GetServiceStatus")
    .WithSummary("Get service status");

app.MapGet("/health", () => TypedResults.Ok(new HealthResponse("healthy")))
    .WithName("GetHealth")
    .WithSummary("Check process liveness");

app.MapGet("/ready", async Task<Results<Ok<HealthResponse>, ProblemHttpResult>> (
        IUrlShortenerStore store,
        ILoggerFactory loggerFactory,
        CancellationToken cancellationToken) =>
    {
        try
        {
            await store.CheckReadinessAsync(cancellationToken);
            return TypedResults.Ok(new HealthResponse("ready"));
        }
        catch (IOException exception)
        {
            ReadinessLog.LogFailure(loggerFactory.CreateLogger("Readiness"), exception);
            return TypedResults.Problem(
                statusCode: StatusCodes.Status503ServiceUnavailable,
                title: "Storage unavailable",
                detail: "The link store is not ready to accept writes.");
        }
        catch (UnauthorizedAccessException exception)
        {
            ReadinessLog.LogFailure(loggerFactory.CreateLogger("Readiness"), exception);
            return TypedResults.Problem(
                statusCode: StatusCodes.Status503ServiceUnavailable,
                title: "Storage unavailable",
                detail: "The link store is not ready to accept writes.");
        }
    })
    .WithName("GetReadiness")
    .WithSummary("Check link-store readiness")
    .WithDescription("Verifies that the configured link store is writable.")
    .Produces<HealthResponse>(StatusCodes.Status200OK)
    .ProducesProblem(StatusCodes.Status503ServiceUnavailable);

app.MapUrlShortenerEndpoints();

app.Run();

static RateLimitPartition<string> CreateClientLimiter(
    string clientKey,
    int permitLimit,
    TimeSpan window) =>
    RateLimitPartition.GetFixedWindowLimiter(
        clientKey,
        _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = permitLimit,
            Window = window,
            QueueLimit = 0,
            QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
            AutoReplenishment = true
        });

public partial class Program
{
}
