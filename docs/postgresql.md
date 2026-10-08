# PostgreSQL database provider

PostgreSQL support is experimental. SQLite remains Jellyfin's default database provider. The built-in PostgreSQL
provider is tested with PostgreSQL 16 (the integration environment is pinned to PostgreSQL 16.6); other PostgreSQL
major versions are not currently supported.

> [!WARNING]
> Selecting PostgreSQL does not migrate an existing SQLite library. Changing `DatabaseType` is only a provider
> selection, not a data migration. A `jellyfin.db` file, a SQLite fast-migration backup, or a Jellyfin full-system
> SQLite database backup cannot be restored into PostgreSQL. Only a fresh PostgreSQL database and upgrades from an
> existing supported Jellyfin PostgreSQL schema are supported. Keep the original SQLite server and its backups intact.

Jellyfin must be stopped while changing `database.xml`. Do not use these instructions to switch a server which has
already stored library or user data in SQLite.

## Provision the database

Create the database with a PostgreSQL administrator. The Jellyfin login does not need superuser, role-management, or
database-creation privileges. It needs `CONNECT` on its database and `USAGE` and `CREATE` on the `public` schema so
Entity Framework can create and upgrade Jellyfin's tables. The same login owns the objects it creates, which lets it
run Jellyfin's table maintenance.

The following `psql` session prompts for the login password instead of putting it in shell history. Replace the role
and database names if required:

```sql
CREATE ROLE jellyfin LOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE NOREPLICATION;
\password jellyfin
CREATE DATABASE jellyfin WITH ENCODING 'UTF8' TEMPLATE template0;
\connect jellyfin
REVOKE ALL ON DATABASE jellyfin FROM PUBLIC;
GRANT CONNECT ON DATABASE jellyfin TO jellyfin;
REVOKE CREATE ON SCHEMA public FROM PUBLIC;
GRANT USAGE, CREATE ON SCHEMA public TO jellyfin;
```

Do not pre-create tables, run the generated migration SQL by hand, or grant Jellyfin access to unrelated databases.
Jellyfin must receive an empty database for a fresh installation and will create its own schema objects at startup.

## Configure Jellyfin

Create or replace `database.xml` in Jellyfin's configuration directory (beside `system.xml`) while Jellyfin is stopped.
Use the exact provider key `Jellyfin-PgSql` and the required `NoLock` locking behavior:

```xml
<?xml version="1.0" encoding="utf-8"?>
<DatabaseConfigurationOptions>
  <DatabaseType>Jellyfin-PgSql</DatabaseType>
  <CustomProviderOptions>
    <PluginName />
    <PluginAssembly />
    <ConnectionString>Host={POSTGRES_HOST};Port=5432;Database=jellyfin;Username=jellyfin;Passfile={PGPASS_FILE};SSL Mode=VerifyFull;Root Certificate={CA_CERTIFICATE};Application Name=Jellyfin</ConnectionString>
    <Options>
      <CustomDatabaseOption>
        <Key>command-timeout</Key>
        <Value>30</Value>
      </CustomDatabaseOption>
    </Options>
  </CustomProviderOptions>
  <LockingBehavior>NoLock</LockingBehavior>
</DatabaseConfigurationOptions>
```

Replace every value in braces. Use the TLS mode and CA certificate required by the PostgreSQL deployment. For a local
test server without TLS, replace the two TLS parameters with `SSL Mode=Disable`; do not use that setting across an
untrusted network.

The connection string is parsed by Npgsql. `Timeout` controls connection establishment, while the
`command-timeout` custom option controls database command execution and defaults to 30 seconds. Npgsql pooling is
enabled by default; `Pooling`, `Minimum Pool Size`, `Maximum Pool Size`, `Connection Idle Lifetime`, and other Npgsql
connection-string options can be appended when the database administrator has selected values appropriate for the
server's connection budget. Each Jellyfin process has its own pool. Do not increase its maximum without accounting for
other clients and PostgreSQL's `max_connections`.

### Keep credentials out of configuration

Prefer Npgsql's `Passfile` connection option to embedding `Password` in `database.xml`. A passfile line has this form:

```text
{POSTGRES_HOST}:5432:jellyfin:jellyfin:{PASSWORD}
```

