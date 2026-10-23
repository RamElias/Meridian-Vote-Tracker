# Verification record — 2026-10-23

The primary assignment PDF is authoritative; appendix B was used for presentation only. This record distinguishes performed checks from environment-dependent work.

## Requirements and evidence

| Area | Implementation / repeatable evidence |
|---|---|
| ASP.NET Core, Angular, PostgreSQL; API and Worker separate | `Meridian.VoteTracker.sln`, four-service `docker-compose.yml`, independent API/Worker entry points. Full .NET 8 solution and Angular builds pass. |
| Durable background work | Database jobs plus a separate throttled progress table. Reading, Staging, Validating, Promoting and Calculating have phase counters/units. Integration checks observe progress through a second connection before calculation commits. |
| CSV validation and partial acceptance | Original and normalized JSON retained separately; whitespace trim/session canonicalization recorded. Strict Y/N/A/X, country/date/ISO timestamp/CSV shape checks; inclusive membership validation against the approved date. Interleaved votes, overlap, duplicate files/rows, invalid rows and contradictions are exercised. |
| Correct import reports after failure | Promotion, row classifications, report and import/job completion are atomic. Cancellation at the final update rolls everything back; replay preserves New instead of changing it to NoChange. Deferred date decisions update affected reports in the same transaction. |
| Human conflict review | Separate vote/country scope; all proposals retain sources and stable indices. Concurrent decisions have one winner. Deferred rows are captured by ID before revalidation, leaving unrelated staging rows alone. |
| Provenance and history | Initial vote/ballot versions, later country additions, unchanged imports and decision/position audit appear in the timeline. Every current ballot includes source file, uploader and upload time. |
| Position concurrency and roles | `expectedVersionId` is required by the JSON contract (null for first creation). Stale writes return 409 with currentPosition. Actual HTTP checks verify 401, analyst 403, administrator writes and stale 409. |
| Snapshot selection and scoring | Unknown requested IDs return 422 with identifiers; missing positions block creation. Exact fixture verifies weighted alignment 66.7%, participation 75%, coverage 100%, and N/A when the denominator is zero. |
| Immutable snapshots and publication | Frozen contributions survive approved ballot/position edits. Results and snapshot completion commit together; restart reconciles an unacknowledged completed job. Concurrent publication has one winner; replacement and withdrawal retain history/audit. |
| DB protection | SQL triggers reject updates/deletes to approved versions and audit, and changes to completed snapshot inputs/results. Publication metadata can change; incomplete work can be cleared. Direct mutation attempts are integration checks. |
| Worker recovery/ownership | Tests cancel processors and also kill an actual Worker process before commit, start it again, and verify recovery. An additional Worker rejects ownership without resetting the first Worker's Running import. |
| API contract/client/errors | Angular aliases generated DTOs; the facade has no response casts. `npm run check:api` regenerates into a temporary directory and compares every file. Integration tests validate actual HTTP payload types/required fields/enums/nullability against OpenAPI. Internal failures expose safe text plus correlation ID; full exceptions and correlation scopes go to JSON logs. |
| UI separation and asynchronous selection | Imports, Review, Positions and Snapshots are standalone screen components with shared state. Five Angular tests pass, including rendered checks that late responses cannot mix contribution countries/snapshots. Field errors appear alongside upload, weight, selection, country and publication inputs. |
| Delivery and configuration | README, concise DECISIONS, AI_USAGE, OpenAPI, ignored `.env`, example configuration and both Compose files are present. Optional Key Vault, Blob, App Insights and external PostgreSQL configuration are implemented. |

## Runs performed

- .NET 8 solution build: passed, zero warnings/errors.
- Actual PostgreSQL integration suite: passed, including live HTTP API checks.
- Synthetic scale: 6,500 selected votes × 150 countries = **975,000 contributions**, completed with progress visible while work was uncommitted.
- Docker-only regression command: passed with `MERIDIAN_TEST_ARGS=--quick`; real process kill/restart and competing Worker were included. This is independent of the user's application containers/database.
- Angular production build: passed. Angular tests: **5/5 passed**.
- Generated Angular client consistency: passed.
- Both Compose configuration checks: passed.
- Original PostgreSQL schema/seed smoke: passed, including rejection of overlapping membership intervals.
- Original CSV parser smoke: passed; 5,896 Q1 records, malformed five-field-row recovery and quoted fields.
- Supplied historical CSV import: **962,171 rows**, **962,109 New**, **30 Duplicate**, **32 Invalid**. Upload, staging, validation and atomic promotion/report completed in **1,372.3 seconds** (about 23 minutes) while other verification work was running. This is a local functional scale measurement, not an isolated performance SLA. The historical snapshot also completed: **6,480 selected votes**, **972,000 contributions**, **358.0 seconds** (about 6 minutes). Institute positions were seeded to Y/weight 1 for this isolated volume check.

An initial synthetic volume run hit the test runner's three-minute cancellation limit. It was not counted as a success. The subsequent run with an appropriate background-work timeout completed and verified every contribution. An initial Docker runner copied the local SDK cache; the runner now copies only explicit source/project paths.

## Limits

Live Key Vault authentication, Blob upload/read, Application Insights ingestion and Azure PostgreSQL connectivity have not been verified against the supplied Azure account. They require its configured credentials/access. The application continues to run locally in Docker.

The ownership lock intentionally supports **one active Worker per database**. Distributed workers, lease expiry/fencing under network partitions, production identity, deployment and PDF extraction are outside this implementation. The tests are targeted regression evidence, not proof that every possible input or failure is covered. Historical reports created by older buggy code are not rewritten automatically; new processing and deferred-row decisions use the corrected paths.

## Structural refactor — 2026-10-23

- Backend organized into feature Contracts/Services/Processing folders with matching namespaces; API switched to MVC Controllers with injected feature interfaces and standard authentication/authorization policies. Worker loop and shared DI were extracted from host setup.
- `dotnet build Meridian.VoteTracker.sln`: passed with zero warnings and errors (isolated WSL build and Docker test build).
- `MERIDIAN_TEST_ARGS=--quick docker compose -p meridian-structure -f docker-compose.tests.yml up --abort-on-container-exit --exit-code-from integration-tests`: passed with PostgreSQL 16 and actual HTTP API/Worker processes. Includes import/overlap/normalization, deferred decisions/reports, transaction rollback, stale positions, snapshot immutability/progress, publication races, role enforcement, Worker kill/restart and competing-owner rejection. Final scale check used 101 votes; import plus calculation took 1.5 seconds.
- Added live HTTP checks for anonymous metadata, missing required query values, malformed query types and omitted `expectedVersionId`. These exposed an absent `type` in the initial MVC validation response; using the framework ProblemDetailsFactory fixed it, and the final suite passed.
- Angular production build: passed. Generated-client/OpenAPI drift check: passed. Angular tests: 8/8 passed, including late contribution responses, separate form errors/busy state and overlapping requests within one store.
- Source comparison: 13 relocated backend implementation files retained executable bodies and SQL, excluding namespace/import changes, extracted records and interface declarations. Import transaction boundaries and migrations were preserved. `git diff --check` passed.
- Tests used a separate Compose project and disposable database, with no application volumes or ports. The full historical benchmark and Azure connectivity were not repeated for this structural change.
