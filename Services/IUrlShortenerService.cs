using ProgramCSDemo.Api;
using ProgramCSDemo.Domain;

namespace ProgramCSDemo.Services;

internal interface IUrlShortenerService
{
    Task<CreateLinkResult> CreateAsync(
        CreateShortLinkRequest request,
        string? idempotencyKey,
        CancellationToken cancellationToken);

    Task<LinkResolutionResult> ResolveAsync(string code, CancellationToken cancellationToken);
    Task<ShortLink?> GetAnalyticsAsync(string code, CancellationToken cancellationToken);
}

internal sealed record CreateLinkResult(
    ShortLink? Link,
    CreateLinkError Error,
    bool IdempotentReplay = false);

internal sealed record LinkResolutionResult(ShortLink? Link, LinkResolutionStatus Status);

internal enum CreateLinkError
{
    None,
    InvalidUrl,
    UnsafeUrl,
    InvalidCode,
    ReservedCode,
    InvalidIdempotencyKey,
    IdempotencyConflict,
    ExpirationInPast,
    CodeConflict
}

internal enum LinkResolutionStatus
{
    Found,
    NotFound,
    Expired
}
