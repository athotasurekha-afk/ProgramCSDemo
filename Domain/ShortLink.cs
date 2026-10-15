namespace ProgramCSDemo.Domain;

internal sealed record ShortLink(
    string Code,
    string OriginalUrl,
    DateTimeOffset CreatedAt,
    DateTimeOffset? ExpiresAt,
    long ClickCount,
    DateTimeOffset? LastClickedAt);

internal sealed record LinkJournalEntry(
    string Kind,
    ShortLink? Link = null,
    string? IdempotencyKey = null,
    string? RequestFingerprint = null,
    string? Code = null,
    long ClickDelta = 0,
    DateTimeOffset? LastClickedAt = null);

internal sealed record IdempotencyRecord(string RequestFingerprint, string Code);

internal sealed record PendingClick(long Count, DateTimeOffset LastClickedAt);
