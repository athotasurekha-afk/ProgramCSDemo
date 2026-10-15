using System.Text.Json;
using ProgramCSDemo.Domain;
using ProgramCSDemo.Storage;
using Xunit;

namespace ProgramCSDemo.Tests;

public sealed class JsonUrlShortenerStoreTests
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    [Fact]
    public async Task AppendedLinksAndBatchedClicksSurviveRestart()
    {
        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.Path, "links.jsonl");
        using var store = new JsonUrlShortenerStore(path);
        await store.InitializeAsync(CancellationToken.None);
        var link = NewLink("persisted");

        var created = await store.TryCreateAsync(link, "retry-token", "fingerprint", CancellationToken.None);
        Assert.Equal(StoreCreateStatus.Created, created.Status);

        await Task.WhenAll(Enumerable.Range(0, 200).Select(index =>
            store.RecordClickAsync(
                "persisted",
                DateTimeOffset.UnixEpoch.AddSeconds(index),
                CancellationToken.None)));
        var inMemory = await store.FindAsync("persisted", CancellationToken.None);
        Assert.Equal(200, inMemory!.ClickCount);
        await store.FlushAnalyticsAsync(CancellationToken.None);

        using var restarted = new JsonUrlShortenerStore(path);
        await restarted.InitializeAsync(CancellationToken.None);
        var persisted = await restarted.FindAsync("persisted", CancellationToken.None);
        var replay = await restarted.TryCreateAsync(
            NewLink("new-code"),
            "retry-token",
            "fingerprint",
            CancellationToken.None);

        Assert.Equal(200, persisted!.ClickCount);
        Assert.Equal(DateTimeOffset.UnixEpoch.AddSeconds(199), persisted.LastClickedAt);
        Assert.Equal(StoreCreateStatus.IdempotentReplay, replay.Status);
        Assert.Equal("persisted", replay.Link!.Code);
        Assert.Equal(2, File.ReadAllLines(path).Length);
    }

    [Fact]
    public async Task LegacySnapshotMigratesAndPreservesBackup()
    {
        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.Path, "links.json");
        var original = new[] { NewLink("legacy"), NewLink("another") };
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(original));

        using var store = new JsonUrlShortenerStore(path);
        await store.InitializeAsync(CancellationToken.None);

        Assert.Equal(original[0], await store.FindAsync("legacy", CancellationToken.None));
        Assert.Equal(original[1], await store.FindAsync("another", CancellationToken.None));
        Assert.Single(Directory.GetFiles(directory.Path, "*.legacy.bak"));
        Assert.Equal(2, File.ReadAllLines(path).Length);

        var updated = await store.TryCreateAsync(
            NewLink("post-migration"),
            null,
            "fingerprint",
            CancellationToken.None);
        Assert.Equal(StoreCreateStatus.Created, updated.Status);
    }

    [Fact]
    public async Task EmptyLegacySnapshotMigratesToAnEmptyJournal()
    {
        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.Path, "links.json");
        await File.WriteAllTextAsync(path, "[]");

        using var store = new JsonUrlShortenerStore(path);
        await store.InitializeAsync(CancellationToken.None);

        Assert.Empty(await File.ReadAllTextAsync(path));
        Assert.Single(Directory.GetFiles(directory.Path, "*.legacy.bak"));
    }

    [Fact]
    public async Task ReadinessProbeConfirmsWritableStoreDirectory()
    {
        using var directory = new TemporaryDirectory();
        using var store = new JsonUrlShortenerStore(Path.Combine(directory.Path, "links.jsonl"));
        await store.InitializeAsync(CancellationToken.None);

        await store.CheckReadinessAsync(CancellationToken.None);

        Assert.Empty(Directory.GetFiles(directory.Path, "*.ready"));
    }

    [Fact]
    public async Task IncompleteFinalJournalRecordIsDiscardedOnRecovery()
    {
        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.Path, "links.jsonl");
        using var store = new JsonUrlShortenerStore(path);
        await store.InitializeAsync(CancellationToken.None);
        await store.TryCreateAsync(NewLink("durable"), null, "fingerprint", CancellationToken.None);
        await File.AppendAllTextAsync(path, "{\"kind\":\"create\"");

        using var recovered = new JsonUrlShortenerStore(path);
        await recovered.InitializeAsync(CancellationToken.None);

        Assert.NotNull(await recovered.FindAsync("durable", CancellationToken.None));
        Assert.Single(File.ReadAllLines(path));
    }

    [Fact]
    public async Task AppendRepairsAnIncompleteTailBeforeWritingNextCreate()
    {
        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.Path, "links.jsonl");
        using var store = new JsonUrlShortenerStore(path);
        await store.InitializeAsync(CancellationToken.None);
        await store.TryCreateAsync(NewLink("first"), null, "fingerprint", CancellationToken.None);
        await File.AppendAllTextAsync(path, "{\"kind\":\"create\"");

        var second = await store.TryCreateAsync(
            NewLink("second"),
            null,
            "fingerprint",
            CancellationToken.None);

        Assert.Equal(StoreCreateStatus.Created, second.Status);
        using var restarted = new JsonUrlShortenerStore(path);
        await restarted.InitializeAsync(CancellationToken.None);
        Assert.NotNull(await restarted.FindAsync("first", CancellationToken.None));
        Assert.NotNull(await restarted.FindAsync("second", CancellationToken.None));
    }

    [Fact]
    public async Task ClickJournalStoresCompactDeltasRatherThanFullLinkSnapshots()
    {
        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.Path, "links.jsonl");
        using var store = new JsonUrlShortenerStore(path);
        await store.InitializeAsync(CancellationToken.None);
        await store.TryCreateAsync(NewLink("compact"), null, "fingerprint", CancellationToken.None);
        await Task.WhenAll(Enumerable.Range(0, 10_000).Select(index =>
            store.RecordClickAsync(
                "compact",
                DateTimeOffset.UnixEpoch.AddSeconds(index),
                CancellationToken.None)));
        await store.FlushAnalyticsAsync(CancellationToken.None);

        var lines = await File.ReadAllLinesAsync(path);
        Assert.Equal(2, lines.Length);
        Assert.Contains("\"kind\":\"click-delta\"", lines[1]);
        Assert.Contains("\"clickDelta\":10000", lines[1]);
        Assert.DoesNotContain("\"originalUrl\"", lines[1]);

        using var recovered = new JsonUrlShortenerStore(path);
        await recovered.InitializeAsync(CancellationToken.None);
        Assert.Equal(10_000, (await recovered.FindAsync("compact", CancellationToken.None))!.ClickCount);
    }

    [Fact]
    public async Task ExportProducesLegacyArrayForRollback()
    {
        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.Path, "links.jsonl");
        var exportPath = Path.Combine(directory.Path, "rollback.json");
        using var store = new JsonUrlShortenerStore(path);
        await store.InitializeAsync(CancellationToken.None);
        await store.TryCreateAsync(NewLink("exported"), null, "fingerprint", CancellationToken.None);
        await store.RecordClickAsync("exported", DateTimeOffset.UnixEpoch, CancellationToken.None);
        await store.FlushAnalyticsAsync(CancellationToken.None);

        await store.ExportLegacySnapshotAsync(exportPath, CancellationToken.None);

        var exported = JsonSerializer.Deserialize<List<ShortLink>>(
            await File.ReadAllTextAsync(exportPath),
            JsonOptions);
        var exportedLink = Assert.Single(exported!);
        Assert.Equal(1, exportedLink.ClickCount);
    }

    private static ShortLink NewLink(string code) =>
        new(
            code,
            "https://example.com/",
            DateTimeOffset.UnixEpoch,
            null,
            0,
            null);

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                $"url-shortener-tests-{Guid.NewGuid():N}");
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
