# Meridian Vote Tracker

Meridian Vote Tracker helps a research institute manage Assembly voting data and publish country rankings based on its policy positions. Analysts can import CSV files, review corrections, resolve conflicting values, and trace each change to its source. Administrators set vote positions and weights and publish quarterly rankings. Each ranking is saved as a snapshot, so later corrections cannot change previously published results. The optional PDF workflow extracts proposed data from scanned voting sheets for human review.

**Technical overview:** ASP.NET Core 8 API and Worker, PostgreSQL 16, Angular, and Docker Compose. The API and Worker run as separate processes; PostgreSQL stores approved data, history, background jobs, and progress. The application runs locally, with optional Azure integrations configured through environment settings.

## 1. Run the application

**Prerequisite:** Docker Desktop running Linux containers, with Docker Compose. No local .NET SDK, Node.js, or WSL is required to run the application.

1. Place the supplied `member_states.csv` at `../Assignment-DotNet/member_states.csv`, relative to the repository:

   ```text
   parent/
     Meridian-Vote-Tracker/
     Assignment-DotNet/
       member_states.csv
   ```

2. Open PowerShell in the repository root and create the local settings file:

   ```powershell
   if (-not (Test-Path .env)) { Copy-Item .env.example .env }
   ```

3. Open `.env` and replace the sample `POSTGRES_PASSWORD` with a local password. Leave the Azure settings empty to run the core CSV workflow.

4. Start the application:

   ```powershell
   docker compose up -d --build
   ```

