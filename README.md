# URL Shortener — AI-Assisted Engineering Prototype

Runnable .NET 9 minimal API for the interview assignment. The prototype covers link creation, redirects, expiration, aggregate analytics, safe retries, readiness, rate limiting, durable link journaling, integration tests, CI, and container packaging. AI accelerates bounded engineering tasks; a human engineer owns review, correctness, and release approval.

## Run and validate

Prerequisite: .NET 9 SDK.

```powershell
dotnet run --project .\ProgramCSDemo.csproj
dotnet build .\ProgramCSDemo.csproj
dotnet test .\Tests\ProgramCSDemo.Tests.csproj
```

The HTTP launch profile listens on `http://localhost:5232`. In Development, the OpenAPI document is at `/openapi/v1.json`. The committed [openapi.json](./openapi.json) is the API contract snapshot. See [UrlShortener.http](./UrlShortener.http) for manual API examples. The xUnit suite includes service tests, real-file-store tests, and isolated `WebApplicationFactory` HTTP tests.

Link data defaults to `App_Data\links.json`. Configure `UrlShortener:DataPath` in appsettings or set `URL_SHORTENER_DATA_PATH` to override it (environment variable takes precedence). The process needs write access to its directory. Link creation appends a journal record; click analytics are coalesced and journaled by a background service every two seconds. Journal records, migration, and recovery are specified in [docs/journal-format.md](./docs/journal-format.md).

Rate limits are configurable under `RateLimiting:Create` and `RateLimiting:Redirect`; each section has `PermitLimit` and `WindowSeconds`. Defaults are 30 creates/minute and 300 redirects/minute per observed client IP. Do not accept forwarded IP headers from arbitrary proxies: configure trusted proxy networks before relying on client-IP limits.

### Docker

```powershell
docker build -t url-shortener .
docker run --rm -p 8080:8080 -v url-shortener-data:/data url-shortener
```

The image runs as the non-root `app` user and stores records in the mounted `/data` volume. `/health` is a process liveness check and `/ready` verifies writable storage.

### CI

GitHub Actions (`.github/workflows/ci.yml`) verifies formatting, builds with recommended .NET analyzers treated as errors, scans NuGet dependencies, runs the xUnit suite with coverage, runs the performance smoke test, and builds the Docker image on pushes and pull requests. The workflow definition is committed; hosted execution is a separate CI result and is not implied by a local run.

## Normalized requirements and API

`POST /api/links` accepts an absolute HTTP(S) URL, optional custom code, and optional future expiration. It returns `201 Created` with a relative redirect path and `Location`. `GET /{code}` returns a temporary `302` redirect with `Cache-Control: no-store`; unknown codes return `404`; expired codes return `410` and are not counted. `GET /api/links/{code}/analytics` returns aggregate clicks, creation/expiration, and last-click time without exposing the destination.

Custom codes are case-sensitive, 4-32 ASCII letters/digits/hyphens/underscores. Codes conflicting with the single-segment service routes (`health`, `ready`, `openapi`) are reserved case-insensitively. Duplicates return `409`. Create and redirect requests have configurable per-observed-client-IP limits; a rejected request returns `429` Problem Details.

Supply an `Idempotency-Key` header to make create retries safe. Keys allow 1-128 ASCII letters/digits/dots/colons/hyphens/underscores. A retry with the same key and same normalized request returns the original code and `Idempotency-Replayed: true`; reuse with a different request returns `409`. Keys have no expiry in this prototype and are persisted indefinitely. Without a key, creating the same destination again intentionally creates another link: destination equality cannot distinguish an accidental retry from two intended links.

### Destination safety policy

Only HTTP and HTTPS URLs are accepted. Empty/null URLs, credentials in the authority, localhost and `.localhost`/`.local`/`.internal`/`.home.arpa` names, single-label hostnames, and non-public IP literals (including loopback, private, link-local, documentation, multicast, and IPv4-mapped private IPv6) are rejected. The service does not resolve DNS names or fetch destinations. This prevents the tested metadata/private IP literal cases, but it cannot guarantee a public hostname will continue to resolve publicly (DNS rebinding, split-horizon DNS, or a later DNS change). The API is a redirector, not an SSRF-capable fetch proxy; users can still be redirected to a harmful public website. Production policy must be reviewed against the deployment's threat model.

## Architecture and reliability

