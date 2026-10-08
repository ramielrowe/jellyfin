using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Database.Implementations;
using Jellyfin.Database.Implementations.DbConfiguration;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Database.Implementations.Locking;
using Jellyfin.Database.Providers.Sqlite;
using Jellyfin.Server.Implementations.FullSystemBackup;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.SystemBackupService;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;
using BaseItemKind = Jellyfin.Data.Enums.BaseItemKind;

namespace Jellyfin.Server.Implementations.Tests.FullSystemBackup;

/// <summary>
/// Tests for <see cref="BackupService"/>, in particular that a single row of corrupt
/// <see cref="KeyframeData"/> (e.g. malformed <c>KeyframeTicks</c> JSON) does not abort
/// an otherwise healthy backup. See https://github.com/jellyfin/jellyfin/issues/17216.
/// </summary>
public sealed class BackupServiceTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<JellyfinDbContext> _dbOptions;
    private readonly string _testRoot;
    private readonly string _backupPath;
    private readonly string _configurationDirectoryPath;

    public BackupServiceTests()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();

        _dbOptions = new DbContextOptionsBuilder<JellyfinDbContext>()
            .UseSqlite(_connection)
            .Options;

        using (var ctx = CreateDbContext())
        {
            ctx.Database.EnsureCreated();
        }

        // Use the test assembly's own output directory instead of Path.GetTempPath(). On GitHub-hosted
        // windows-latest runners, the system temp directory lives on the constrained C: drive, which can have
        // less than the 5GiB BackupService requires free, causing spurious failures. AppContext.BaseDirectory
        // is under the repo checkout (the much larger D: drive on Windows runners) on all platforms.
        _testRoot = Path.Combine(AppContext.BaseDirectory, "jellyfin-backup-service-tests-" + Guid.NewGuid().ToString("N"));
        _backupPath = Path.Combine(_testRoot, "Backup");
        _configurationDirectoryPath = Path.Combine(_testRoot, "Config");
        Directory.CreateDirectory(_backupPath);
        Directory.CreateDirectory(_configurationDirectoryPath);
    }

    public void Dispose()
    {
        _connection.Dispose();

        if (Directory.Exists(_testRoot))
        {
            Directory.Delete(_testRoot, true);
        }
    }

    [Fact]
    public async Task CreateBackupAsync_WithCorruptKeyframeDataRow_SkipsRowAndCompletesBackup()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var validItemId = Guid.Parse("11111111-1111-1111-1111-111111111111");
        var corruptItemId = Guid.Parse("22222222-2222-2222-2222-222222222222");

        await using (var ctx = CreateDbContext())
        {
            // A healthy item + keyframe row, written the normal way.
            ctx.BaseItems.Add(CreateMovieEntity(validItemId, "Good Movie"));
            ctx.BaseItems.Add(CreateMovieEntity(corruptItemId, "Corrupt Movie"));
            await ctx.SaveChangesAsync(cancellationToken).ConfigureAwait(true);

            ctx.KeyframeData.Add(new KeyframeData
            {
                ItemId = validItemId,
                TotalDuration = 60_000,
                KeyframeTicks = [0, 1000, 2000]
            });
            await ctx.SaveChangesAsync(cancellationToken).ConfigureAwait(true);

            // Simulate a corrupted database row: truncated JSON array for KeyframeTicks,
            // written directly via SQL to bypass EF's normal (well-formed) write path.
            await ctx.Database.ExecuteSqlInterpolatedAsync(
                $"INSERT INTO KeyframeData (ItemId, TotalDuration, KeyframeTicks) VALUES ({corruptItemId.ToString()}, {5000L}, {"[1,2,3"})",
                cancellationToken).ConfigureAwait(true);
        }

        var backupService = CreateBackupService();

        var manifest = await backupService.CreateBackupAsync(new BackupOptionsDto()).ConfigureAwait(true);

        Assert.True(File.Exists(manifest.Path));

        using var archive = await ZipFile.OpenReadAsync(manifest.Path, cancellationToken).ConfigureAwait(true);
        var keyframeEntry = archive.GetEntry("Database/KeyframeData.json");
        Assert.NotNull(keyframeEntry);

        await using var entryStream = await keyframeEntry!.OpenAsync(cancellationToken).ConfigureAwait(true);
        using var document = await JsonDocument.ParseAsync(entryStream, cancellationToken: cancellationToken).ConfigureAwait(true);

        var rows = document.RootElement.EnumerateArray().ToList();

        // The corrupt row must be skipped, but the valid row must still make it into the backup.
        var singleRow = Assert.Single(rows);
        Assert.Equal(validItemId, singleRow.GetProperty("ItemId").GetGuid());
    }

    [Fact]
    public async Task CreateBackupAsync_RecordsDatabaseProviderIdentity()
    {
        var backupService = CreateBackupService();

        var manifest = await backupService.CreateBackupAsync(new BackupOptionsDto()).ConfigureAwait(true);

        Assert.Equal(DatabaseProviderKey.Sqlite, manifest.DatabaseProvider);
        using var archive = await ZipFile.OpenReadAsync(manifest.Path, TestContext.Current.CancellationToken).ConfigureAwait(true);
        var manifestEntry = archive.GetEntry("manifest.json");
        Assert.NotNull(manifestEntry);
        await using var manifestStream = await manifestEntry.OpenAsync(TestContext.Current.CancellationToken).ConfigureAwait(true);
        using var document = await JsonDocument.ParseAsync(manifestStream, cancellationToken: TestContext.Current.CancellationToken).ConfigureAwait(true);
        Assert.Equal(DatabaseProviderKey.Sqlite, document.RootElement.GetProperty("DatabaseProvider").GetString());
        Assert.Equal("0.3.0", document.RootElement.GetProperty("BackupEngineVersion").GetString());
        Assert.NotEqual("0.2.0", document.RootElement.GetProperty("BackupEngineVersion").GetString());
    }

    [Fact]
    public async Task CreateBackupAsync_WithoutDatabase_OmitsDatabaseWorkAndManifestContents()
    {
        var provider = CreateDatabaseProvider("Unsupported", DatabaseProviderCapabilities.None);

        var manifest = await CreateBackupService(provider.Object)
            .CreateBackupAsync(new BackupOptionsDto { Database = false })
            .ConfigureAwait(true);

        Assert.Null(manifest.DatabaseProvider);
        Assert.False(manifest.Options.Database);
        provider.Verify(p => p.RunScheduledOptimisation(It.IsAny<CancellationToken>()), Times.Never);
        using var archive = await ZipFile.OpenReadAsync(manifest.Path, TestContext.Current.CancellationToken).ConfigureAwait(true);
        Assert.DoesNotContain(archive.Entries, entry => entry.FullName.StartsWith("Database/", StringComparison.Ordinal));
        var manifestEntry = archive.GetEntry("manifest.json");
        Assert.NotNull(manifestEntry);
        await using var manifestStream = await manifestEntry.OpenAsync(TestContext.Current.CancellationToken).ConfigureAwait(true);
        using var document = await JsonDocument.ParseAsync(manifestStream, cancellationToken: TestContext.Current.CancellationToken).ConfigureAwait(true);
        Assert.False(document.RootElement.GetProperty("Options").GetProperty("Database").GetBoolean());
        Assert.Equal(JsonValueKind.Null, document.RootElement.GetProperty("DatabaseProvider").ValueKind);
        Assert.Empty(document.RootElement.GetProperty("DatabaseTables").EnumerateArray());
    }

    [Fact]
    public async Task CreateAndRestoreBackup_WithOldStyleProvider_PreservesLogicalBackupBehavior()
    {
        var provider = new LegacyDatabaseProvider();
        Assert.Equal(DatabaseProviderCapabilities.Unknown, ((IJellyfinDatabaseProvider)provider).Capabilities);
        var backupService = CreateBackupService(provider);

        var manifest = await backupService.CreateBackupAsync(new BackupOptionsDto()).ConfigureAwait(true);
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => backupService.RestoreBackupAsync(manifest.Path));

        Assert.Equal("purge reached", exception.Message);
        Assert.True(provider.PurgeCalled);
    }

    [Fact]
    public async Task CreateBackupAsync_WhenDatabaseBackupIsUnsupported_FailsBeforeOptimization()
    {
        var provider = CreateDatabaseProvider("Unsupported", DatabaseProviderCapabilities.None);
        var backupService = CreateBackupService(provider.Object);

        var exception = await Assert.ThrowsAsync<NotSupportedException>(
            () => backupService.CreateBackupAsync(new BackupOptionsDto()));

        Assert.Contains("Unsupported", exception.Message, StringComparison.Ordinal);
        Assert.Contains("does not support full-system database backup", exception.Message, StringComparison.Ordinal);
        provider.Verify(p => p.RunScheduledOptimisation(It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task RestoreBackupAsync_WithDifferentDatabaseProvider_RejectsBeforeRestore()
    {
        var manifest = await CreateBackupService().CreateBackupAsync(new BackupOptionsDto()).ConfigureAwait(true);
        var provider = CreateDatabaseProvider("Jellyfin-PgSql", DatabaseProviderCapabilities.FullSystemRestore);
        var backupService = CreateBackupService(provider.Object);

        var exception = await Assert.ThrowsAsync<NotSupportedException>(() => backupService.RestoreBackupAsync(manifest.Path));

        Assert.Contains(DatabaseProviderKey.Sqlite, exception.Message, StringComparison.Ordinal);
        Assert.Contains("Jellyfin-PgSql", exception.Message, StringComparison.Ordinal);
        Assert.Contains("Cross-provider database restore is not supported", exception.Message, StringComparison.Ordinal);
        provider.Verify(
            p => p.PurgeDatabase(It.IsAny<JellyfinDbContext>(), It.IsAny<System.Collections.Generic.IEnumerable<string>>()),
            Times.Never);
    }

    [Fact]
    public async Task RestoreBackupAsync_WhenDatabaseRestoreIsUnsupported_FailsBeforeRestore()
    {
        var manifest = await CreateBackupService().CreateBackupAsync(new BackupOptionsDto()).ConfigureAwait(true);
        var provider = CreateDatabaseProvider(DatabaseProviderKey.Sqlite, DatabaseProviderCapabilities.None);
        var backupService = CreateBackupService(provider.Object);

        var exception = await Assert.ThrowsAsync<NotSupportedException>(() => backupService.RestoreBackupAsync(manifest.Path));

        Assert.Contains("does not support full-system database restore", exception.Message, StringComparison.Ordinal);
        provider.Verify(
            p => p.PurgeDatabase(It.IsAny<JellyfinDbContext>(), It.IsAny<System.Collections.Generic.IEnumerable<string>>()),
            Times.Never);
    }

    [Fact]
    public async Task RestoreBackupAsync_LegacySqliteArchiveWithoutDatabaseConfiguration_ReachesPurge()
    {
        var manifest = await CreateBackupService().CreateBackupAsync(new BackupOptionsDto()).ConfigureAwait(true);
        await RewriteManifest(manifest.Path, new Version(0, 2, 0), null).ConfigureAwait(true);
        var provider = CreateDatabaseProvider(DatabaseProviderKey.Sqlite, DatabaseProviderCapabilities.FullSystemRestore);
        provider
            .Setup(p => p.PurgeDatabase(It.IsAny<JellyfinDbContext>(), It.IsAny<System.Collections.Generic.IEnumerable<string>>()))
            .ThrowsAsync(new InvalidOperationException("purge reached"));

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => CreateBackupService(provider.Object).RestoreBackupAsync(manifest.Path));

        Assert.Equal("purge reached", exception.Message);
        provider.Verify(
            p => p.PurgeDatabase(It.IsAny<JellyfinDbContext>(), It.IsAny<System.Collections.Generic.IEnumerable<string>>()),
            Times.Once);
    }

    [Fact]
    public async Task RestoreBackupAsync_LegacyPluginArchiveWithMatchingAssembly_ReachesPurgeWithoutNewCapabilities()
    {
        var manifest = await CreateBackupService().CreateBackupAsync(new BackupOptionsDto()).ConfigureAwait(true);
        await RewriteManifest(manifest.Path, new Version(0, 2, 0), null).ConfigureAwait(true);
        var provider = new LegacyDatabaseProvider();
        await AddPluginDatabaseConfiguration(
            manifest.Path,
            provider.GetType().Assembly.GetName().Name! + ".dll").ConfigureAwait(true);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => CreateBackupService(provider).RestoreBackupAsync(manifest.Path));

        Assert.Equal("purge reached", exception.Message);
        Assert.True(provider.PurgeCalled);
    }

    [Fact]
    public async Task RestoreBackupAsync_LegacyPluginArchiveWithAmbiguousIdentity_RejectsBeforePurge()
    {
        var manifest = await CreateBackupService().CreateBackupAsync(new BackupOptionsDto()).ConfigureAwait(true);
        await RewriteManifest(manifest.Path, new Version(0, 2, 0), null).ConfigureAwait(true);
        await AddPluginDatabaseConfiguration(manifest.Path, "Different.Database.Plugin").ConfigureAwait(true);
        var provider = CreateDatabaseProvider("Legacy.Plugin.Provider", DatabaseProviderCapabilities.None);

        var exception = await Assert.ThrowsAsync<NotSupportedException>(
            () => CreateBackupService(provider.Object).RestoreBackupAsync(manifest.Path));

        Assert.Contains("cannot be matched safely", exception.Message, StringComparison.Ordinal);
        provider.Verify(
            p => p.PurgeDatabase(It.IsAny<JellyfinDbContext>(), It.IsAny<System.Collections.Generic.IEnumerable<string>>()),
            Times.Never);
    }

    [Fact]
    public async Task RestoreBackupAsync_ProviderTaggedLegacyVersion_RejectsBeforePurge()
    {
        var manifest = await CreateBackupService().CreateBackupAsync(new BackupOptionsDto()).ConfigureAwait(true);
        await RewriteManifest(manifest.Path, new Version(0, 2, 0), DatabaseProviderKey.Sqlite).ConfigureAwait(true);
        var provider = CreateDatabaseProvider(DatabaseProviderKey.Sqlite, DatabaseProviderCapabilities.FullSystemRestore);

        var exception = await Assert.ThrowsAsync<NotSupportedException>(
            () => CreateBackupService(provider.Object).RestoreBackupAsync(manifest.Path));

        Assert.Contains("requires backup format version 0.3.0", exception.Message, StringComparison.Ordinal);
        provider.Verify(
            p => p.PurgeDatabase(It.IsAny<JellyfinDbContext>(), It.IsAny<System.Collections.Generic.IEnumerable<string>>()),
            Times.Never);
    }

    private BackupService CreateBackupService(IJellyfinDatabaseProvider? jellyfinDatabaseProvider = null)
    {
        var factory = new Mock<IDbContextFactory<JellyfinDbContext>>();
        factory.Setup(f => f.CreateDbContext()).Returns(CreateDbContext);
        factory.Setup(f => f.CreateDbContextAsync(It.IsAny<CancellationToken>())).ReturnsAsync(CreateDbContext);

        var applicationHost = new Mock<IServerApplicationHost>();
        applicationHost.Setup(a => a.ApplicationVersion).Returns(new Version(10, 11, 0));

        var applicationPaths = new Mock<IServerApplicationPaths>();
        applicationPaths.Setup(a => a.BackupPath).Returns(_backupPath);
        applicationPaths.Setup(a => a.ConfigurationDirectoryPath).Returns(_configurationDirectoryPath);
        applicationPaths.Setup(a => a.DataPath).Returns(Path.Combine(_testRoot, "Data"));
        applicationPaths.Setup(a => a.CachePath).Returns(Path.Combine(_testRoot, "Cache"));
        applicationPaths.Setup(a => a.ProgramDataPath).Returns(Path.Combine(_testRoot, "ProgramData"));
        applicationPaths.Setup(a => a.RootFolderPath).Returns(Path.Combine(_testRoot, "Root"));
        applicationPaths.Setup(a => a.InternalMetadataPath).Returns(Path.Combine(_testRoot, "Metadata"));
        applicationPaths.Setup(a => a.DefaultInternalMetadataPath).Returns(Path.Combine(_testRoot, "MetadataDefault"));

        jellyfinDatabaseProvider ??= CreateDatabaseProvider(DatabaseProviderKey.Sqlite, DatabaseProviderCapabilities.All).Object;

        var applicationLifetime = new Mock<IHostApplicationLifetime>();

        var libraryManager = new Mock<ILibraryManager>();
        libraryManager.Setup(l => l.IsScanRunning).Returns(false);

        return new BackupService(
            NullLogger<BackupService>.Instance,
            factory.Object,
            applicationHost.Object,
            applicationPaths.Object,
            jellyfinDatabaseProvider,
            applicationLifetime.Object,
            libraryManager.Object);
    }

    private static Mock<IJellyfinDatabaseProvider> CreateDatabaseProvider(
        string providerKey,
        DatabaseProviderCapabilities capabilities)
    {
        var provider = new Mock<IJellyfinDatabaseProvider>();
        provider.SetupGet(p => p.ProviderKey).Returns(providerKey);
        provider.SetupGet(p => p.Capabilities).Returns(capabilities);
        provider.Setup(p => p.RunScheduledOptimisation(It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        provider.Setup(p => p.PurgeDatabase(It.IsAny<JellyfinDbContext>(), It.IsAny<System.Collections.Generic.IEnumerable<string>>())).Returns(Task.CompletedTask);
        return provider;
    }

    private static async Task RewriteManifest(string archivePath, Version backupEngineVersion, string? databaseProvider)
    {
        using var archive = await ZipFile.OpenAsync(
            archivePath,
            ZipArchiveMode.Update,
            TestContext.Current.CancellationToken).ConfigureAwait(true);
        var manifestEntry = archive.GetEntry("manifest.json");
        Assert.NotNull(manifestEntry);

        JsonObject manifest;
        await using (var manifestStream = await manifestEntry.OpenAsync(TestContext.Current.CancellationToken).ConfigureAwait(true))
        {
            manifest = (JsonObject)(await JsonNode.ParseAsync(
                manifestStream,
                cancellationToken: TestContext.Current.CancellationToken).ConfigureAwait(true))!;
        }

        manifestEntry.Delete();
        manifest["BackupEngineVersion"] = backupEngineVersion.ToString();
        if (databaseProvider is null)
        {
            manifest.Remove("DatabaseProvider");
        }
        else
        {
            manifest["DatabaseProvider"] = databaseProvider;
        }

        var replacement = archive.CreateEntry("manifest.json");
        await using var replacementStream = await replacement.OpenAsync(TestContext.Current.CancellationToken).ConfigureAwait(true);
        await JsonSerializer.SerializeAsync(
            replacementStream,
            manifest,
            cancellationToken: TestContext.Current.CancellationToken).ConfigureAwait(true);
    }

    private static async Task AddPluginDatabaseConfiguration(string archivePath, string pluginAssembly)
    {
        using var archive = await ZipFile.OpenAsync(
            archivePath,
            ZipArchiveMode.Update,
            TestContext.Current.CancellationToken).ConfigureAwait(true);
        var configurationEntry = archive.CreateEntry("Config/database.xml");
        await using var configurationStream = await configurationEntry.OpenAsync(TestContext.Current.CancellationToken).ConfigureAwait(true);
        await using var writer = new StreamWriter(configurationStream);
        await writer.WriteAsync(
            ($"<DatabaseConfigurationOptions><DatabaseType>{DatabaseProviderKey.Plugin}</DatabaseType>"
            + $"<CustomProviderOptions><PluginAssembly>{pluginAssembly}</PluginAssembly></CustomProviderOptions>"
            + "</DatabaseConfigurationOptions>").AsMemory(),
            TestContext.Current.CancellationToken).ConfigureAwait(true);
    }

    private static BaseItemEntity CreateMovieEntity(Guid id, string name)
    {
        return new BaseItemEntity
        {
            Id = id,
            Type = "Movie",
            Name = name,
            PresentationUniqueKey = id.ToString("N"),
            MediaType = "Video",
            IsMovie = true,
            IsFolder = false,
            IsVirtualItem = false
        };
    }

    private JellyfinDbContext CreateDbContext()
    {
        return new JellyfinDbContext(
            _dbOptions,
            NullLogger<JellyfinDbContext>.Instance,
            new SqliteDatabaseProvider(null!, NullLogger<SqliteDatabaseProvider>.Instance),
            new NoLockBehavior(NullLogger<NoLockBehavior>.Instance));
    }

    private sealed class LegacyDatabaseProvider : IJellyfinDatabaseProvider
    {
        public bool PurgeCalled { get; private set; }

        public IDbContextFactory<JellyfinDbContext>? DbContextFactory { get; set; }

        public void Initialise(DbContextOptionsBuilder options, DatabaseConfigurationOptions databaseConfiguration)
        {
        }

        public void OnModelCreating(ModelBuilder modelBuilder)
        {
        }

        public void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
        {
        }

        public Task RunScheduledOptimisation(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task RunShutdownTask(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task<string> MigrationBackupFast(CancellationToken cancellationToken) => throw new NotImplementedException();

        public Task RestoreBackupFast(string key, CancellationToken cancellationToken) => throw new NotImplementedException();

        public Task DeleteBackup(string key) => throw new NotImplementedException();

        public Task PurgeDatabase(JellyfinDbContext dbContext, System.Collections.Generic.IEnumerable<string>? tableNames)
        {
            PurgeCalled = true;
            throw new InvalidOperationException("purge reached");
        }
    }
}
