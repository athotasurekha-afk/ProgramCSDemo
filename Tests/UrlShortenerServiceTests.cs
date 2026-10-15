using Microsoft.Extensions.Logging.Abstractions;
using ProgramCSDemo.Api;
using ProgramCSDemo.Domain;
using ProgramCSDemo.Services;
using ProgramCSDemo.Storage;
using Xunit;

namespace ProgramCSDemo.Tests;

public sealed class UrlShortenerServiceTests
{
    private static readonly DateTimeOffset Now =
        new(2026, 10, 15, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task CreateAsync_AcceptsHttpUrlAndCustomCode()
    {
        var service = CreateService(new InMemoryUrlStore());

        var result = await service.CreateAsync(
            new CreateShortLinkRequest
            {
                OriginalUrl = " https://example.com/path ",
                CustomCode = "team-link"
            },
            null,
            CancellationToken.None);

        Assert.Equal(CreateLinkError.None, result.Error);
        Assert.NotNull(result.Link);
        Assert.Equal("team-link", result.Link.Code);
        Assert.Equal("https://example.com/path", result.Link.OriginalUrl);
        Assert.Equal(Now, result.Link.CreatedAt);
    }

    [Theory]
    [InlineData("javascript:alert(1)")]
    [InlineData("data:text/plain,hello")]
    [InlineData("/relative/path")]
    [InlineData("not a URL")]
    public async Task CreateAsync_RejectsNonHttpOrInvalidUrls(string url)
    {
        var service = CreateService(new InMemoryUrlStore());

        var result = await service.CreateAsync(
            new CreateShortLinkRequest { OriginalUrl = url },
            null,
            CancellationToken.None);

        Assert.Equal(CreateLinkError.InvalidUrl, result.Error);
        Assert.Null(result.Link);
    }

    [Fact]
    public async Task CreateAsync_RejectsInvalidAndDuplicateCustomCodes()
    {
        var store = new InMemoryUrlStore();
        var service = CreateService(store);
        var request = new CreateShortLinkRequest
        {
            OriginalUrl = "https://example.com",
            CustomCode = "bad!"
        };

        var invalid = await service.CreateAsync(request, null, CancellationToken.None);
        var first = await service.CreateAsync(
            request with { CustomCode = "valid-code" },
            null,
            CancellationToken.None);
        var duplicate = await service.CreateAsync(
            request with { CustomCode = "valid-code" },
            null,
            CancellationToken.None);

        Assert.Equal(CreateLinkError.InvalidCode, invalid.Error);
        Assert.Equal(CreateLinkError.None, first.Error);
        Assert.Equal(CreateLinkError.CodeConflict, duplicate.Error);
    }

    [Fact]
    public async Task ResolveAsync_RecordsClicksAndAnalytics()
    {
        var store = new InMemoryUrlStore();
        var service = CreateService(store);
        var created = await service.CreateAsync(
            new CreateShortLinkRequest
            {
                OriginalUrl = "https://example.com",
                CustomCode = "click-me"
            },
            null,
            CancellationToken.None);

        var firstClick = await service.ResolveAsync("click-me", CancellationToken.None);
        var secondClick = await service.ResolveAsync("click-me", CancellationToken.None);
        var analytics = await service.GetAnalyticsAsync("click-me", CancellationToken.None);

        Assert.Equal(LinkResolutionStatus.Found, firstClick.Status);
        Assert.Equal(LinkResolutionStatus.Found, secondClick.Status);
        Assert.Equal(2, analytics!.ClickCount);
        Assert.Equal(Now, analytics.LastClickedAt);
        Assert.Equal(created.Link!.Code, analytics.Code);
    }

    [Fact]
    public async Task ResolveAsync_ReturnsExpiredWithoutRecordingClick()
    {
        var store = new InMemoryUrlStore();
        var clock = new ManualTimeProvider(Now);
        var service = new UrlShortenerService(store, clock, NullLogger<UrlShortenerService>.Instance);
        await service.CreateAsync(
            new CreateShortLinkRequest
            {
                OriginalUrl = "https://example.com",
                CustomCode = "expires",
                ExpiresAt = Now.AddMinutes(1)
            },
            null,
            CancellationToken.None);

        clock.SetUtcNow(Now.AddMinutes(1));
        var result = await service.ResolveAsync("expires", CancellationToken.None);
        var analytics = await service.GetAnalyticsAsync("expires", CancellationToken.None);

        Assert.Equal(LinkResolutionStatus.Expired, result.Status);
        Assert.Equal(0, analytics!.ClickCount);
        Assert.Null(analytics.LastClickedAt);
    }

    [Fact]
    public async Task CreateAsync_RejectsExpirationAtOrBeforeCurrentTime()
    {
        var service = CreateService(new InMemoryUrlStore());

        var result = await service.CreateAsync(
            new CreateShortLinkRequest
            {
                OriginalUrl = "https://example.com",
                ExpiresAt = Now
            },
            null,
            CancellationToken.None);

        Assert.Equal(CreateLinkError.ExpirationInPast, result.Error);
    }

    [Fact]
    public async Task ResolveAsync_ReturnsNotFoundForUnknownCode()
    {
        var service = CreateService(new InMemoryUrlStore());

        var result = await service.ResolveAsync("missing", CancellationToken.None);

        Assert.Equal(LinkResolutionStatus.NotFound, result.Status);
        Assert.Null(result.Link);
    }

    [Fact]
    public async Task GeneratedCodes_AreUniqueAcrossManyLinks()
    {
        var service = CreateService(new InMemoryUrlStore());
        var codes = new HashSet<string>(StringComparer.Ordinal);

        for (var index = 0; index < 100; index++)
        {
            var result = await service.CreateAsync(
                new CreateShortLinkRequest { OriginalUrl = $"https://example.com/{index}" },
                null,
                CancellationToken.None);

            Assert.Equal(CreateLinkError.None, result.Error);
            Assert.True(codes.Add(result.Link!.Code));
            Assert.Equal(8, result.Link.Code.Length);
        }
    }

    [Fact]
    public async Task CreateAsync_RejectsNullOriginalUrl()
    {
        var service = CreateService(new InMemoryUrlStore());

        var result = await service.CreateAsync(
            new CreateShortLinkRequest { OriginalUrl = null },
            null,
            CancellationToken.None);

        Assert.Equal(CreateLinkError.InvalidUrl, result.Error);
    }

    [Theory]
    [InlineData("http://169.254.169.254/latest/meta-data/")]
    [InlineData("http://localhost:8080/admin")]
    [InlineData("http://127.0.0.1/admin")]
    [InlineData("http://192.168.1.1/")]
    [InlineData("http://[::1]/")]
    [InlineData("http://[fd00::1]/")]
    [InlineData("http://[::ffff:127.0.0.1]/")]
    [InlineData("http://2130706433/")]
    [InlineData("http://127.1/")]
    [InlineData("https://user:pw@example.com/")]
    [InlineData("http://my-service.local/")]
    public async Task CreateAsync_RejectsUnsafeDestinations(string url)
    {
        var service = CreateService(new InMemoryUrlStore());

        var result = await service.CreateAsync(
            new CreateShortLinkRequest { OriginalUrl = url },
            null,
            CancellationToken.None);

        Assert.Equal(CreateLinkError.UnsafeUrl, result.Error);
    }

    [Fact]
    public async Task CreateAsync_AcceptsPublicIpv6Destination()
    {
        var service = CreateService(new InMemoryUrlStore());

        var result = await service.CreateAsync(
            new CreateShortLinkRequest
            {
                OriginalUrl = "https://[2606:4700:4700::1111]/"
            },
            null,
            CancellationToken.None);

        Assert.Equal(CreateLinkError.None, result.Error);
        Assert.NotNull(result.Link);
    }

    [Theory]
    [InlineData("health")]
    [InlineData("READY")]
    [InlineData("openapi")]
    public async Task CreateAsync_RejectsReservedCustomCodes(string code)
    {
        var service = CreateService(new InMemoryUrlStore());

        var result = await service.CreateAsync(
            new CreateShortLinkRequest
            {
                OriginalUrl = "https://example.com",
                CustomCode = code
            },
            null,
            CancellationToken.None);

        Assert.Equal(CreateLinkError.ReservedCode, result.Error);
    }

    [Fact]
    public async Task CreateAsync_ReplaysIdempotentRequestAndRejectsKeyReuse()
    {
        var service = CreateService(new InMemoryUrlStore());
        var request = new CreateShortLinkRequest { OriginalUrl = "https://example.com/path" };

        var first = await service.CreateAsync(request, "retry-key-1", CancellationToken.None);
        var replay = await service.CreateAsync(request, "retry-key-1", CancellationToken.None);
        var conflictingReplay = await service.CreateAsync(
            request with { OriginalUrl = "https://example.org/other" },
            "retry-key-1",
            CancellationToken.None);

        Assert.Equal(CreateLinkError.None, first.Error);
        Assert.Equal(CreateLinkError.None, replay.Error);
        Assert.False(first.IdempotentReplay);
        Assert.True(replay.IdempotentReplay);
        Assert.Equal(first.Link!.Code, replay.Link!.Code);
        Assert.Equal(CreateLinkError.IdempotencyConflict, conflictingReplay.Error);
    }

    private static UrlShortenerService CreateService(InMemoryUrlStore store) =>
        new(store, new ManualTimeProvider(Now), NullLogger<UrlShortenerService>.Instance);

    private sealed class ManualTimeProvider(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset _now = now;

        public override DateTimeOffset GetUtcNow() => _now;

        public void SetUtcNow(DateTimeOffset value) => _now = value;
    }

    private sealed class InMemoryUrlStore : IUrlShortenerStore
    {
        private readonly Dictionary<string, ShortLink> _links = new(StringComparer.Ordinal);
        private readonly Dictionary<string, IdempotencyRecord> _idempotency = new(StringComparer.Ordinal);

        public Task InitializeAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task<StoreCreateResult> TryCreateAsync(
            ShortLink link,
            string? idempotencyKey,
            string requestFingerprint,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (idempotencyKey is not null && _idempotency.TryGetValue(idempotencyKey, out var existing))
            {
                return Task.FromResult(StringComparer.Ordinal.Equals(
                    existing.RequestFingerprint,
                    requestFingerprint)
                    ? new StoreCreateResult(StoreCreateStatus.IdempotentReplay, _links[existing.Code])
                    : new StoreCreateResult(StoreCreateStatus.IdempotencyConflict, null));
            }

            if (!_links.TryAdd(link.Code, link))
            {
                return Task.FromResult(new StoreCreateResult(StoreCreateStatus.CodeConflict, null));
            }

            if (idempotencyKey is not null)
            {
                _idempotency.Add(idempotencyKey, new IdempotencyRecord(requestFingerprint, link.Code));
            }

            return Task.FromResult(new StoreCreateResult(StoreCreateStatus.Created, link));
        }

        public Task<ShortLink?> FindAsync(string code, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _links.TryGetValue(code, out var link);
            return Task.FromResult(link);
        }

        public Task<ShortLink?> RecordClickAsync(
            string code,
            DateTimeOffset clickedAt,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!_links.TryGetValue(code, out var link))
            {
                return Task.FromResult<ShortLink?>(null);
            }

            var updated = link with
            {
                ClickCount = checked(link.ClickCount + 1),
                LastClickedAt = clickedAt
            };
            _links[code] = updated;
            return Task.FromResult<ShortLink?>(updated);
        }

        public Task FlushAnalyticsAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task CheckReadinessAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
