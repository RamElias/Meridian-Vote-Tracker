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

The ownership lock intentionally supports **one active Worker per database**. Distributed workers, lease expiry/fencing under network partitions, production identity and cloud deployment are outside this implementation. The optional PDF workflow is documented below. The tests are targeted regression evidence, not proof that every possible input or failure is covered. Historical reports created by older buggy code are not rewritten automatically; new processing and deferred-row decisions use the corrected paths.

## Structural refactor — 2026-10-23

- Backend organized into feature Contracts/Services/Processing folders with matching namespaces; API switched to MVC Controllers with injected feature interfaces and standard authentication/authorization policies. Worker loop and shared DI were extracted from host setup.
- `dotnet build Meridian.VoteTracker.sln`: passed with zero warnings and errors (isolated WSL build and Docker test build).
- `MERIDIAN_TEST_ARGS=--quick docker compose -p meridian-structure -f docker-compose.tests.yml up --abort-on-container-exit --exit-code-from integration-tests`: passed with PostgreSQL 16 and actual HTTP API/Worker processes. Includes import/overlap/normalization, deferred decisions/reports, transaction rollback, stale positions, snapshot immutability/progress, publication races, role enforcement, Worker kill/restart and competing-owner rejection. Final scale check used 101 votes; import plus calculation took 1.5 seconds.
- Added live HTTP checks for anonymous metadata, missing required query values, malformed query types and omitted `expectedVersionId`. These exposed an absent `type` in the initial MVC validation response; using the framework ProblemDetailsFactory fixed it, and the final suite passed.
- Angular production build: passed. Generated-client/OpenAPI drift check: passed. Angular tests: 8/8 passed, including late contribution responses, separate form errors/busy state and overlapping requests within one store.
- Source comparison: 13 relocated backend implementation files retained executable bodies and SQL, excluding namespace/import changes, extracted records and interface declarations. Import transaction boundaries and migrations were preserved. `git diff --check` passed.
- Tests used a separate Compose project and disposable database, with no application volumes or ports. The full historical benchmark and Azure connectivity were not repeated for this structural change.

## Optional PDF workflow — 2026-10-23

- .NET solution build and Docker Release publishing for API/Worker: passed, with zero build warnings/errors in the solution build. Angular production build and generated-client drift check: passed. `docker compose config --quiet` and `docker compose up -d --build`: passed; the existing local database was preserved and migration 010 applied.
- Isolated PostgreSQL quick suite: passed including all existing core regressions and the new PDF checks. PDF service output is simulated; the HTTP transport check uses a local recording handler, not Azure. Tests cover: no promotion before human review, multi-table parsing, blank/low-confidence flags, missing publication time, totals mismatch, duplicate/unknown countries, exact ballots, saved drafts, stale/concurrent reviews, atomic import linkage, ordinary CSV promotion, corrected-PDF conflicts, duplicate-file history, source retention, rejection, safe failure, explicit retry, timeout, persisted-operation recovery and database immutability. Live API tests include PDF authentication, typed list/detail responses, stale decisions and PDF source retrieval.
- Angular: 11/11 tests passed. New tests render source-review warnings and approval acknowledgement, preserve edits after HTTP 422, and reject late responses from a previously selected PDF. Existing screen state and snapshot response-race tests still pass.
- Read-only browser inspection confirmed that the updated Data imports screen loads with the PDF upload/history section. No PDF was uploaded to the real Azure resource, no OCR quality measurements were made, and no synthetic test files were sent outside the isolated test workflow. Real endpoint/authorization and evaluation against the six supplied scans remain necessary.
- New C# feature files were formatted with Roslyn and checked for identical syntax tokens before/after formatting. Full-history CSV/snapshot benchmarks were not repeated; their earlier measurements remain recorded separately.

## PDF parsing and unified review correction — 2026-10-24

