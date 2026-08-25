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

Implement streaming `.json.gz` download into `raw.SourceRecord`, using a bounded batch and `SqlBulkCopy`, then normalize names and identifiers. The old `PgDumpImporter` project remains useful as a reference for sequential streaming, progress, cancellation, and bulk-copy patterns—not for its PostgreSQL COPY parser.

