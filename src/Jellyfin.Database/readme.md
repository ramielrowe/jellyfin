# How to run EF Core migrations

Jellyfin keeps an independent Entity Framework migration history for every built-in database provider.
Provider-specific migrations live in that provider's project because they contain provider-specific SQL and metadata.

SQLite remains the default provider. PostgreSQL support is experimental.

Every shared model change must produce migrations for both built-in providers in the same change. A SQLite-only or
PostgreSQL-only model migration is incomplete. Create and review both migrations from the repository root, replacing
`MIGRATION_NAME` with the same descriptive name in both commands:

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

Inspect both generated migrations instead of assuming the operations are interchangeable. In particular, check column
types, identity behavior, generated columns, indexes and filters, foreign-key delete behavior, seed data, identifier
lengths, and any raw SQL. Never copy a migration or snapshot between provider projects. Run both migration drift tests
after generation:

```sh
dotnet test tests/Jellyfin.Server.Implementations.Tests/Jellyfin.Server.Implementations.Tests.csproj \
  --filter 'FullyQualifiedName~EfMigrations.EfMigrationTests'
```

The test command must report both `CheckForUnappliedMigrations_SqLite` and
`CheckForUnappliedMigrations_PostgreSql` as passing. The PostgreSQL migration directory starts with a current-schema
baseline. It deliberately does not replay the SQLite migration chain, share SQLite migration IDs, or transfer data
from `jellyfin.db`. PostgreSQL databases must be pre-created; Jellyfin migrations own the schema objects inside the
configured database, not server administration.

The operator-facing PostgreSQL setup, upgrade, and backup contract is documented in
[`docs/postgresql.md`](../../docs/postgresql.md). Keep that guide synchronized with provider keys, custom option keys,
capabilities, and tested PostgreSQL versions when any of those contracts change.

If `dotnet ef` is unavailable, run `dotnet tool restore`. Do not run restore or migration generation as root: root-owned
`bin` or `obj` directories prevent normal builds later. Fix the directory ownership instead.
