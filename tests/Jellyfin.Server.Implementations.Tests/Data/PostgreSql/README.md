# PostgreSQL integration tests

These opt-in tests use a real PostgreSQL 16 server. Normal `dotnet test` runs do not contact PostgreSQL; the tests skip unless `JELLYFIN_POSTGRES_TEST_CONNECTION_STRING` is set.

Start the pinned local test server:

```sh
docker run --rm --name jellyfin-postgres-tests \
  -e POSTGRES_USER=jellyfin_test_admin \
  -e POSTGRES_PASSWORD=jellyfin-test-only \
  -e POSTGRES_DB=jellyfin_test_admin \
  -p 127.0.0.1:55432:5432 \
  postgres:16.6-alpine
```

The image creates `POSTGRES_USER` as a superuser. In another shell, create separate restricted harness and runtime roles.
Only the harness has `CREATEDB`, which it uses to isolate test runs. Jellyfin connects as the runtime role, which has
`NOCREATEDB` and owns each isolated database:

```sh
docker exec jellyfin-postgres-tests \
  psql -v ON_ERROR_STOP=1 -U jellyfin_test_admin -d jellyfin_test_admin \
  -c "CREATE ROLE jellyfin_test_runtime LOGIN PASSWORD 'jellyfin-runtime-test-only' NOSUPERUSER NOCREATEROLE NOCREATEDB" \
  -c "CREATE ROLE jellyfin_test_harness LOGIN PASSWORD 'jellyfin-harness-test-only' NOSUPERUSER NOCREATEROLE CREATEDB" \
  -c "GRANT jellyfin_test_runtime TO jellyfin_test_harness"
```

In another shell, run the tests:

```sh
export JELLYFIN_POSTGRES_TEST_CONNECTION_STRING='Host=127.0.0.1;Port=55432;Database=jellyfin_test_admin;Username=jellyfin_test_harness;Password=jellyfin-harness-test-only'
export JELLYFIN_POSTGRES_TEST_RUNTIME_CONNECTION_STRING='Host=127.0.0.1;Port=55432;Database=jellyfin_test_admin;Username=jellyfin_test_runtime;Password=jellyfin-runtime-test-only'
export JELLYFIN_POSTGRES_TEST_REQUIRED=true
dotnet test tests/Jellyfin.Server.Implementations.Tests/Jellyfin.Server.Implementations.Tests.csproj \
  --filter 'FullyQualifiedName~Data.PostgreSql'
```

The fixture verifies that the harness role is `NOSUPERUSER`, `NOCREATEROLE`, and `CREATEDB`, and independently verifies
that the Jellyfin runtime role is `NOSUPERUSER`, `NOCREATEROLE`, and `NOCREATEDB`. `CREATEDB` is strictly a test-harness
requirement. `JELLYFIN_POSTGRES_TEST_REQUIRED=true` turns either missing connection string into a test failure instead
of a skip, so CI cannot silently omit this suite. As a safety check, the database in the harness connection string must
contain `jellyfin_test` in its name. The fixture never modifies or drops that administrator database. It creates
databases named `jellyfin_test_<random-guid>` owned by the runtime role, resets non-system schemas only within those
databases, and drops them after the run.

PostgreSQL does not advertise Jellyfin fast-migration backup or full-system database backup/restore support. Use your
administrator's native PostgreSQL backup and restore procedure. Jellyfin rejects its logical database restore before
purging data because that path cannot currently reseed every imported identity sequence safely.

An existing PostgreSQL database with pending migrations stops before mutation unless its `database.xml` contains a
`CustomProviderOptions.Options` entry named `migration-backup-acknowledgement` whose value is the newest pending
migration id. Set that value only after creating and verifying an administrator-managed PostgreSQL backup for that
specific upgrade. A stale acknowledgement does not authorize a later upgrade, and Jellyfin cannot automatically
restore the external backup if a migration fails.

`PostgreSqlStartupLifecycleTests` builds the real `CoreAppHost` with the production host builder and runs the same
pre-initialisation, migration preparation, core migration, service initialisation, app migration, cleanup,
optimisation, and provider-shutdown entry points as server startup. It proves an empty pre-created database can start
and restart, upgrades a predecessor PostgreSQL migration state while preserving data read through production-resolved
services, and exercises credential, connectivity, schema-permission, and incompatible/future/gapped schema failures.
The exact backup acknowledgement authorizes and applies the pending migration; missing, stale, and wrong
acknowledgements are rejected without mutation. These scenarios are PostgreSQL-to-PostgreSQL only; a SQLite database or
backup is not an input to them and changing `DatabaseType` does not migrate SQLite data.

Do not use production credentials or point this test harness at a production server. Connection strings and credentials are not written to test output.

## Behavioral provider matrix

`ProviderBehaviorMatrixTests` is the shared SQLite/PostgreSQL persistence contract. Each test gets a clean schema and runs once per provider. It covers:

- `UserManager` policy/configuration persistence, normalized username uniqueness, permission/preference composite uniqueness, display-preference replacement, and optimistic concurrency;
- `BaseItemRepository` query construction, exact ascending/descending null-last ordering (including ties), adjacent pages with total counts, parent filtering, and literal `%`, `_`, and backslash matching with wildcard decoys;
- `PeopleRepository` credit deduplication and ordering plus complete people, item-value, and user-data relationship snapshots, including Unicode and nullable values;
- `MediaStreamRepository` and `ChapterRepository` save, ordered read, replacement, deletion, failed-replacement rollback, obsolete-row removal, and preservation of another item's rows;
- `ActivityManager` literal `LIKE`, date, exact ordering, adjacent paging, and total-count behavior; `AuthenticationManager` concurrent API-key creation/read/delete; and device-row round trips;
- `ItemPersistenceService` deletion of 2,050 ids with duplicate and missing inputs, user-data deduplication, multi-table cleanup, and preservation of unrelated rows;
- explicit transaction rollback, unique-constraint enforcement, and stale-write concurrency detection.

The matrix compares observable behavior, not SQL text or engine internals. PostgreSQL-specific schema, migration, index-planner, and lifecycle contracts remain in the other tests in this directory. PostgreSQL intentionally does not support Jellyfin's SQLite file-copy fast migration backup or full-system database backup/restore; those operations require administrator-managed PostgreSQL backup tooling.
