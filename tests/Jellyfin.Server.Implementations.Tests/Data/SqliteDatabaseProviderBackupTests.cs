using System;
using System.IO;
using System.Threading.Tasks;
using Jellyfin.Database.Providers.Sqlite;
using MediaBrowser.Common.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Jellyfin.Server.Implementations.Tests.Data;

public sealed class SqliteDatabaseProviderBackupTests : IDisposable
{
    private readonly string _dataPath = Path.Combine(Path.GetTempPath(), "jellyfin-sqlite-backup-tests", Guid.NewGuid().ToString("N"));
    private readonly SqliteDatabaseProvider _provider;

    public SqliteDatabaseProviderBackupTests()
    {
        var applicationPaths = new Mock<IApplicationPaths>();
        applicationPaths.SetupGet(p => p.DataPath).Returns(_dataPath);
        _provider = new SqliteDatabaseProvider(applicationPaths.Object, NullLogger<SqliteDatabaseProvider>.Instance);
    }

    [Fact]
    public async Task TryRestoreBackupFast_WhenBackupDoesNotExist_ReturnsFailure()
    {
        var result = await _provider.TryRestoreBackupFast("missing", TestContext.Current.CancellationToken);

        Assert.False(result.Succeeded);
        Assert.Contains("missing", result.ErrorMessage, StringComparison.Ordinal);
        Assert.Contains("does not exist", result.ErrorMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TryDeleteBackup_WhenBackupDoesNotExist_ReturnsFailure()
    {
        var result = await _provider.TryDeleteBackup("missing");

        Assert.False(result.Succeeded);
        Assert.Contains("missing", result.ErrorMessage, StringComparison.Ordinal);
        Assert.Contains("does not exist", result.ErrorMessage, StringComparison.Ordinal);
    }

    public void Dispose()
    {
        if (Directory.Exists(_dataPath))
        {
            Directory.Delete(_dataPath, true);
        }
    }
}