Mount or create that file using the host's secret-management mechanism, make it readable only by the Jellyfin service
account (mode `0600` on Unix-like systems), and protect `database.xml` with the same account-level permissions. Do not
commit either file, paste credentials into logs, or expose secrets through command-line arguments. If a deployment
cannot use a passfile, store the password in `database.xml` only after applying equivalent file permissions.

## First startup and upgrades

On the first start, Jellyfin validates the connection, confirms that the database is empty, and applies the PostgreSQL
baseline migration. Restarting the same version is safe. Authentication, connectivity, insufficient schema
permissions, a conflicting schema, an unknown future migration, or a gapped migration history stops startup without
silently selecting SQLite.

For a later PostgreSQL-to-PostgreSQL Jellyfin upgrade, Jellyfin stops before pending migrations until the operator has
made and verified an external PostgreSQL backup. The startup error reports the newest pending migration identifier.
After testing that the backup can be restored by the database administrator, add this entry inside the `Options`
element, using that exact identifier:

```xml
<CustomDatabaseOption>
  <Key>migration-backup-acknowledgement</Key>
  <Value>{NEWEST_PENDING_MIGRATION_ID}</Value>
</CustomDatabaseOption>
```

Restart Jellyfin to apply the migration, then remove the acknowledgement after a successful startup. An old or
incorrect value does not authorize a different migration. The acknowledgement records an operator decision only:
Jellyfin does not create, inspect, verify, or automatically restore the external backup.

Never set the acknowledgement to force an initial baseline over non-Jellyfin objects, an SQLite-derived schema, an
unknown migration, or an inconsistent migration history. Restore a compatible PostgreSQL backup or return to a
matching Jellyfin version instead.

## Backup and restore limitations

The PostgreSQL provider does not support Jellyfin's database-inclusive full-system backup or restore, nor its internal
fast migration backup and rollback. A Jellyfin backup created with database contents enabled is therefore rejected for
this provider. Backups which exclude database contents do not protect PostgreSQL data.

Use PostgreSQL-native, administrator-managed backup and restore procedures. Keep the database backup consistent with
any separately backed-up Jellyfin configuration, metadata, and media state, and verify restoration in an isolated
PostgreSQL environment before acknowledging an upgrade. Recovery remains an external PostgreSQL operation; Jellyfin
does not orchestrate it.

PostgreSQL-to-PostgreSQL application upgrades are supported through Jellyfin's migrations. This does not imply that
Jellyfin supports every PostgreSQL replication, high-availability, point-in-time recovery, or cross-major-version
strategy. SQLite-to-PostgreSQL, PostgreSQL-to-SQLite, and other cross-provider restores are unsupported.

## Maintenance and observation

Leave PostgreSQL autovacuum enabled. Jellyfin's scheduled database optimisation runs `VACUUM (ANALYZE)` on mapped
Jellyfin tables, and its statistics refresh runs `ANALYZE`; both are bounded by the configured command timeout. The
shutdown path performs no server-wide checkpoint or vacuum. Database-wide backup scheduling, retention, replication,
capacity planning, and PostgreSQL upgrades remain administrator responsibilities.

Jellyfin logs the configured host, port, and database, but not the connection string. Monitor normal PostgreSQL views
such as `pg_stat_activity`, `pg_stat_database`, `pg_stat_user_tables`, and lock views using a separate monitoring role.
Setting `Application Name=Jellyfin` in the connection string makes its sessions easier to identify. Watch connection
counts, pool exhaustion symptoms, long-running or blocked statements, dead tuples/autovacuum progress, database size,
and backup success.

Common startup failures have these remedies:

- Authentication: verify the login, passfile entry, host authentication rules, and secret-file permissions.
- Connectivity or timeout: verify DNS, host, port, TLS trust, firewall rules, and PostgreSQL availability.
- Permission denied: grant only `CONNECT` on the database and `USAGE, CREATE` on the target schema; make sure the
  Jellyfin role still owns the objects it created.
- Conflicting or incompatible schema: do not edit `__EFMigrationsHistory`; use an empty database for a new server or
  restore a compatible PostgreSQL backup.
- Pending migration: make and verify the external backup, then use the exact acknowledgement from the startup error.

For contributor migration and integration-test instructions, see
[`src/Jellyfin.Database/readme.md`](../src/Jellyfin.Database/readme.md) and the
[`PostgreSQL integration test guide`](../tests/Jellyfin.Server.Implementations.Tests/Data/PostgreSql/README.md).
