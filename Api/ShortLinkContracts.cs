using System.ComponentModel.DataAnnotations;

namespace ProgramCSDemo.Api;

/// <summary>Payload for creating a short link.</summary>
public sealed record CreateShortLinkRequest
{
    /// <summary>The absolute HTTP or HTTPS URL to shorten.</summary>
    [Required, MaxLength(2048)]
    public required string? OriginalUrl { get; init; }

    /// <summary>Optional human-readable code. Use 4-32 letters, digits, hyphens, or underscores.</summary>
    [MaxLength(32)]
    public string? CustomCode { get; init; }

    /// <summary>Optional instant after which the redirect is no longer available.</summary>
    public DateTimeOffset? ExpiresAt { get; init; }
}

/// <summary>A created short link and its redirect path.</summary>
public sealed record ShortLinkResponse(
    string Code,
    string RedirectPath,
    DateTimeOffset CreatedAt,
    DateTimeOffset? ExpiresAt);

/// <summary>Click metrics for a short link.</summary>
public sealed record LinkAnalyticsResponse(
    string Code,
    long ClickCount,
    DateTimeOffset CreatedAt,
    DateTimeOffset? ExpiresAt,
    DateTimeOffset? LastClickedAt);

/// <summary>A basic liveness or storage-readiness status.</summary>
public sealed record HealthResponse(string Status);

/// <summary>Basic service identity and operational status.</summary>
public sealed record ServiceStatusResponse(string Service, string Status);
