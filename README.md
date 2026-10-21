# Meridian Vote Tracker

An ASP.NET Core 8, PostgreSQL, Angular and Docker Compose implementation of the Meridian Vote Tracker assignment. The current foundation starts the API and background Worker as separate processes, applies versioned PostgreSQL SQL migrations, and seeds member states and membership periods from the supplied CSV.

## Prerequisites

- Docker Desktop with Docker Compose v2
- The .NET 8 SDK for local builds and tests
- The supplied assignment files in a sibling folder named `Assignment-DotNet` (the Compose seed mount points to `../Assignment-DotNet/member_states.csv`)

## Run locally

From this directory in PowerShell:

```powershell
Copy-Item .env.example .env
# Edit .env and replace the sample password with a local development password.
docker compose up --build
```

The PostgreSQL database uses a named persistent volume. Uploaded files will use a separate persistent volume. The API publishes at <http://localhost:8080>; health is at `/health`, metadata at `/api/v1/meta`, and the OpenAPI document at `/openapi/v1.json`.
Upload CSV through `POST /api/v1/imports` as multipart field `file`. The API responds with an import ID; use `GET /api/v1/imports/{id}` to poll durable progress and `GET /api/v1/imports` for history. The worker stages rows, validates row shape/fields/country membership, detects duplicates and within-file contradictions, compares against approved rows, promotes new values as versions, and creates pending conflicts for changed values. Use `GET /api/v1/imports/{id}/rows` to page through row-level results. Conflict decisions and UI remain upcoming.

Set `ConnectionStrings__Meridian` to use another PostgreSQL server, including Azure Database for PostgreSQL. The app does not contain connection strings or credentials. Local uploads are configured with `Storage__LocalPath`; Azure Blob and Application Insights can be enabled through provider configuration in a later implementation step.

## Project layout

- `src/Meridian.Api` — HTTP API and OpenAPI
- `src/Meridian.Worker` — independent background process
- `src/Meridian.Infrastructure` — PostgreSQL bootstrap and SQL migrations
- `docker-compose.yml` — PostgreSQL, API and Worker

Database migrations are applied at startup and recorded in `schema_migrations`. The initial migration enforces ISO alpha-3 state codes and non-overlapping, inclusive membership periods. Seed loading is repeatable.

## Current implementation status

The initial infrastructure and asynchronous CSV upload/staging are implemented, and the .NET solution compiles with zero warnings. Row validation/classification and promotion, conflict review, snapshots, and the Angular client are still pending. PostgreSQL startup/integration verification is pending because Docker and a PostgreSQL server are unavailable in this machine session.

## Assignment material

`DECISIONS.md` records domain and architecture choices. `AI_USAGE.md` describes AI assistance used in this implementation.


## PostgreSQL integration smoke test

With Compose running and the seed complete, set `MERIDIAN_TEST_CONNECTION_STRING` to the local PostgreSQL connection string and run:

```powershell
dotnet run --project tests/Meridian.PostgresSmoke
```

The smoke test connects to PostgreSQL, checks that migration and seed data are present, and verifies that PostgreSQL rejects overlapping membership periods. This test has not been run in the current environment because no PostgreSQL server or Docker CLI is available.


