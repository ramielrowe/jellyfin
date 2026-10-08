# How to run EF Core migrations

Jellyfin keeps an independent Entity Framework migration history for every built-in database provider. Provider-specific migrations live in that provider's project because they contain provider-specific SQL and metadata.

SQLite remains the default provider. PostgreSQL support is experimental.

When the shared model changes, create and review a migration for both providers. Run these commands from the repository root, replacing `MIGRATION_NAME` with the same descriptive name in both commands:

```sh
dotnet ef migrations add MIGRATION_NAME \
  --project src/Jellyfin.Database/Jellyfin.Database.Providers.Sqlite \
  --output-dir Migrations \
  -- --migration-provider Jellyfin-SQLite

dotnet ef migrations add MIGRATION_NAME \
  --project src/Jellyfin.Database/Jellyfin.Database.Providers.PostgreSql \
  --output-dir Migrations \
  -- --migration-provider Jellyfin-PgSql
```

Inspect both generated migrations instead of assuming the operations are interchangeable. In particular, check column types, identity behavior, generated columns, indexes and filters, foreign-key delete behavior, seed data, identifier lengths, and any raw SQL. Run the migration drift tests after generation:

```sh
dotnet test tests/Jellyfin.Server.Implementations.Tests/Jellyfin.Server.Implementations.Tests.csproj \
  --filter 'FullyQualifiedName~EfMigrations'
```

The PostgreSQL migration directory starts with a current-schema baseline. It deliberately does not replay the SQLite migration chain, share SQLite migration IDs, or transfer data from `jellyfin.db`. PostgreSQL databases must be pre-created; Jellyfin migrations own the schema objects inside the configured database, not server administration.

If `dotnet ef` is unavailable, run `dotnet tool restore`. Do not run restore or migration generation as root: root-owned `bin` or `obj` directories prevent normal builds later. Fix the directory ownership instead.
