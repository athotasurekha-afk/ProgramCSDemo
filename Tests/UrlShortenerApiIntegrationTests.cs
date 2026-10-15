using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using ProgramCSDemo.Api;
using Xunit;

namespace ProgramCSDemo.Tests;

public sealed class UrlShortenerApiIntegrationTests
{
    [Fact]
    public async Task CreateEndpointRejectsNullUnsafeAndReservedInputs()
    {
        using var host = new ApiHost();
        using var client = host.CreateClient();

        using var nullUrl = await client.PostAsync(
            "/api/links",
            Json("{\"originalUrl\":null}"));
        using var metadata = await client.PostAsync(
            "/api/links",
            Json("{\"originalUrl\":\"http://169.254.169.254/latest/meta-data/\"}"));
        using var local = await client.PostAsync(
            "/api/links",
            Json("{\"originalUrl\":\"http://localhost:8080/admin\"}"));
        using var credentials = await client.PostAsync(
            "/api/links",
            Json("{\"originalUrl\":\"https://user:pw@example.com/\"}"));
        using var reserved = await client.PostAsync(
            "/api/links",
            Json("{\"originalUrl\":\"https://example.com/\",\"customCode\":\"HEALTH\"}"));

        Assert.Equal(HttpStatusCode.BadRequest, nullUrl.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, metadata.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, local.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, credentials.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, reserved.StatusCode);
        Assert.Equal(
            "application/problem+json",
            nullUrl.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task CreateEndpointSupportsIdempotentReplayAndDetectsKeyConflicts()
    {
        using var host = new ApiHost();
        using var client = host.CreateClient();

        using var firstRequest = CreateRequest("https://example.com/target", "retry-key");
        using var first = await client.SendAsync(firstRequest);
        using var replayRequest = CreateRequest("https://example.com/target", "retry-key");
        using var replay = await client.SendAsync(replayRequest);
        using var conflictingRequest = CreateRequest("https://example.org/other", "retry-key");
        using var conflicting = await client.SendAsync(conflictingRequest);

        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        Assert.Equal(HttpStatusCode.Created, replay.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, conflicting.StatusCode);
        Assert.True(replay.Headers.Contains("Idempotency-Replayed"));
        Assert.Equal(first.Headers.Location, replay.Headers.Location);
    }

    [Fact]
    public async Task RedirectsCountConcurrentClicksAndReturnAggregateAnalytics()
    {
        using var host = new ApiHost();
        using var client = host.CreateClient();

        using var created = await client.PostAsync(
            "/api/links",
            Json("{\"originalUrl\":\"https://example.com/target\",\"customCode\":\"parallel\"}"));
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);

        await Task.WhenAll(Enumerable.Range(0, 200).Select(async _ =>
        {
            using var response = await client.GetAsync("/parallel");
            Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
            Assert.Equal("https://example.com/target", response.Headers.Location?.OriginalString);
            Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
        }));

        using var analyticsResponse = await client.GetAsync("/api/links/parallel/analytics");
        var analytics = await analyticsResponse.Content.ReadFromJsonAsync<LinkAnalyticsResponse>();
        Assert.Equal(HttpStatusCode.OK, analyticsResponse.StatusCode);
        Assert.Equal(200, analytics!.ClickCount);
    }

    [Fact]
    public async Task HealthReadinessAndOpenApiAreAvailable()
    {
        using var host = new ApiHost();
        using var client = host.CreateClient();

        using var health = await client.GetAsync("/health");
        using var readiness = await client.GetAsync("/ready");
        using var openApi = await client.GetAsync("/openapi/v1.json");

        Assert.Equal(HttpStatusCode.OK, health.StatusCode);
        Assert.Equal(HttpStatusCode.OK, readiness.StatusCode);
        Assert.Equal(HttpStatusCode.OK, openApi.StatusCode);
        Assert.Contains("/api/links", await openApi.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task ConfiguredRateLimitsCoverLinkCreationAndRedirects()
    {
        using var host = new ApiHost(createLimit: 2, redirectLimit: 3);
        using var client = host.CreateClient();

        using var first = await client.PostAsync(
            "/api/links",
            Json("{\"originalUrl\":\"https://example.com/1\",\"customCode\":\"limit1\"}"));
        using var second = await client.PostAsync(
            "/api/links",
            Json("{\"originalUrl\":\"https://example.com/2\",\"customCode\":\"limit2\"}"));
        using var third = await client.PostAsync(
            "/api/links",
            Json("{\"originalUrl\":\"https://example.com/3\",\"customCode\":\"limit3\"}"));
        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        Assert.Equal(HttpStatusCode.Created, second.StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, third.StatusCode);
        Assert.Equal(
            "application/problem+json",
            third.Content.Headers.ContentType?.MediaType);

        using var redirect1 = await client.GetAsync("/limit1");
        using var redirect2 = await client.GetAsync("/limit1");
        using var redirect3 = await client.GetAsync("/limit1");
        using var redirect4 = await client.GetAsync("/limit1");
        Assert.Equal(HttpStatusCode.Redirect, redirect1.StatusCode);
        Assert.Equal(HttpStatusCode.Redirect, redirect2.StatusCode);
        Assert.Equal(HttpStatusCode.Redirect, redirect3.StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, redirect4.StatusCode);
    }

    private static HttpRequestMessage CreateRequest(string originalUrl, string idempotencyKey)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/links")
        {
            Content = Json(JsonSerializer.Serialize(new { originalUrl }))
        };
        request.Headers.Add("Idempotency-Key", idempotencyKey);
        return request;
    }

    private static StringContent Json(string content) =>
        new(content, Encoding.UTF8, "application/json");

    private sealed class ApiHost : IDisposable
    {
        private readonly string _dataDirectory = Path.Combine(
            Path.GetTempPath(),
            $"url-shortener-api-tests-{Guid.NewGuid():N}");
        private readonly WebApplicationFactory<Program> _factory;

        public ApiHost(int createLimit = 30, int redirectLimit = 300)
        {
            Directory.CreateDirectory(_dataDirectory);
            var dataPath = Path.Combine(_dataDirectory, "links.json");
            _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
            {
                builder.UseEnvironment("Development");
                builder.ConfigureAppConfiguration((_, configuration) =>
                    configuration.AddInMemoryCollection(new Dictionary<string, string?>
                    {
                        ["UrlShortener:DataPath"] = dataPath,
                        ["RateLimiting:Create:PermitLimit"] = createLimit.ToString(CultureInfo.InvariantCulture),
                        ["RateLimiting:Create:WindowSeconds"] = "60",
                        ["RateLimiting:Redirect:PermitLimit"] = redirectLimit.ToString(CultureInfo.InvariantCulture),
                        ["RateLimiting:Redirect:WindowSeconds"] = "60"
                    }));
            });
        }

        public HttpClient CreateClient() =>
            _factory.CreateClient(new WebApplicationFactoryClientOptions
            {
                AllowAutoRedirect = false
            });

        public void Dispose()
        {
            _factory.Dispose();
            if (Directory.Exists(_dataDirectory))
            {
                Directory.Delete(_dataDirectory, recursive: true);
            }
        }
    }
}
