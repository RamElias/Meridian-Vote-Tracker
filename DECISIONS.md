# Meridian Vote Tracker — Decisions

**Status:** Core API/Worker flows and a four-area Angular UI for CSV imports, conflicts, positions, rankings, and publication are implemented and exercised against local PostgreSQL. Final edge-case checks remain. This file summarizes the choices relevant to the assignment.

## Architecture and data

- ASP.NET Core 8+, Angular, PostgreSQL, and Docker Compose. A modular monolith shares one solution and database, with API and Worker in separate processes. PostgreSQL persists jobs and progress; the single Compose Worker requeues interrupted work at startup (multi-worker deployments would need leases). Angular services/models are generated from `src/Meridian.Api/openapi.json` using `npm run generate:api` and consumed by the UI facade.
- PostgreSQL is the source of truth. Use direct Npgsql access and versioned SQL migrations: the initial schema is relational and small enough to keep SQL explicit and easy to explain. Approved values are immutable versions with a current pointer and audit records; full event sourcing is unnecessary.
- Local Compose uses PostgreSQL and persistent volumes. The connection string is configuration-driven to support Azure Database for PostgreSQL. Original uploads use persistent local storage first, with an Azure Blob provider as an optional configured deployment choice. Application Insights is enabled only when configured. No secrets are committed.
- No Cosmos DB or LLM in the core workflow. PDF extraction is optional after the CSV, conflict, and ranking paths; if implemented, use Azure Document Intelligence and route outputs through normal validation and human review.

## Imports and conflicts

- Stage CSV rows before validation and promotion. A file can have interleaved rows for the same vote, and partial acceptance must preserve valid rows when others fail. Persist row classifications and the final report.
- Hashes prevent reprocessing identical files while retaining upload history. Compare overlapping files with approved data; add new country votes, never interpret absence as deletion, and attach new proposals to an existing open conflict.
- `assembly_vote_id` identifies a vote. `(assembly_vote_id, country_code)` identifies a country ballot. Country identity is ISO 3166-1 alpha-3; a changed display name alone is not a conflict. Vote metadata conflicts and country-ballot conflicts are separate review items. Human decisions are audited; the first concurrent decision wins. Rows whose membership validity depends on a pending vote-date conflict wait for that decision.
- Membership intervals are inclusive, non-overlapping, and an empty end date means active membership. Seed country and membership data from the supplied `member_states.csv`.

## Scoring and publication

- Alignment includes approved `Y` and `N` ballots only. Matching ballots add their weight to the numerator; all eligible `Y/N` ballots add weight to the denominator. `A`, `X`, non-membership, and missing ballots are excluded. A zero denominator is N/A.
- Participation counts `Y/N/A` among approved ballots; `X` is absence. Coverage is approved country-vote records divided by selected votes during which the country was a member. Display one decimal place and preserve full calculation precision.
- Snapshots store exact selected votes, input/version references, rule version, results, denominators, and per-country/per-vote contributions. Later edits never change prior snapshots.
- An administrator explicitly chooses publication year and quarter. Publication period is distinct from vote date range and actual publication time. At most one snapshot is active per period; replacement/removal is audited and preserves the old snapshot. A concurrent publish that loses the unique-constraint race returns HTTP 409.

## Scope and trade-offs

Authentication is simplified for the assignment; API authorization remains required for roles. Production would add real identity, managed identity/Key Vault, deployment, backup/restore, security and load reviews, and retention policies. Before parallel development, agree on API contracts, generated client, formatting/analyzers, migration ownership, error format, UTC timestamps, tests, and review practices, then divide work into import, review/positions, Angular, and ranking/operations.
