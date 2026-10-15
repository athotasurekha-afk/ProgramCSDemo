using System.Diagnostics;
using ProgramCSDemo.Domain;
using ProgramCSDemo.Storage;
using Xunit;

namespace ProgramCSDemo.Tests;

public sealed class UrlShortenerPerformanceTests
{
    [Fact]
    [Trait("Category", "Performance")]
    public async Task ConcurrentClickUpdatesCompleteWithoutPerClickFileWrites()
    {
        using var directory = new TemporaryDirectory();
        using var store = new JsonUrlShortenerStore(Path.Combine(directory.Path, "links.jsonl"));
        await store.InitializeAsync(CancellationToken.None);
        await store.TryCreateAsync(
            new ShortLink(
                "perf",
                "https://example.com/",
                DateTimeOffset.UtcNow,
                null,
                0,
                null),
            null,
            "fingerprint",
            CancellationToken.None);

        var stopwatch = Stopwatch.StartNew();
        await Task.WhenAll(Enumerable.Range(0, 5_000).Select(index =>
            store.RecordClickAsync(
                "perf",
                DateTimeOffset.UnixEpoch.AddMilliseconds(index),
                CancellationToken.None)));
        stopwatch.Stop();

        var link = await store.FindAsync("perf", CancellationToken.None);
        Assert.Equal(5_000, link!.ClickCount);
        Assert.True(
            stopwatch.Elapsed < TimeSpan.FromSeconds(15),
            $"In-memory click updates took {stopwatch.Elapsed} for 5,000 concurrent operations.");

        await store.FlushAnalyticsAsync(CancellationToken.None);
        Assert.Equal(2, File.ReadAllLines(Path.Combine(directory.Path, "links.jsonl")).Length);
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                $"url-shortener-performance-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }
        }
    }
}
