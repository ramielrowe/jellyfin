using System;
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
    public void RedactConnectionString_RemovesPasswordAndPassfile()
    {
        const string Password = "do-not-expose-this";
        const string Passfile = "/private/pgpass";

        var sanitized = PostgreSqlTestDatabase.RedactConnectionString(
            $"Host=localhost;Database=jellyfin_test_admin;Username=postgres;Password={Password};Passfile={Passfile}");

        Assert.DoesNotContain(Password, sanitized, StringComparison.Ordinal);
        Assert.DoesNotContain(Passfile, sanitized, StringComparison.Ordinal);
        Assert.DoesNotContain("Password", sanitized, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Passfile", sanitized, StringComparison.OrdinalIgnoreCase);
    }
}
