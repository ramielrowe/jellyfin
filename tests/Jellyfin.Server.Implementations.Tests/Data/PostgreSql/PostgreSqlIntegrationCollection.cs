using Xunit;

namespace Jellyfin.Server.Implementations.Tests.Data.PostgreSql;

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class PostgreSqlIntegrationCollection : ICollectionFixture<PostgreSqlDatabaseFixture>
{
    public const string Name = "PostgreSQL integration";
}
