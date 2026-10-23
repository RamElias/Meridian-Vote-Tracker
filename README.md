# Meridian Vote Tracker

An ASP.NET Core 8, PostgreSQL, Angular and Docker Compose implementation of the Meridian Vote Tracker assignment. It supports durable imports, human conflict review, versioned positions and reproducible ranking snapshots. API and Worker run as separate processes, share feature services and apply versioned PostgreSQL migrations. Member states and membership periods are seeded from the supplied CSV.

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

The Angular app publishes at <http://localhost:4200> and proxies API requests to the backend at <http://localhost:8080>. Health is at `/health`, metadata at `/api/v1/meta`, and the OpenAPI document at `/openapi/v1.json`. Docker Compose runs the application locally. PostgreSQL and import-file storage default to local services; telemetry export is disabled until configured; Azure resources are optional external services selected by configuration.

## Azure Key Vault

The API and Worker optionally load configuration from Azure Key Vault when `AZURE_KEY_VAULT_URI` is set to the vault's HTTPS URI. Both processes use `DefaultAzureCredential`. For a workload that runs in Azure, use a system-assigned or user-assigned managed identity and grant it the **Key Vault Secrets User** role. For local Docker Compose, use a local development service principal with the required access and put its `AZURE_TENANT_ID`, `AZURE_CLIENT_ID` and `AZURE_CLIENT_SECRET` in the ignored `.env` file. The bootstrap credential must be supplied outside the vault because it is needed to open the vault. Leave the vault URI unset to use only local settings.

Store the PostgreSQL connection string as a Key Vault secret named `ConnectionStrings--Meridian`; the `--` separator maps to the .NET configuration key `ConnectionStrings:Meridian`. A Key Vault value overrides Compose's default local PostgreSQL connection. Alternatively, set `MERIDIAN_CONNECTION_STRING` in `.env` to use an external PostgreSQL server directly. For Azure Database for PostgreSQL, use its required TLS settings and grant the configured database user access to the schema. The local PostgreSQL container remains available for the default development setup.

To store original imports in Azure Blob, set `STORAGE_PROVIDER=AzureBlob`, `AZURE_BLOB_SERVICE_URI=https://<account>.blob.core.windows.net`, and (if needed) `AZURE_BLOB_CONTAINER`. Create the container first and grant the local service principal **Storage Blob Data Contributor** on it. Alternatively, store the storage connection string in Key Vault as `Storage--AzureBlob--ConnectionString`. API and Worker share the same provider configuration, so both can upload/read the same files. Keep `STORAGE_PROVIDER=Local` to use the persistent Compose volume. Keep this provider stable for a database's import history; changing it requires migrating stored files and their storage keys.

Set `APPLICATIONINSIGHTS_CONNECTION_STRING` to enable Application Insights telemetry for both API and Worker, or store it in Key Vault as `ApplicationInsights--ConnectionString`. When it is empty, telemetry export stays disabled. Resource credentials and connection strings must remain in ignored `.env` or Key Vault, never in source control.

The principal API flows are:

- **Imports:** `POST /api/v1/imports` with multipart field `file`; poll `GET /api/v1/imports/{id}` and page results with `GET /api/v1/imports/{id}/rows`. `GET /api/v1/imports` returns history. Rows are staged, validated, classified, and promoted independently where valid.
- **Conflicts:** `GET /api/v1/conflicts`; resolve with `POST /api/v1/conflicts/{id}/decision` using `Accept` plus `proposalIndex`, or `Reject`, and an optional note. Concurrent decisions use first-write-wins.
- **Votes and positions:** `GET /api/v1/votes` supports search/session/date filters. Vote IDs contain slashes, so details use `GET /api/v1/vote?voteId=...`; set a position with `PUT /api/v1/vote/position?voteId=...`. Position changes require the Admin demo user.
- **Snapshots:** `POST /api/v1/snapshots` accepts a session, date range, or explicit `voteIds`; poll `GET /api/v1/snapshots/{id}`, fetch results and per-country contributions, then publish or withdraw as Admin. A snapshot requires a position for every selected vote.

API v1 uses the seeded demo identities through `X-Demo-User` (`dana`, `omer`, or `lana`); `lana` is the Admin. This is assignment/demo authentication, not production identity.

## Project layout

- `src/Meridian.Api` — feature Controllers, authentication, request middleware and OpenAPI
- `src/Meridian.Worker` — independent background process
- `src/Meridian.Infrastructure` — feature contracts/services/processors, storage adapters, PostgreSQL bootstrap and SQL migrations
- `src/Meridian.Web` — Angular feature pages with separate state stores and Nginx API proxy
- `docker-compose.yml` — PostgreSQL, API, Worker and Angular/Nginx Web

See [ARCHITECTURE.md](ARCHITECTURE.md) for the folder tree, request flow, service responsibilities and design trade-offs.

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


## Repeatable PostgreSQL and HTTP regression suite

The suite creates a uniquely named temporary database, applies all migrations, runs the real processors and a real HTTP API, then drops only that temporary database. Its database account needs CREATEDB permission. It covers interleaved/overlapping/duplicate imports, normalization, membership boundaries, deferred-date reports, concurrent conflict decisions, stale positions, API roles, OpenAPI responses, snapshot metrics/immutability, concurrent publication and recovery after cancellation.

With Docker only (no host .NET SDK required):

```powershell
docker compose -p meridian-tests -f docker-compose.tests.yml up --abort-on-container-exit --exit-code-from integration-tests
docker compose -p meridian-tests -f docker-compose.tests.yml down -v
```

This uses an isolated PostgreSQL service with no published port or application volume. The default scale case calculates 975,000 contributions (6,500 votes × 150 countries). Set `MERIDIAN_TEST_ARGS=--quick` for the smaller 101-vote case.

With the .NET 8 SDK and `MERIDIAN_TEST_CONNECTION_STRING` pointing to a disposable PostgreSQL server:

```powershell
dotnet build Meridian.VoteTracker.sln
dotnet run --no-build --project tests/Meridian.IntegrationTests
dotnet run --no-build --project tests/Meridian.IntegrationTests -- --large
# Optional measurement using the original assignment files:
dotnet run --no-build --project tests/Meridian.IntegrationTests -- --historic ../Assignment-DotNet/assembly_historic.csv ../Assignment-DotNet/member_states.csv
```

The API contract is `src/Meridian.Api/openapi.json`. After changing it, run `npm run generate:api` and `npm run check:api` in `src/Meridian.Web`. The integration suite validates actual HTTP response shapes against this contract. Angular tests include late-response protection when switching contribution country or snapshot.

Progress is reported as Reading, Staging, Validating, Promoting or Calculating, with a phase counter and unit. Internal errors show a safe message and correlation ID; the exception and correlation scope remain in JSON console logs. For position writes, send `expectedVersionId` (null for the first position); stale versions receive HTTP 409 and `currentPosition`. Unknown explicit snapshot vote IDs receive HTTP 422 with `unknownVoteIds`.

See `VERIFICATION.md` for the performed checks and their limits. Azure provider code is implemented, but live Key Vault, Blob, telemetry ingestion and Azure PostgreSQL connectivity require environment credentials and have not been verified against the supplied Azure account.