5. Once startup completes, open **[localhost:4200](http://localhost:4200)**.

The first build downloads dependencies. Startup applies migrations and loads countries, membership periods, and demo users automatically. Upload the vote CSV files through the application; they are not imported at startup.

### Addresses and demo users

| Service | Address |
|---|---|
| Angular application | [localhost:4200](http://localhost:4200) |
| API health | [localhost:8080/health](http://localhost:8080/health) |
| OpenAPI JSON | [localhost:8080/openapi/v1.json](http://localhost:8080/openapi/v1.json) |
| PostgreSQL | `localhost:5432`, database/user `meridian`, password from `.env` |

OpenAPI JSON is provided; there is no bundled Swagger UI. The frontend proxies `/api` requests through Nginx to the API.

Use the user switcher in the application:

| User | Role | Actions |
|---|---|---|
| Dana / Omer | Analyst | Upload, review conflicts/PDFs, create and inspect snapshots |
| Lana | Admin | Analyst actions plus editing positions/weights and publishing/withdrawing snapshots |

Direct API calls use `X-Demo-User: dana`, `omer`, or `lana`. This is demo identity; role permissions are enforced by the API.

## 2. Suggested review walkthrough

Start with a fresh application database for the complete sequence. Existing uploads and decisions remain in history across restarts.

1. **Data imports — original file.** As Dana, upload `assembly_export_2024_q1.csv`. Watch persisted progress, then open the history row to inspect the report and row classifications. Valid rows can be accepted even when other rows are invalid.
2. **Data imports — corrected file.** Upload `assembly_export_2024_q1_rev2.csv`. New records are added; changes to approved values become review items. Uploading identical content again records a duplicate without processing it again.
3. **Conflict review.** Use **Conflicts**, select an item on the left, and compare values and sources on the right. Accept a proposal or reject it with an optional note. **Resolved** retains the decision. A competing decision receives a conflict response instead of overwriting it.
4. **Vote positions.** Switch to Lana. Inspect a vote, its country ballots and timeline; assign a Yes/No position and weight from 1 to 5.
5. **Rankings & snapshots.** Create a snapshot using an explicit vote ID for which you just set a position. Larger selections require positions for every selected vote. All inputs are captured consistently when the Worker starts calculation; changes while queued are reflected at that capture point. Inspect the ranking and a country's contributions, then publish as Lana for an explicit year/quarter.
6. **Verify history.** Change a position or approve a later correction, then reopen the old snapshot: its stored inputs and results remain unchanged. A new snapshot uses the updated data.

For the historical dataset, upload `assembly_historic.csv` and allow the background job to finish. Progress survives page refresh. Vote filters and an explicit ID list are combined as an intersection when both are supplied.

## 3. Architecture at a glance

```text
Browser → Angular / Nginx (:4200) → ASP.NET Core API (:8080)
                                         ↓
                                    PostgreSQL
                                         ↑
                               Independent .NET Worker
API and Worker → shared persistent source-file storage
```

| Project | Responsibility |
|---|---|
| `src/Meridian.Api` | MVC controllers, authentication/authorization, correlation IDs, HTTP errors and OpenAPI |
| `src/Meridian.Worker` | Durable background import, extraction and calculation jobs |
| `src/Meridian.Infrastructure` | Feature services/processors, explicit Npgsql SQL, migrations and storage adapters |
| `src/Meridian.Web` | Angular feature pages/stores and generated API client |

PostgreSQL stores jobs, progress, approved versions, audit, and frozen snapshots. One active Worker owns the database's work queue. SQL migrations run at startup. Source files persist in a Compose volume by default. Errors use ProblemDetails and correlation IDs; full internal exceptions stay in JSON logs. See [ARCHITECTURE.md](ARCHITECTURE.md) for code navigation and [DECISIONS.md](DECISIONS.md) for trade-offs and scoring rules.

## 4. Run the tests

### Backend: real PostgreSQL, Docker only

From the repository root in PowerShell:

```powershell
$env:MERIDIAN_TEST_ARGS = '--quick'
docker compose -p meridian-tests -f docker-compose.tests.yml up --abort-on-container-exit --exit-code-from integration-tests
# After inspecting the result:
docker compose -p meridian-tests -f docker-compose.tests.yml down -v
```

Success ends with `PASS: integration suite` and `integration-tests ... exited with code 0`. The suite builds .NET inside Docker and uses a separate PostgreSQL service with no application volumes or published ports. It starts real API/Worker processes and tests imports, concurrency, authorization, snapshots, publication, recovery, and PDF review with simulated Azure responses. Some PostgreSQL errors are deliberately triggered to verify failure handling; the runner's exit code determines success.

The quick scale case uses 101 votes. For the larger 6,500-vote / 975,000-contribution case, set `$env:MERIDIAN_TEST_ARGS = '--large'` and repeat the same commands.

### Frontend and API client

Requires Node.js 24.15+ and npm. From the repository root:

```powershell
cd src/Meridian.Web
npm.cmd ci
npm.cmd test -- --watch=false
npm.cmd run check:api
npm.cmd run build
npm.cmd audit
```

`npm.cmd` works in PowerShell without enabling script execution; other shells can use `npm`. The client check regenerates into a temporary directory and detects drift from the checked-in OpenAPI contract. After an intentional contract change, use `npm.cmd run generate:api` and review the generated changes.

Latest local checks: **27 Angular tests passed**, PostgreSQL quick integration suite passed, client consistency and production build passed, and `npm audit` reported zero known vulnerabilities after the Vite override. Detailed evidence, historical volume measurements, and limits are in [VERIFICATION.md](VERIFICATION.md). Backend checks use executable regression runners (`dotnet run`), not a `dotnet test` framework.

## 5. Azure configuration and PDF bonus

The application runs locally in Docker, as required for this submission. Containerized API, Worker and Web services, together with configurable Azure PostgreSQL, Blob Storage, Key Vault and Application Insights integrations, provide a foundation for future Azure deployment. That deployment would still require choosing hosting services, configuring identities/network access and setting up a deployment pipeline; it has not been implemented or verified. Put credentials only in ignored `.env` or Key Vault. Never commit `.env`.

| Capability | Settings in `.env` | Default / access |
|---|---|---|
| External PostgreSQL | `MERIDIAN_CONNECTION_STRING` | Local Compose PostgreSQL; external connection needs database access and appropriate TLS settings |
| Key Vault configuration | `AZURE_KEY_VAULT_URI`, `AZURE_TENANT_ID`, `AZURE_CLIENT_ID`, `AZURE_CLIENT_SECRET` | Disabled; local service principal needs **Key Vault Secrets User** |
| Blob source storage | `STORAGE_PROVIDER=AzureBlob`, `AZURE_BLOB_SERVICE_URI`, `AZURE_BLOB_CONTAINER` | Persistent local volume; create the container and grant **Storage Blob Data Contributor** |
| Application Insights | `APPLICATIONINSIGHTS_CONNECTION_STRING` | Telemetry export disabled |
| PDF extraction | `DOCUMENT_INTELLIGENCE_ENDPOINT`, `DOCUMENT_INTELLIGENCE_API_KEY` | Disabled without endpoint; alternatively use the configured identity with **Cognitive Services User** |

Key Vault uses `DefaultAzureCredential`. For local Docker, the service-principal bootstrap credential must come from outside the vault. Secret names map `--` to `:`: `ConnectionStrings--Meridian`, `Storage--AzureBlob--ConnectionString`, `ApplicationInsights--ConnectionString`, `DocumentIntelligence--Endpoint`, and `DocumentIntelligence--ApiKey`. Vault values override corresponding local configuration. API and Worker must use the same settings. Recreate them with `docker compose up -d --build` after configuration changes. Changing the storage provider for existing history requires migrating the stored files.

### Try the PDF workflow

1. Configure the Document Intelligence endpoint and credential, then recreate the containers.
2. Upload a supplied scan in **Data imports**; follow it in **Conflict review → Extractions**.
3. Compare the original PDF with the editable draft. Correct flagged metadata/ballots and verify all four printed totals. Enter the source publication timestamp explicitly when absent from the scan; upload time is not substituted.
4. Save and approve after acknowledging source review. Approval queues the normal CSV validation/conflict flow; it does not bypass it. Rejected/accepted drafts remain available under **Resolved**.

The adapter uses `prebuilt-layout`, API version `2024-11-30`, without an LLM. Supported scope is the supplied Record of Vote layout, up to 20 MB, 20 returned pages, and 2,000 country rows. Blank marks propose absence with a warning; unknown or multiple marks require review. Confidence is a heuristic, not an accuracy guarantee.

Failed jobs can be retried; retries can incur another Azure charge. Restart resumes a persisted operation, but a crash between Azure acceptance and saving its operation URL can cause resubmission. **Re-read stored analysis** reuses an existing response without another Azure call and refuses to replace saved human edits.

## 6. Limits and operating notes

- Demo identity is not production authentication. Only one active Worker per database is supported.
- Real Document Intelligence responses were inspected for two supplied scans; extraction accuracy across all six scans has not been independently measured. Automated PDF tests simulate Azure responses. Live Key Vault, Blob, Application Insights ingestion and Azure PostgreSQL connectivity have not been verified against the supplied account.
- Before production, add real user identity, backup/restore and retention policies, and complete load/security validation. The existing containers and Azure configuration adapters provide a starting point for future Azure deployment, rather than a completed deployment setup.
- Stop the application with `docker compose down`; its named volumes remain. For startup problems, inspect `docker compose ps` and `docker compose logs --tail=100 api worker` before retrying. Do not share credentials from local logs/configuration.

[Design decisions](DECISIONS.md) · [AI usage](AI_USAGE.md) · [Architecture](ARCHITECTURE.md) · [Verification evidence](VERIFICATION.md)
