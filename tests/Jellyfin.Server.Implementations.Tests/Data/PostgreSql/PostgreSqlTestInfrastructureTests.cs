using System;
using System.Data.Common;
using Xunit;

namespace Jellyfin.Server.Implementations.Tests.Data.PostgreSql;

public sealed class PostgreSqlTestInfrastructureTests
{
    [Fact]
    public void CreateDatabaseName_UsesRequiredMarkerAndUniqueGuid()
    {
        var first = PostgreSqlTestDatabase.CreateDatabaseName();
        var second = PostgreSqlTestDatabase.CreateDatabaseName();

        Assert.StartsWith(PostgreSqlTestDatabase.DatabaseNamePrefix, first, StringComparison.Ordinal);
        Assert.True(PostgreSqlTestDatabase.IsOwnedDatabaseName(first));
        Assert.NotEqual(first, second);
    }

    [Theory]
    [InlineData("jellyfin")]
    [InlineData("postgres")]
    [InlineData("jellyfin_test_not-a-guid")]
    [InlineData("")]
    public void IsOwnedDatabaseName_RejectsUnownedNames(string databaseName)
    {
        Assert.False(PostgreSqlTestDatabase.IsOwnedDatabaseName(databaseName));
    }

    [Fact]
    public void ValidateAdministratorConnectionString_RejectsDatabaseWithoutSafetyMarker()
    {
        const string Password = "do-not-expose-this";

        var exception = Assert.Throws<InvalidOperationException>(
            () => PostgreSqlTestDatabase.ValidateAdministratorConnectionString(
                $"Host=localhost;Database=production;Username=postgres;Password={Password}"));

        Assert.Contains(PostgreSqlTestDatabase.RequiredAdministratorDatabaseMarker, exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(Password, exception.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void ValidateAdministratorConnectionString_MalformedValueDoesNotExposeCredential()
    {
        const string Password = "do-not-expose-this";

        var exception = Assert.Throws<InvalidOperationException>(
            () => PostgreSqlTestDatabase.ValidateAdministratorConnectionString(
                $"Host=localhost;Database=jellyfin_test_admin;Password={Password};Unknown Keyword=value"));

        Assert.DoesNotContain(Password, exception.ToString(), StringComparison.Ordinal);
        Assert.Contains(PostgreSqlDatabaseFixture.ConnectionStringEnvironmentVariable, exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ValidateRuntimeConnectionString_MalformedValueDoesNotExposeCredential()
    {
        const string Password = "do-not-expose-this";

        var exception = Assert.Throws<InvalidOperationException>(
            () => PostgreSqlTestDatabase.ValidateRuntimeConnectionString(
                $"Host=localhost;Database=jellyfin_test_admin;Password={Password};Unknown Keyword=value"));

        Assert.DoesNotContain(Password, exception.ToString(), StringComparison.Ordinal);
        Assert.Contains(PostgreSqlDatabaseFixture.RuntimeConnectionStringEnvironmentVariable, exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void RedactConnectionString_RemovesAllCredentialKeysAndValues()
    {
        const string Password = "do-not-expose-this";
        const string Passfile = "/private/pgpass";
        const string SslPassword = "do-not-expose-this-ssl-password";

        var sanitized = PostgreSqlTestDatabase.RedactConnectionString(
            $"Host=localhost;Database=jellyfin_test_admin;Username=postgres;Password={Password};Passfile={Passfile};SSL Password={SslPassword}");
        var sanitizedBuilder = new DbConnectionStringBuilder
        {
            ConnectionString = sanitized
        };

        Assert.DoesNotContain(Password, sanitized, StringComparison.Ordinal);
        Assert.DoesNotContain(Passfile, sanitized, StringComparison.Ordinal);
        Assert.DoesNotContain(SslPassword, sanitized, StringComparison.Ordinal);
        Assert.False(sanitizedBuilder.ContainsKey("Password"));
        Assert.False(sanitizedBuilder.ContainsKey("Passfile"));
        Assert.False(sanitizedBuilder.ContainsKey("SSL Password"));
        Assert.Equal("localhost", sanitizedBuilder["Host"]);
        Assert.Equal("postgres", sanitizedBuilder["Username"]);
    }

    [Fact]
    public void CreateCleanupFailureException_ReportsOnlySafeDatabaseIdentifier()
    {
        var databaseName = PostgreSqlTestDatabase.CreateDatabaseName();

        var exception = PostgreSqlTestDatabase.CreateCleanupFailureException(databaseName);

        Assert.Equal(
            $"Unable to remove isolated PostgreSQL test database '{databaseName}' after test setup failed. "
            + "Remove that database manually before rerunning the tests.",
            exception.Message);
        Assert.Null(exception.InnerException);
    }
}
