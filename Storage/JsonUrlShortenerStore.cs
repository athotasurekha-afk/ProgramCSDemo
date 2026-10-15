using System.Collections.Concurrent;
using System.Text.Json;
using ProgramCSDemo.Domain;

namespace ProgramCSDemo.Storage;

internal sealed class JsonUrlShortenerStore : IUrlShortenerStore, IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly string _filePath;
    private readonly string _directoryPath;
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private readonly ConcurrentDictionary<string, ShortLink> _links = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, IdempotencyRecord> _idempotency =
        new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, PendingClick> _dirtyClicks =
        new(StringComparer.Ordinal);
    private volatile bool _initialized;

    public void Dispose() => _writeGate.Dispose();

    public JsonUrlShortenerStore(string filePath)
    {
        _filePath = Path.GetFullPath(filePath);
        _directoryPath = Path.GetDirectoryName(_filePath)
            ?? throw new ArgumentException("The store path must include a directory.", nameof(filePath));
    }

    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        await _writeGate.WaitAsync(cancellationToken);
        try
        {
            if (_initialized)
            {
                return;
            }

            Directory.CreateDirectory(_directoryPath);
            if (File.Exists(_filePath))
            {
                if (IsLegacySnapshot())
                {
                    await MigrateLegacySnapshotAsync(cancellationToken);
                }
                else
                {
                    RepairIncompleteJournalTail(_filePath);
                    await LoadJournalAsync(cancellationToken);
                }
            }

            await using (new FileStream(
                _filePath,
                FileMode.OpenOrCreate,
                FileAccess.ReadWrite,
                FileShare.ReadWrite,
                bufferSize: 1,
                FileOptions.Asynchronous))
            {
            }

            _initialized = true;
        }
        finally
        {
            _writeGate.Release();
        }
    }

    public async Task<StoreCreateResult> TryCreateAsync(
        ShortLink link,
        string? idempotencyKey,
        string requestFingerprint,
        CancellationToken cancellationToken)
    {
        await EnsureInitializedAsync(cancellationToken);
        await _writeGate.WaitAsync(cancellationToken);
        try
        {
            if (idempotencyKey is not null
                && _idempotency.TryGetValue(idempotencyKey, out var previousRequest))
            {
                if (!StringComparer.Ordinal.Equals(
                    previousRequest.RequestFingerprint,
                    requestFingerprint))
                {
                    return new StoreCreateResult(StoreCreateStatus.IdempotencyConflict, null);
                }

                if (!_links.TryGetValue(previousRequest.Code, out var previousLink))
                {
                    throw new InvalidDataException("An idempotency record refers to a missing short link.");
                }

                return new StoreCreateResult(StoreCreateStatus.IdempotentReplay, previousLink);
            }

            if (_links.ContainsKey(link.Code))
            {
                return new StoreCreateResult(StoreCreateStatus.CodeConflict, null);
            }

            var entry = new LinkJournalEntry(
                "create",
                link,
                idempotencyKey,
                idempotencyKey is null ? null : requestFingerprint);
            await AppendEntriesAsync([entry], cancellationToken);

            if (!_links.TryAdd(link.Code, link))
            {
                throw new InvalidOperationException("Short-code uniqueness changed during serialized creation.");
            }

            if (idempotencyKey is not null
                && !_idempotency.TryAdd(
                    idempotencyKey,
                    new IdempotencyRecord(requestFingerprint, link.Code)))
            {
                throw new InvalidOperationException("Idempotency-key uniqueness changed during serialized creation.");
            }

            return new StoreCreateResult(StoreCreateStatus.Created, link);
        }
        finally
        {
            _writeGate.Release();
        }
    }

    public async Task<ShortLink?> FindAsync(string code, CancellationToken cancellationToken)
    {
        await EnsureInitializedAsync(cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        return _links.GetValueOrDefault(code);
    }

    public async Task<ShortLink?> RecordClickAsync(
        string code,
        DateTimeOffset clickedAt,
        CancellationToken cancellationToken)
    {
        await EnsureInitializedAsync(cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();

        while (_links.TryGetValue(code, out var existing))
        {
            var lastClickedAt = existing.LastClickedAt is { } prior && prior > clickedAt
                ? prior
                : clickedAt;
            var updated = existing with
            {
                ClickCount = checked(existing.ClickCount + 1),
                LastClickedAt = lastClickedAt
            };
            if (_links.TryUpdate(code, updated, existing))
            {
                _dirtyClicks.AddOrUpdate(
                    code,
                    _ => new PendingClick(1, clickedAt),
                    (_, current) => new PendingClick(
                        checked(current.Count + 1),
                        current.LastClickedAt > clickedAt ? current.LastClickedAt : clickedAt));
                return updated;
            }
        }

        return null;
    }

    public async Task FlushAnalyticsAsync(CancellationToken cancellationToken)
    {
        await EnsureInitializedAsync(cancellationToken);
        await _writeGate.WaitAsync(cancellationToken);
        try
        {
            var changedLinks = new List<LinkJournalEntry>();
            foreach (var code in _dirtyClicks.Keys)
            {
                if (!_dirtyClicks.TryRemove(code, out var pendingClicks))
                {
                    continue;
                }

                changedLinks.Add(new LinkJournalEntry(
                    "click-delta",
                    Code: code,
                    ClickDelta: pendingClicks.Count,
                    LastClickedAt: pendingClicks.LastClickedAt));
            }

            if (changedLinks.Count == 0)
            {
                return;
            }

            var persisted = false;
            try
            {
                await AppendEntriesAsync(changedLinks, cancellationToken);
                persisted = true;
            }
            finally
            {
                if (!persisted)
                {
                    foreach (var entry in changedLinks)
                    {
                        _dirtyClicks.AddOrUpdate(
                            entry.Code!,
                            _ => new PendingClick(entry.ClickDelta, entry.LastClickedAt!.Value),
                            (_, current) => new PendingClick(
                                checked(current.Count + entry.ClickDelta),
                                current.LastClickedAt > entry.LastClickedAt
                                    ? current.LastClickedAt
                                    : entry.LastClickedAt!.Value));
                    }
                }
            }
        }
        finally
        {
            _writeGate.Release();
        }
    }

    public async Task CheckReadinessAsync(CancellationToken cancellationToken)
    {
        await EnsureInitializedAsync(cancellationToken);
        await _writeGate.WaitAsync(cancellationToken);
        var probePath = $"{_filePath}.{Guid.NewGuid():N}.ready";
        try
        {
            await using (var journal = new FileStream(
                _filePath,
                FileMode.Open,
                FileAccess.ReadWrite,
                FileShare.Read,
                bufferSize: 1,
                FileOptions.Asynchronous))
            {
                await journal.FlushAsync(cancellationToken);
            }

            await using var stream = new FileStream(
                probePath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 1,
                FileOptions.Asynchronous | FileOptions.WriteThrough);
            await stream.WriteAsync(new byte[] { 1 }, cancellationToken);
            await stream.FlushAsync(cancellationToken);
            stream.Flush(flushToDisk: true);
        }
        finally
        {
            try
            {
                if (File.Exists(probePath))
                {
                    File.Delete(probePath);
                }
            }
            finally
            {
                _writeGate.Release();
            }
        }
    }

    public async Task ExportLegacySnapshotAsync(
        string outputPath,
        CancellationToken cancellationToken)
    {
        await EnsureInitializedAsync(cancellationToken);
        var fullOutputPath = Path.GetFullPath(outputPath);
        if (StringComparer.OrdinalIgnoreCase.Equals(fullOutputPath, _filePath))
        {
            throw new ArgumentException("The legacy export must not overwrite the active journal.", nameof(outputPath));
        }

        var outputDirectory = Path.GetDirectoryName(fullOutputPath)
            ?? throw new ArgumentException("The export path must include a directory.", nameof(outputPath));
        Directory.CreateDirectory(outputDirectory);

        await _writeGate.WaitAsync(cancellationToken);
        var temporaryPath = $"{fullOutputPath}.{Guid.NewGuid():N}.tmp";
        try
        {
            var links = _links.Values.OrderBy(link => link.Code, StringComparer.Ordinal).ToArray();
            await using (var stream = new FileStream(
                temporaryPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 4096,
                FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await JsonSerializer.SerializeAsync(stream, links, JsonOptions, cancellationToken);
                await stream.FlushAsync(cancellationToken);
                stream.Flush(flushToDisk: true);
            }

            File.Move(temporaryPath, fullOutputPath, overwrite: true);
        }
        finally
        {
            try
            {
                if (File.Exists(temporaryPath))
                {
                    File.Delete(temporaryPath);
                }
            }
            finally
            {
                _writeGate.Release();
            }
        }
    }

    private async Task EnsureInitializedAsync(CancellationToken cancellationToken)
    {
        if (!_initialized)
        {
            await InitializeAsync(cancellationToken);
        }
    }

    private bool IsLegacySnapshot()
    {
        using var reader = new StreamReader(File.OpenRead(_filePath));
        int character;
        while ((character = reader.Read()) != -1)
        {
            if (!char.IsWhiteSpace((char)character))
            {
                return character == '[';
            }
        }

        return false;
    }

    private async Task MigrateLegacySnapshotAsync(CancellationToken cancellationToken)
    {
        List<ShortLink> legacyLinks;
        await using (var source = File.OpenRead(_filePath))
        {
            legacyLinks = await JsonSerializer.DeserializeAsync<List<ShortLink>>(
                source,
                JsonOptions,
                cancellationToken)
                ?? throw new InvalidDataException("The legacy URL store must contain a JSON array of links.");
        }

        var temporaryPath = $"{_filePath}.{Guid.NewGuid():N}.migration";
        var backupPath = $"{_filePath}.{DateTimeOffset.UtcNow:yyyyMMddHHmmssfff}.{Guid.NewGuid():N}.legacy.bak";
        try
        {
            foreach (var link in legacyLinks)
            {
                if (!_links.TryAdd(link.Code, link))
                {
                    throw new InvalidDataException("The legacy URL store contains duplicate short codes.");
                }
            }

            var entries = legacyLinks.Select(link => new LinkJournalEntry("create", link)).ToArray();
            await WriteEntriesAsync(temporaryPath, FileMode.CreateNew, entries, cancellationToken);
            File.Copy(_filePath, backupPath, overwrite: false);
            File.Move(temporaryPath, _filePath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    private async Task LoadJournalAsync(CancellationToken cancellationToken)
    {
        using var reader = new StreamReader(File.OpenRead(_filePath));
        while (await reader.ReadLineAsync(cancellationToken) is { } line)
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            var entry = JsonSerializer.Deserialize<LinkJournalEntry>(line, JsonOptions)
                ?? throw new InvalidDataException("The URL journal contains an empty record.");

            switch (entry.Kind)
            {
                case "create":
                    var createdLink = entry.Link
                        ?? throw new InvalidDataException("A URL create record is missing its link.");
                    if (!_links.TryAdd(createdLink.Code, createdLink))
                    {
                        throw new InvalidDataException("The URL journal contains duplicate short codes.");
                    }

                    if (entry.IdempotencyKey is not null)
                    {
                        if (entry.RequestFingerprint is null
                            || !_idempotency.TryAdd(
                                entry.IdempotencyKey,
                                new IdempotencyRecord(entry.RequestFingerprint, createdLink.Code)))
                        {
                            throw new InvalidDataException("The URL journal contains an invalid idempotency record.");
                        }
                    }

                    break;
                case "click":
                    var clickedLink = entry.Link
                        ?? throw new InvalidDataException("A URL click record is missing its link.");
                    if (!_links.ContainsKey(clickedLink.Code))
                    {
                        throw new InvalidDataException("A URL journal click record refers to a missing short link.");
                    }

                    _links[clickedLink.Code] = clickedLink;
                    break;
                case "click-delta":
                    if (string.IsNullOrEmpty(entry.Code)
                        || entry.ClickDelta <= 0
                        || entry.LastClickedAt is null
                        || !_links.TryGetValue(entry.Code, out var current))
                    {
                        throw new InvalidDataException("The URL journal contains an invalid click-delta record.");
                    }

                    _links[entry.Code] = current with
                    {
                        ClickCount = checked(current.ClickCount + entry.ClickDelta),
                        LastClickedAt = current.LastClickedAt is { } previous && previous > entry.LastClickedAt
                            ? previous
                            : entry.LastClickedAt
                    };
                    break;
                default:
                    throw new InvalidDataException($"Unsupported URL journal record type: {entry.Kind}");
            }
        }
    }

    private Task AppendEntriesAsync(
        IReadOnlyCollection<LinkJournalEntry> entries,
        CancellationToken cancellationToken) =>
        WriteEntriesAsync(_filePath, FileMode.Append, entries, cancellationToken);

    private static async Task WriteEntriesAsync(
        string path,
        FileMode fileMode,
        IReadOnlyCollection<LinkJournalEntry> entries,
        CancellationToken cancellationToken)
    {
        if (entries.Count == 0 && fileMode == FileMode.Append)
        {
            return;
        }

        if (fileMode == FileMode.Append)
        {
            RepairIncompleteJournalTail(path);
        }

        await using var stream = new FileStream(
            path,
            fileMode == FileMode.Append ? FileMode.OpenOrCreate : fileMode,
            FileAccess.Write,
            FileShare.Read,
            bufferSize: 4096,
            FileOptions.Asynchronous | FileOptions.WriteThrough);
        if (fileMode == FileMode.Append)
        {
            stream.Seek(0, SeekOrigin.End);
        }

        foreach (var entry in entries)
        {
            var json = JsonSerializer.SerializeToUtf8Bytes(entry, JsonOptions);
            await stream.WriteAsync(json, cancellationToken);
            await stream.WriteAsync("\n"u8.ToArray(), cancellationToken);
        }

        await stream.FlushAsync(cancellationToken);
        stream.Flush(flushToDisk: true);
    }

    private static void RepairIncompleteJournalTail(string path)
    {
        if (!File.Exists(path))
        {
            return;
        }

        using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.ReadWrite,
            FileShare.Read);
        if (stream.Length == 0)
        {
            return;
        }

        stream.Position = stream.Length - 1;
        if (stream.ReadByte() == '\n')
        {
            return;
        }

        var position = stream.Length - 1;
        while (position >= 0)
        {
            stream.Position = position;
            if (stream.ReadByte() == '\n')
            {
                stream.SetLength(position + 1);
                stream.Flush(flushToDisk: true);
                return;
            }

            position--;
        }

        stream.SetLength(0);
        stream.Flush(flushToDisk: true);
    }
}
