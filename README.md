# Meridian Vote Tracker

An ASP.NET Core 8, PostgreSQL, Angular and Docker Compose implementation of the Meridian Vote Tracker assignment. The current foundation starts the API and background Worker as separate processes, applies versioned PostgreSQL SQL migrations, and seeds member states and membership periods from the supplied CSV.

## Prerequisites

- Docker Desktop with Docker Compose v2
- The .NET 8 SDK for local builds and tests
- Node.js 24.15+ and npm for local Angular development (the Compose build uses Node 24)
- The supplied assignment files in a sibling folder named `Assignment-DotNet` (the Compose seed mount points to `../Assignment-DotNet/member_states.csv`)

## Run locally

From this directory in PowerShell:

```powershell
if (-not (Test-Path .env)) { Copy-Item .env.example .env }
# Edit .env and replace the sample password with a local development password.
docker compose up -d --build
```

The Angular app publishes at <http://localhost:4200> and proxies API requests to the backend at <http://localhost:8080>. Health is at `/health`, metadata at `/api/v1/meta`, and the OpenAPI document at `/openapi/v1.json`. Set `ConnectionStrings__Meridian` to use another PostgreSQL server, including Azure Database for PostgreSQL. The app does not contain connection strings or credentials. Local uploads are configured with `Storage__LocalPath`; Azure Blob and Application Insights can be enabled through provider configuration later.

The principal API flows are:

- **Imports:** `POST /api/v1/imports` with multipart field `file`; poll `GET /api/v1/imports/{id}` and page results with `GET /api/v1/imports/{id}/rows`. `GET /api/v1/imports` returns history. Rows are staged, validated, classified, and promoted independently where valid.
- **Conflicts:** `GET /api/v1/conflicts`; resolve with `POST /api/v1/conflicts/{id}/decision` using `Accept` plus `proposalIndex`, or `Reject`, and an optional note. Concurrent decisions use first-write-wins.
- **Votes and positions:** `GET /api/v1/votes` supports search/session/date filters. Vote IDs contain slashes, so details use `GET /api/v1/vote?voteId=...`; set a position with `PUT /api/v1/vote/position?voteId=...`. Position changes require the Admin demo user.
- **Snapshots:** `POST /api/v1/snapshots` accepts a session, date range, or explicit `voteIds`; poll `GET /api/v1/snapshots/{id}`, fetch results and per-country contributions, then publish or withdraw as Admin. A snapshot requires a position for every selected vote.

API v1 uses the seeded demo identities through `X-Demo-User` (`dana`, `omer`, or `lana`); `lana` is the Admin. This is assignment/demo authentication, not production identity.

## Project layout

- `src/Meridian.Api` — HTTP API and OpenAPI
- `src/Meridian.Worker` — independent background process
- `src/Meridian.Infrastructure` — PostgreSQL bootstrap and SQL migrations
- `src/Meridian.Web` — Angular dashboard and Nginx API proxy
- `docker-compose.yml` — PostgreSQL, API, Worker and Angular/Nginx Web

Database migrations are applied at startup and recorded in `schema_migrations`. The initial migration enforces ISO alpha-3 state codes and non-overlapping, inclusive membership periods. Seed loading is repeatable.

## Current implementation status

The API/Worker foundation, durable CSV upload and staging, validation/classification, immutable promotion versions, pending conflict creation and audited decisions, vote positions, ranking snapshots, contribution details, publication, demo-user attribution, OpenAPI contracts, and the Angular dashboard for the four core workflows are implemented. Docker Compose builds and runs the Web, API, Worker, and PostgreSQL services. PostgreSQL smoke and end-to-end import-to-snapshot checks have been run locally; the Angular build and unit tests also pass.

## Assignment material

`DECISIONS.md` records domain and architecture choices. `AI_USAGE.md` describes AI assistance used in this implementation.


## PostgreSQL integration smoke test

With Compose running and the seed complete, set `MERIDIAN_TEST_CONNECTION_STRING` to the local PostgreSQL connection string and run the real-PostgreSQL smoke test. In PowerShell, you can read the password from `.env` without printing it:

```powershell
$passwordLine = Get-Content .env | Where-Object { $_ -match '^POSTGRES_PASSWORD=' } | Select-Object -First 1
$password = $passwordLine -replace '^POSTGRES_PASSWORD=', ''
$env:MERIDIAN_TEST_CONNECTION_STRING = "Host=localhost;Port=5432;Database=meridian;Username=meridian;Password=$password"
dotnet run --project tests/Meridian.PostgresSmoke
```

The smoke test connects to PostgreSQL, checks that migration and seed data are present, and verifies that PostgreSQL rejects overlapping membership periods. It was run successfully in the development environment. Do not commit `.env` or connection strings.

## CSV parser smoke test

Run the parser check against the supplied Q1 file:

```powershell
dotnet run --project tests/Meridian.CsvSmoke -- ../Assignment-DotNet/assembly_export_2024_q1.csv
```

It checks quoted fields, the expected record count, and recovery after the malformed five-field row present in the supplied CSV. The importer must preserve and report that row while continuing with subsequent rows.

## Angular checks

For a local client-only build and unit tests:

```powershell
cd src/Meridian.Web
npm ci
npm run build
npm test -- --watch=false
```
