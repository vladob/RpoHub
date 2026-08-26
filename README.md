# RpoHub

Foundation for importing, normalizing, verifying, and monitoring Slovak public-register data. The first source is RPO, but the model is intentionally source-neutral so RÚZ and Financial Administration data can be added later.

## Architecture

- `RpoHub.Domain` — subjects, identifiers, source records, and data-quality concepts.
- `RpoHub.Application` — use-case contracts and orchestration.
- `RpoHub.Infrastructure` — official RPO HTTP clients and SQL Server persistence.
- `RpoHub.Web` — small ASP.NET Core operational UI/API.
- `RpoHub.Worker` — scheduled discovery/import worker.
- `database` — idempotent SQL Server foundation.

Raw source JSON is retained unchanged in `raw.SourceRecord`. Normalization is a separate, repeatable step. An IČO, DIČ, or IČ DPH is an attributed identifier, not the database primary key. Conflicting identifiers are recorded as data-quality observations and do not silently merge subjects.

The initialization export is treated as a package: a dated manifest plus all consecutively numbered `.json.gz` parts. `GET /api/import/initialization` previews and validates available packages without downloading or changing database state.

After running `database/002_AddInitializationPlanning.sql` on an existing database, `POST /api/import/initialization/{snapshotDate}/start` validates the manifest and atomically records the initialization batch and its files. It does not download the large JSON parts.

After running `database/003_AddImportFileExecutionState.sql`, `POST /api/import/initialization/{batchId}/import-next` claims the smallest pending JSON part, streams its `results` array through GZip, and writes idempotent 2,000-row batches into raw staging.

`database/004_ConvertRawJsonToUtf8.sql` converts raw JSON from UTF-16 `nvarchar(max)` to UTF-8 `varchar(max)`, validates every JSON value, and confirms that stored SHA-256 content hashes remain unchanged.

The Worker automatically resumes a `Started` initialization batch, importing one file at a time. A database application lock plus the persisted `Importing` status prevents concurrent Web and Worker claims.

After running `database/005_AddRpoCoreNormalization.sql`, the Worker normalizes the completed initialization snapshot in bounded, restartable batches. The first normalization slice creates one subject per RPO source entity, records the source entity ID and all IČO validity periods, preserves every historical name in `registry.SubjectName`, and selects the current or latest name for `registry.Subject.DisplayName`. Registry changes and `raw.SourceRecord.NormalizedAtUtc` are committed atomically for each batch.

`database/006_BackfillInitialConflictObservations.sql` idempotently backfills conflicting-`ValidTo` data-quality observations for the first 75,020 records that were normalized before those rules were deployed during development.

## First run

1. Install the .NET 8 SDK and SQL Server.
2. Run `database/001_CreateFoundation.sql` against a new database (recommended name: `RegistersRpo2`).
3. Copy `src/RpoHub.Web/appsettings.Development.example.json` to `appsettings.Development.json` in both Web and Worker projects, and set the connection string.
4. From this directory run:

```powershell
dotnet restore
dotnet build RpoHub.sln
dotnet run --project .\src\RpoHub.Web
```

In a second console:

```powershell
dotnet run --project .\src\RpoHub.Worker
```

The default official operational API base URL is configurable and currently set to `https://api.statistics.sk/rpo/v1/`. The official batch object-store URL is also configurable; no endpoint is embedded in business logic.

## Safe initialization rule

The worker never assumes that missing ETL metadata means an empty database. Initial import is permitted only when both the ETL state and raw source tables are empty, or when an operator explicitly starts a new initialization batch. This prevents an accidental reload over existing data.

## Next vertical slice

Normalize legal forms, establishment and termination dates, addresses, activities, and organization-unit relationships. The old `PgDumpImporter` project remains useful as a reference for sequential streaming, progress, cancellation, and bulk-copy patterns—not for its PostgreSQL COPY parser.