```text
HTTP
  -> Minimal API (typed status mapping, Problem Details, per-IP create/redirect rate limits)
  -> IUrlShortenerService (validation, destination policy, code generation, idempotency fingerprint)
  -> IUrlShortenerStore
       -> ConcurrentDictionary (lock-free redirect/analytics reads; in-memory click counters)
       -> append-only JSON Lines journal (serialized creates; batched click checkpoints)
  -> App_Data\links.json
```

- **Creation durability:** A create record is appended and flushed before success is returned. Code allocation and idempotency key reservation are serialized within one process.
- **Redirect latency:** Mapping reads use `ConcurrentDictionary`; click increments are atomic in-memory compare/update operations. A redirect does not acquire the create/journal lock and does not write the file. The background worker writes at most one compact click-delta record per changed link per checkpoint interval.
- **Crash recovery:** Startup replays the journal and fails visibly on malformed complete records. An incomplete final record (a crash during append before its newline) is truncated; preceding complete records remain. A process crash can lose click updates not yet checkpointed (up to the two-second interval); create records are flushed before acknowledging.
- **Readiness:** `/ready` checks read/write access to the journal, then writes and flushes a temporary file in its directory. `/health` is liveness only and intentionally does not claim storage readiness.
- **Observability:** Structured logs report create outcomes and storage-readiness failures without logging destination URLs. Framework logs capture server/rate-limit events. There are no metrics backend or distributed tracing exporters.
- **Single instance:** The file journal and write semaphore are process-local. Do not share this path across replicas or run multiple writers. Journal compaction is not implemented; journal replay time and file size grow over time.

## Three assignment scenarios

These scenarios describe the real starter implementation and a realistic next change; they do not claim a separate legacy deployment or historical branch existed.

### 1. Greenfield — issue a short link

**Intent and tasks:** Define request/status contracts; validate URL and alias inputs; add the service/store boundaries; generate cryptographically random codes; persist a unique mapping; implement create and redirect endpoints.

**Code flow:** `POST /api/links` validates and journals the mapping; `GET /{code}` performs a concurrent lookup, checks expiry, updates the in-memory click counter, and redirects. Relative `Location` values avoid deriving public links from an untrusted request Host header.

**Validation:** Unit tests cover URL parsing, dangerous schemes, custom/generated codes, and expiry. HTTP integration tests verify actual create/redirect response headers and status codes.

### 2. Brownfield — evolve the existing snapshot store for analytics and reliable retries

**Baseline impact analysis:** The pre-change prototype persisted the `ShortLink` record fields (`Code`, `OriginalUrl`, `CreatedAt`, `ExpiresAt`, `ClickCount`, `LastClickedAt`) as one JSON array at `App_Data\links.json`. `JsonUrlShortenerStore.TryCreateAsync` and `RecordClickAsync` copied and rewrote the whole array under one semaphore; `FindAsync` also waited on that lock. The affected flow is API handler → service → store → data file. The starter has no external database, migration framework, or separately deployed legacy service.

**Change plan:** Preserve the path and existing record shape; on startup detect the old JSON array, validate duplicate codes, write a JSON Lines journal to a temporary file, keep a timestamped `.legacy.bak`, then atomically replace the original file. New creates append one `create` record; optional idempotency fingerprint is persisted with that record. Click updates are atomically maintained in memory and checkpointed as compact `click-delta` journal records. Reads no longer wait on the create/checkpoint gate.

**Compatibility and rollback:** Roll forward by taking a backup, deploying the new version against the existing data path, and checking `/ready`, link lookups, analytics, and the generated migration backup. A new journal is not readable by the old snapshot-only code. To roll back, stop the new process (do not export while writes are active), export the current journal to a separate legacy array, verify the export, then configure the old binary to read that exported file. Command: `dotnet run --project .\ProgramCSDemo.csproj -- --export-legacy .\rollback-links.json` (set `URL_SHORTENER_DATA_PATH` first if not using the default). The original legacy array backup remains available but does not include post-migration writes; the export does. Idempotency-key history is not represented in the old schema.

**Validation and risk:** Store tests migrate a legacy array, verify the backup, exercise journal replay and incomplete-tail recovery, and export the rollback array. HTTP tests verify the changed API contract. Before production rollout, test with a copied representative data file, record its hash/count, exercise rollback on a staging copy, and agree acceptable analytics loss during the checkpoint interval.

### 3. Ambiguous — “make links safer”