- Inspected persisted responses from the user's real Azure extraction without sending another Azure request. Merged member-name columns caused the first page to be skipped; checkbox-only empty rows were incorrectly interpreted as country identities. Parsing now respects column spans, removes checkbox tokens from identity fields, and calculates confidence from relevant identity/selected-mark spans.
- Reparsed two untouched AwaitingReview drafts using the stored analysis, with optimistic concurrency, an audit record, and protection against replacing saved human edits. No draft was approved and no ballots were promoted.
- MA_2011_042 now has 148 rows and matches printed totals: Y=70, N=55, A=7, X=16. MA_2007_015 has 149 extracted rows; ANT is unknown to the member seed and BEL/NIC have ambiguous extracted marks. These and the totals discrepancy still require human review. Publication timestamps remain explicit reviewer input.
- Review has one left queue with Conflicts/Extractions/Resolved tabs and a right detail panel. Field errors are displayed beside their fields without repeated global messages. Browser inspection confirmed the layout and extracted rows.
- Isolated PostgreSQL quick integration suite passed, including merged-column/empty-row parsing and safe reparse regressions. .NET build had zero warnings/errors. Angular tests: 12/12 passed. Generated-client drift check and Angular production build passed. Local API/Worker/Web containers were rebuilt with the existing application database preserved.

## Independent review corrections — 2026-10-25

- Corrected snapshot input capture: the Worker re-evaluates stored filters and captures metadata/positions/ballots from one repeatable-read view. Request-time records are provisional; failed/cancelled calculation attempts roll back and recapture on recovery. A regression changes vote date, ballot and position after enqueue, checks date-filter re-evaluation, cancels after capture and verifies both recovered consistency and prior completed-snapshot immutability.
- CSV parser now reports malformed quote syntax per logical record, preserves the malformed source, handles valid multiline fields and retains valid earlier records when the remaining quoted suffix is unterminated. A malformed header still fails the file because columns cannot safely be mapped. Promotion transaction boundaries remain unchanged.
- Import reports distinguish a ballot's independent promotion/no-change outcome from a final metadata-conflict classification and retain the conflict ID. Title-only/session-only changes are covered, including valid independent ballot additions. Migration 011 adds the outcome/link fields and stable snapshot-history index; old completed import reports are not rewritten.
- Import UI generations guard details, filters, append and close races; polling preserves loaded pages and suppresses overlapping refreshes. Snapshot history supports stable (created_at, id) ordering, paging and publication year/quarter/status filters. Tests discover a published snapshot beyond the first 200 rows and exercise the HTTP query contract.
- PDF and CSV share one timestamp validator requiring seconds and an explicit zone. PDF approval rejects minute-only timestamps before creating an import. Typed backend response records replace object read contracts; long SQL literals were formatted with an automated non-whitespace-content comparison. Angular strict and strictTemplates are enabled.
- Real PostgreSQL quick suite passed, including the new regressions, live HTTP/OpenAPI checks and actual Worker kill/restart. .NET build: zero warnings/errors. Angular: 20/20 tests passed; generated-client drift check and final Angular production build passed without warnings. Docker Release API/Worker publishing and Angular build passed; application containers were recreated with the original database/uploads preserved. Health and proxied snapshot publication filters responded successfully, and migration 011 was confirmed in the application database.
- Earlier iterations failed on an int/short DTO mismatch and a previously undocumented nullable snapshot progress total. Both were corrected. The process-kill test also exposed that PostgreSQL can finish a deliberate sleep before releasing a dead client's DDL lock; its cleanup command now allows that fault window rather than racing the default 30-second timeout.
- No Azure extraction requests were sent. Full historical-volume benchmarks were not repeated for these fixes.

- The stricter parser was also run against the actual supplied Q1 CSV: 5,896 records parsed, the malformed five-field row preserved, parsing resumed and quoted titles retained. Read-only browser inspection confirmed the new publication filters and opening an existing published snapshot. No decisions, positions, snapshots or publications were changed during this inspection.
- Final UI integration regression: a selected queued/running snapshot keeps polling even when publication filters hide it from the history list. Final Angular suite: 20/20 passed; generated-client check passed and the final Docker Angular production build passed. Temporary test containers/database infrastructure were removed; application services remain running.
