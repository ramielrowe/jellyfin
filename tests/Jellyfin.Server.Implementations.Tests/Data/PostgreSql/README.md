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

In another shell, run the tests:

```sh
export JELLYFIN_POSTGRES_TEST_CONNECTION_STRING='Host=127.0.0.1;Port=55432;Database=jellyfin_test_admin;Username=jellyfin_test_admin;Password=jellyfin-test-only'
dotnet test tests/Jellyfin.Server.Implementations.Tests/Jellyfin.Server.Implementations.Tests.csproj \
  --filter 'FullyQualifiedName~Data.PostgreSql.PostgreSqlIntegrationTests'
```

The configured role must be able to create databases. As a safety check, the database in the supplied connection string must contain `jellyfin_test` in its name. The fixture never modifies or drops that administrator database. It creates databases named `jellyfin_test_<random-guid>`, resets only those databases, and drops them after the run.

Do not use production credentials or point this test harness at a production server. Connection strings and credentials are not written to test output.