**Unanswered questions:** Does “safe” mean only valid redirect schemes, or should private networks, credentials, internal DNS names, or public sites with harmful content be blocked? Should internal URLs be allowed for employee use? Is hostname DNS resolution acceptable on the create path?

**Decision for this prototype:** Reject non-HTTP(S), credentials, localhost/local/internal/single-label hosts, and non-public IP literals; reserve service route names; never fetch the destination; do not perform DNS lookups. This is deterministic, fast, and blocks the specific metadata, localhost, and credential examples without making create depend on external DNS.

**Execution and validation:** `UrlDestinationPolicy` owns the boundary; service tests cover metadata IP, localhost, private IPv4, local suffix, credential-bearing URLs, and `javascript:`; the HTTP integration test verifies unsafe input is rejected before persistence. DNS rebinding and phishing remain documented risks, not silently claimed as solved.

**Human decision gate:** Before external production exposure, a product/security owner must approve whether this rule blocks all IP literals, whether domain DNS/IP policy is needed, internal-use exceptions, abuse reporting, and rate-limit thresholds. Do not weaken the policy based only on model-generated recommendations.

## AI-assisted execution and ownership

The task sequence and concrete generated/edited/rejected decisions are recorded separately in [AI-ASSISTED-ENGINEERING-LOG.md](./AI-ASSISTED-ENGINEERING-LOG.md). I did not put credentials, customer data, or proprietary source material into prompts; this work used the supplied assignment/review and local project. I reviewed the changes and keep release approval with a human engineer rather than treating AI output or passing tests as approval.

## Final engineering summary and assumptions

**Outcome:** This prototype provides create, redirect, expiry, aggregate analytics, optional idempotency, input screening, configurable rate limits, storage readiness, legacy migration/export, and automated service/store/HTTP tests.

**Decisions:** JSONL append-only storage avoids whole-file rewrites; atomic in-memory click updates keep file I/O off the redirect path; compact click deltas reduce journal growth. Caller-keyed idempotency distinguishes retries from intentional duplicate destinations. Rate limiting is per observed IP, not per user identity.

**Assumptions:** This is a single-writer local prototype. Default limits are demo values, not approved customer quotas. A public hostname does not guarantee that DNS remains public. Losing up to one checkpoint interval of clicks after abrupt termination is acceptable for this prototype, not a production analytics SLA.

**Human decision gates:** Approve redirect-host/DNS policy, internal-link needs, quota values and trusted-proxy configuration, idempotency retention, journal compaction/backup strategy, analytics-loss objectives, and deployment architecture before production.

## Task decomposition and dependencies

| Order | Task | Depends on | Acceptance evidence |
|---|---|---|---|
| 1 | Normalize create/redirect/expiry/analytics requirements and URL-safety decisions | — | API contracts and ambiguity decisions documented |
| 2 | Implement service validation, route reservations, retry handling, and JSONL store | 1 | Unit, recovery, migration, and export tests |
| 3 | Implement HTTP API, configurable quotas, readiness, and published schema | 1, 2 | Isolated WebApplicationFactory tests |
| 4 | Document setup, brownfield upgrade/rollback, journal schema, and AI traceability | 1, 2, 3 | README, journal format, execution log |
| 5 | Apply quality gates and validate deliverables | 2, 3, 4 | Format, analyzer build, dependency scan, tests, coverage, performance smoke |

## Known trade-offs and production gates

- Per-IP in-memory rate limiting is process-local, can behave poorly behind proxies without carefully configured trusted forwarded headers, and is not a substitute for edge protection or a distributed quota.
- DNS names are not resolved at creation. The service redirects but does not fetch, so this is not server-side request forgery protection for a fetcher; end users may nevertheless be redirected to internal destinations if DNS changes.
- Click analytics are approximate across abrupt process failure: dirty updates can be lost until the next two-second checkpoint. Redirect retries count as separate clicks. Checkpoints now contain count deltas, not full link snapshots.
- Append-only journal startup/replay and disk use grow over time. Add tested compaction or move to a transactional database before large-scale deployment. A disk failure blocks create/checkpoint operations; readiness reports storage failure.
- Idempotency records do not expire and grow with unique keys. Define a retention window and ownership/quota model before public use.
- No authentication, link ownership, deletion, abuse/phishing classification, destination reputation, per-visitor analytics, privacy retention, or multi-tenant isolation is implemented.
- Run load/fault-injection tests with production-like data and storage; validate backups, TLS, trusted proxy boundaries, structured log retention, alerting, and human security/operations approval before launch.
