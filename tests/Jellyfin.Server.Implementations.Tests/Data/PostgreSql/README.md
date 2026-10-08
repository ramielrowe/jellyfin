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

Do not use production credentials or point this test harness at a production server. Connection strings and credentials are not written to test output.
