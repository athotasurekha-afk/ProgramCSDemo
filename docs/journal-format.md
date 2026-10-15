# URL journal format and recovery

The configured data file is a UTF-8 JSON Lines append-only journal. Each complete record occupies one line, terminated by LF (`\n`). JSON uses `System.Text.Json` web defaults (camel-case property names). The journal is single-writer and must not be shared by multiple service processes.

## Record types

### Create (`kind: "create"`)

```json
{"kind":"create","link":{"code":"example","originalUrl":"https://example.com/","createdAt":"2026-10-15T12:00:00+00:00","expiresAt":null,"clickCount":0,"lastClickedAt":null},"idempotencyKey":"client-request-1","requestFingerprint":"SHA256-HEX"}
```

`idempotencyKey` and `requestFingerprint` are omitted or null when the caller did not provide an idempotency key. Create records are written and flushed before a successful create response.

### Click checkpoint (`kind: "click-delta"`)

```json
{"kind":"click-delta","code":"example","clickDelta":25,"lastClickedAt":"2026-10-15T12:02:00+00:00"}
```

The record contains a coalesced increment and the maximum click timestamp since the previous checkpoint, not a duplicate of the original URL/link. Click data is checkpointed by the background worker (default every two seconds); abrupt termination can lose the unflushed interval.

The reader also accepts the original prototype's `kind: "click"` record containing a full `link` snapshot for backward compatibility with journals written before delta checkpoints. This compatibility reader is intentionally retained during the prototype's format evolution.

## Startup and write-failure recovery

Startup migrates an old JSON-array snapshot to a same-directory temporary JSONL file, keeps an adjacent timestamped `.legacy.bak`, then atomically replaces the old file. For JSONL data, startup truncates an incomplete final line before replay. Before every subsequent append, the writer also checks and removes an unterminated tail. A later valid append can therefore never be concatenated to a failed partial record.

Complete malformed records are not silently skipped: startup fails with a data error so operators can restore a backup or investigate corruption. Appends are serialized in-process and flushed to disk; this is not a substitute for database transactions, multi-process locking, journal compaction, or tested backup/restore operations.

The store export command emits the legacy JSON-array shape for an offline rollback:

```powershell
dotnet run --project .\ProgramCSDemo.csproj -- --export-legacy .\rollback-links.json
```

Stop the service before exporting for rollback. The output path must differ from the active journal path.
