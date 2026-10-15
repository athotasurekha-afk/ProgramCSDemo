using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ProgramCSDemo.Api;
using ProgramCSDemo.Domain;
using ProgramCSDemo.Storage;

namespace ProgramCSDemo.Services;

internal sealed class UrlShortenerService(
    IUrlShortenerStore store,
    TimeProvider timeProvider,
    ILogger<UrlShortenerService> logger) : IUrlShortenerService
{
    private const string CodeAlphabet = "0123456789abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ";
    private const int GeneratedCodeLength = 8;
    private const int MaximumIdempotencyKeyLength = 128;
    private static readonly Action<ILogger, Exception?> InvalidUrlRejected =
        LoggerMessage.Define(LogLevel.Information, new EventId(1, "InvalidUrlRejected"),
            "Short-link creation rejected: invalid URL format");
    private static readonly Action<ILogger, Exception?> UnsafeUrlRejected =
        LoggerMessage.Define(LogLevel.Information, new EventId(2, "UnsafeUrlRejected"),
            "Short-link creation rejected: unsafe destination host");
    private static readonly Action<ILogger, string, bool, Exception?> LinkCreated =
        LoggerMessage.Define<string, bool>(LogLevel.Information, new EventId(3, "LinkCreated"),
            "Short link {ShortCode} was created; idempotent replay: {IdempotentReplay}");
    private static readonly Action<ILogger, CreateLinkError, Exception?> CreationFailed =
        LoggerMessage.Define<CreateLinkError>(LogLevel.Information, new EventId(4, "CreationFailed"),
            "Short-link creation ended with result {CreateResult}");
    private static readonly HashSet<string> ReservedCodes = new(StringComparer.OrdinalIgnoreCase)
    {
        "health",
        "ready",
        "openapi"
    };

    public async Task<CreateLinkResult> CreateAsync(
        CreateShortLinkRequest request,
        string? idempotencyKey,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.OriginalUrl))
        {
            return new CreateLinkResult(null, CreateLinkError.InvalidUrl);
        }

        var originalUrl = request.OriginalUrl.Trim();
        if (originalUrl.Length > 2048
            || !Uri.TryCreate(originalUrl, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
            || string.IsNullOrWhiteSpace(uri.Host))
        {
            InvalidUrlRejected(logger, null);
            return new CreateLinkResult(null, CreateLinkError.InvalidUrl);
        }

        if (!UrlDestinationPolicy.IsAllowed(uri))
        {
            UnsafeUrlRejected(logger, null);
            return new CreateLinkResult(null, CreateLinkError.UnsafeUrl);
        }

        if (idempotencyKey is not null && !IsValidIdempotencyKey(idempotencyKey))
        {
            return new CreateLinkResult(null, CreateLinkError.InvalidIdempotencyKey);
        }

        var customCode = request.CustomCode?.Trim();
        if (customCode is not null && !IsValidCustomCode(customCode))
        {
            return new CreateLinkResult(null, CreateLinkError.InvalidCode);
        }

        if (customCode is not null && ReservedCodes.Contains(customCode))
        {
            return new CreateLinkResult(null, CreateLinkError.ReservedCode);
        }

        var now = timeProvider.GetUtcNow();
        if (request.ExpiresAt is { } expiration && expiration <= now)
        {
            return new CreateLinkResult(null, CreateLinkError.ExpirationInPast);
        }

        if (customCode is not null)
        {
            var customLink = new ShortLink(
                customCode,
                uri.AbsoluteUri,
                now,
                request.ExpiresAt,
                0,
                null);
            return MapStoreResult(await store.TryCreateAsync(
                customLink,
                idempotencyKey,
                CreateFingerprint(uri.AbsoluteUri, customCode, request.ExpiresAt),
                cancellationToken));
        }

        for (var attempt = 0; attempt < 5; attempt++)
        {
            var generatedLink = new ShortLink(
                GenerateCode(),
                uri.AbsoluteUri,
                now,
                request.ExpiresAt,
                0,
                null);
            var result = await store.TryCreateAsync(
                generatedLink,
                idempotencyKey,
                CreateFingerprint(uri.AbsoluteUri, null, request.ExpiresAt),
                cancellationToken);
            if (result.Status != StoreCreateStatus.CodeConflict)
            {
                return MapStoreResult(result);
            }
        }

        throw new InvalidOperationException("Unable to allocate a unique short code after five attempts.");
    }

    public async Task<LinkResolutionResult> ResolveAsync(
        string code,
        CancellationToken cancellationToken)
    {
        var link = await store.FindAsync(code, cancellationToken);
        if (link is null)
        {
            return new LinkResolutionResult(null, LinkResolutionStatus.NotFound);
        }

        if (link.ExpiresAt is { } expiration && expiration <= timeProvider.GetUtcNow())
        {
            return new LinkResolutionResult(link, LinkResolutionStatus.Expired);
        }

        var clickedLink = await store.RecordClickAsync(
            code,
            timeProvider.GetUtcNow(),
            cancellationToken);
        return clickedLink is null
            ? new LinkResolutionResult(null, LinkResolutionStatus.NotFound)
            : new LinkResolutionResult(clickedLink, LinkResolutionStatus.Found);
    }

    public Task<ShortLink?> GetAnalyticsAsync(string code, CancellationToken cancellationToken) =>
        store.FindAsync(code, cancellationToken);

    private static bool IsValidCustomCode(string code)
    {
        if (code.Length is < 4 or > 32)
        {
            return false;
        }

        return code.All(character =>
            char.IsAsciiLetterOrDigit(character) || character is '-' or '_');
    }

    private static bool IsValidIdempotencyKey(string key)
    {
        if (key.Length is 0 or > MaximumIdempotencyKeyLength)
        {
            return false;
        }

        return key.All(character =>
            char.IsAsciiLetterOrDigit(character) || character is '-' or '_' or '.' or ':');
    }

    private static string CreateFingerprint(
        string originalUrl,
        string? customCode,
        DateTimeOffset? expiresAt)
    {
        var canonicalRequest = JsonSerializer.Serialize(new
        {
            originalUrl,
            customCode,
            expiresAt = expiresAt?.ToUniversalTime()
        });
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonicalRequest)));
    }

    private CreateLinkResult MapStoreResult(StoreCreateResult result)
    {
        var mapped = result.Status switch
        {
            StoreCreateStatus.Created => new CreateLinkResult(result.Link, CreateLinkError.None),
            StoreCreateStatus.IdempotentReplay =>
                new CreateLinkResult(result.Link, CreateLinkError.None, IdempotentReplay: true),
            StoreCreateStatus.CodeConflict => new CreateLinkResult(null, CreateLinkError.CodeConflict),
            StoreCreateStatus.IdempotencyConflict =>
                new CreateLinkResult(null, CreateLinkError.IdempotencyConflict),
            _ => throw new InvalidOperationException("Unknown link-store create result.")
        };

        if (mapped.Link is not null)
        {
            LinkCreated(logger, mapped.Link.Code, mapped.IdempotentReplay, null);
        }
        else if (mapped.Error != CreateLinkError.None)
        {
            CreationFailed(logger, mapped.Error, null);
        }

        return mapped;
    }

    private static string GenerateCode()
    {
        return string.Create(
            GeneratedCodeLength,
            CodeAlphabet,
            static (characters, alphabet) =>
            {
                for (var index = 0; index < characters.Length; index++)
                {
                    characters[index] = alphabet[RandomNumberGenerator.GetInt32(alphabet.Length)];
                }
            });
    }
}
