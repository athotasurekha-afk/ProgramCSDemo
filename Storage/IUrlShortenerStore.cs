using ProgramCSDemo.Domain;

namespace ProgramCSDemo.Storage;

internal interface IUrlShortenerStore
{
    Task InitializeAsync(CancellationToken cancellationToken);
    Task<StoreCreateResult> TryCreateAsync(
        ShortLink link,
        string? idempotencyKey,
        string requestFingerprint,
        CancellationToken cancellationToken);
    Task<ShortLink?> FindAsync(string code, CancellationToken cancellationToken);
    Task<ShortLink?> RecordClickAsync(
        string code,
        DateTimeOffset clickedAt,
        CancellationToken cancellationToken);
    Task FlushAnalyticsAsync(CancellationToken cancellationToken);
    Task CheckReadinessAsync(CancellationToken cancellationToken);
}

internal enum StoreCreateStatus
{
    Created,
    IdempotentReplay,
    CodeConflict,
    IdempotencyConflict
}

internal sealed record StoreCreateResult(StoreCreateStatus Status, ShortLink? Link);
