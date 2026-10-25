# Design decisions

## 1. Architecture and persistence

**One solution, separate API and Worker processes.** HTTP requests enqueue durable PostgreSQL jobs; the Worker performs imports, extraction and calculations. This keeps long work out of requests without introducing a broker. A database ownership lock allows one active Worker; multiple active Workers require leases/fencing. Throttled progress writes use separate short transactions so progress remains visible during processing.

**MVC controllers and feature services with explicit Npgsql SQL.** Grouped SQL fits interleaved imports and large calculations, and keeps transaction boundaries visible. Versioned SQL migrations and constraints enforce invariants. The shared Infrastructure project contains both use-case orchestration and persistence: a deliberate packaging compromise. Separate Application/Domain assemblies and a generic repository would add boundaries that this scope does not yet need. Angular uses a generated OpenAPI client and feature-specific state stores.

## 2. Approved data, imports and history

Stage the complete CSV before classification because a vote's rows may be scattered throughout the file. Preserve raw and normalized values, normalization changes and row outcomes. Trim surrounding whitespace and canonicalize numeric sessions; ballots and date formats remain strict. Valid rows survive other rows' failures. Promotion, classification, report and completion commit atomically; deferred date decisions refresh affected reports in their transaction.

Approved values are **immutable versions with a current pointer**, source attribution and audit. This supports historical explanations without full event sourcing. Database triggers protect versions, audit and completed snapshots. A file hash prevents identical content from being processed twice while preserving upload history. Missing rows never imply deletion.

Vote identity is `assembly_vote_id`; ballot identity is `(assembly_vote_id, country_code)`. ISO alpha-3 identifies a country, so a display-name change alone is not a conflict. Metadata and ballot conflicts are separate; new sources join an existing open item. First concurrent decision wins; stale decisions return 409. Position edits also require an expected version. Membership intervals are inclusive and cannot overlap; a blank end means active. Rows depending on a disputed vote date wait for resolution.

## 3. Scoring and publication

Only approved data is eligible. All metrics use the snapshot's selected votes and membership on each vote date.

| Rule | Decision |
|---|---|
| Alignment | `100 × matching Y/N weight ÷ eligible Y/N weight`; exclude A, X, non-membership and missing ballots |
| No eligible weight | N/A, never a fabricated zero |
| Participation | Y/N/A count divided by approved ballot count; X counts as absence |
| Coverage | Approved ballot count divided by selected votes during membership |
| Presentation | One decimal place; retain calculation precision |

A selection with missing positions fails explicitly; unknown explicit vote IDs are reported. Snapshots freeze selected votes, exact inputs/version references, rule version, results, denominators and per-country/per-vote contributions. Later changes cannot alter them. Results and completion commit together; recovery reconciles job acknowledgement.

An Admin chooses publication year/quarter separately from data range and publication time. At most one snapshot is active per period. Replacement/withdrawal preserves the old snapshot and audit; concurrent publication is serialized and conflicting writes return 409.

## 4. Azure and PDF extraction

Compose runs locally with PostgreSQL and persistent local source storage. External Azure PostgreSQL, Blob, optional Key Vault configuration and Application Insights are implemented behind settings; their live account integration is unverified. Local vault access needs an external bootstrap credential. No secrets are committed. The provided Document Intelligence resource has been exercised on supplied scans. Cosmos DB adds no needed capability to this relational model; Azure OpenAI/Claude are unnecessary for the core or structured table extraction.

The PDF bonus uses Document Intelligence `prebuilt-layout` without an LLM. Retain original PDFs, raw analysis, extracted/edited drafts and decisions. Validate identities, membership, duplicates, marks and totals. Blank marks propose X with a warning; confidence below 0.85 or unavailable prompts review. Human approval is mandatory and queues normal CSV validation atomically. Missing publication time requires reviewer input. Persist Azure operation URLs for recovery and bound execution time; retries can incur charges. Reparse stored analysis only when saved human edits will not be replaced.

**Quality evaluation:** independently label all six supplied scans, including the corrigendum. Measure metadata accuracy, country-row precision/recall, ballot accuracy, printed-total discrepancies, confidence calibration and correction time. Two real responses exposed parser issues and informed regression fixtures; this is not a measured accuracy benchmark.

## 5. Before production and the next team phase

Replace demo identity with real authentication; define backup/restore, retention and operational monitoring; conduct security/load reviews. Introduce leases/fencing before multiple active Workers. Keep SQL transaction ownership explicit when splitting application/persistence assemblies.

Before four developers work in parallel, agree on OpenAPI/client generation, migration ownership, ProblemDetails/correlation IDs, UTC timestamps, formatting and regression/review gates. Divide ownership: **(1)** imports/validation, **(2)** review/positions/audit, **(3)** Angular/generated client, **(4)** rankings/operations/integration tests. Review cross-feature contract and schema changes together.
