# AI-Assisted Engineering Log

I used the coding assistant to inspect the project, propose implementation changes, edit the application/tests/docs, and iterate on compiler/test failures. I made the engineering decisions below and verified the resulting changes with the checks listed at the end. The assistant did not approve or deploy the system.

I did not include credentials, customer data, or proprietary source material in prompts. The inputs for this pass were the assignment, the user's review findings, and code already present in this local project.

## Work items, sequence, and evidence

| ID | Task and dependency | Prompt intent / acceptance criteria | AI-generated or edited output | My decision and disposition |
|---|---|---|---|---|
| 1 | Fix unsafe destination handling; no prerequisite | Accept only supported public destination forms, including public IPv6; reject loopback/private addresses, credentials, local names, malformed and null URLs. | Reordered IPv4/IPv6 literal validation ahead of DNS-name checks and added regression cases. | Accepted literal-IP screening. I did not add DNS resolution because it creates a network-dependent create path and cannot prevent later DNS changes. I documented that limitation rather than claiming complete protection. |
| 2 | Repair journal tail and reduce analytics journal size; depends on task 1 review of core flow | A partial final write must never be concatenated with a later record; click checkpoints should not copy the destination URL. | Added tail repair before append and changed new analytics records to compact click deltas, retaining replay of earlier full-snapshot click records. | Accepted. Creates still flush before success. Click updates remain in-memory until checkpoint/shutdown, so abrupt termination can lose an unflushed interval. |
| 3 | Configure abuse controls and operational settings; depends on API review | Make create and redirect limits configurable; exercise both in HTTP tests; make data-path configuration consistent between run and rollback export. | Added settings for both route groups, a shared per-IP limiter policy helper, and configuration-aware export path selection. | Accepted the local per-IP limiter as a prototype guardrail, not a distributed quota. Trusted proxy configuration remains a deployment decision. |
| 4 | Replace process-spawn HTTP test harness; depends on endpoint/runtime review | Isolate each HTTP scenario, avoid test-order coupling and port/process management. | Replaced one process-hosted test with separate `WebApplicationFactory` tests and isolated temporary stores. Moved store initialization into host startup so test configuration is applied before the file path is resolved. | Accepted after the real xUnit runner passed each HTTP scenario. |
| 5 | Complete assignment evidence and quality gates; depends on tasks 1-4 | Add a solution, committed API schema, journal schema, assumptions/summary, explicit dependencies, coverage/performance/security/format CI steps, and human-owned AI traceability. | Added `.sln`, OpenAPI snapshot, docs, performance smoke test, analyzers-as-errors, coverage collector, and CI commands. | Kept claims limited to commands actually executed locally. GitHub Actions and Docker are not described as run unless actually executed there. |

## Shortcuts considered and rejected

- I rejected treating data annotations as the only validation: the explicit JSON `null` case needs a domain guard.
- I rejected URL-only deduplication: identical destinations may be intentional; retries use a caller-provided idempotency key.
- I rejected synchronous whole-link writes for every redirect: this put file I/O on the latency-sensitive path and duplicated destination data in analytics records.
- I rejected claiming hostname checks prevent DNS rebinding or phishing. The service does not fetch the URL, but the browser follows the redirect.
- I rejected multi-process/shared-volume deployment for the file journal. Its coordination is process-local.
- I retained .NET 9 to match the project target and available SDK rather than silently changing the deliverable's runtime.

## Validation record

This section records only validation performed from this workspace for the current revision:

- `dotnet build .\ProgramCSDemo.sln --configuration Release`: passed with zero warnings and zero errors.
- `dotnet test .\ProgramCSDemo.sln --configuration Release --no-build`: passed all 42 xUnit cases, including service, file-store, isolated HTTP, and performance tests.
- Development app OpenAPI smoke check: fetched `/openapi/v1.json`, parsed it as OpenAPI 3.0.1, and verified all six expected route paths before saving [openapi.json](./openapi.json).
- `dotnet list .\Tests\ProgramCSDemo.Tests.csproj package --vulnerable --include-transitive`: passed; no vulnerable packages were reported by the configured sources.
- `dotnet format .\ProgramCSDemo.sln --verify-no-changes --no-restore`: passed after applying the formatter to fix an import-order issue found by the initial check.
- `dotnet test .\Tests\ProgramCSDemo.Tests.csproj --configuration Release --no-build --collect:"XPlat Code Coverage"`: passed all 42 cases and emitted a Cobertura report under `Tests\TestResults`.
- `dotnet test .\Tests\ProgramCSDemo.Tests.csproj --configuration Release --no-build --filter "Category=Performance"`: passed the focused performance smoke test.
- Docker is not installed in this workspace, so the image build could not be run. Hosted GitHub Actions were not run.

## Human ownership and release gates

I own the implementation and its limitations. Before any production use, a human engineer/security owner must approve destination/DNS policy, internal-link needs, trusted proxy boundaries and quotas, idempotency retention, journal compaction and backup/restore, acceptable analytics loss, and single-writer deployment. Passing tests is evidence only for covered cases, not release approval.
