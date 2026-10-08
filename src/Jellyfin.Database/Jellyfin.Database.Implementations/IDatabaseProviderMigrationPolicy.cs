using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Jellyfin.Database.Implementations;

/// <summary>
/// Defines provider-specific safety policy used by the server migration host.
/// </summary>
public interface IDatabaseProviderMigrationPolicy
{
    /// <summary>
    /// Validates that the provider migration rows already recorded by a database describe a supported state.
    /// </summary>
    /// <param name="appliedMigrationIds">All migration identifiers recorded in the provider history table.</param>
    /// <param name="knownProviderMigrationIds">The provider migrations known to this Jellyfin build, in application order.</param>
    /// <param name="knownCodeMigrationIds">The non-provider code migrations known to this Jellyfin build.</param>
    /// <returns>The validation result.</returns>
    DatabaseProviderOperationResult ValidateAppliedMigrationHistory(
        IReadOnlyCollection<string> appliedMigrationIds,
        IReadOnlyList<string> knownProviderMigrationIds,
        IReadOnlyCollection<string> knownCodeMigrationIds)
        => DatabaseProviderOperationResult.Success();

    /// <summary>
    /// Verifies that a database contains no application objects and can receive an initial provider migration
    /// without first protecting existing application data.
    /// </summary>
    /// <param name="dbContext">The database context connected to the target database.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns><see langword="true"/> only when the provider can prove the database is empty.</returns>
    Task<bool> IsDatabaseEmptyForInitialMigrationAsync(
        JellyfinDbContext dbContext,
        CancellationToken cancellationToken);

    /// <summary>
    /// Validates the operator's provider-specific acknowledgement that an external backup was made before the
    /// supplied pending migrations. This does not create or verify the external backup.
    /// </summary>
    /// <param name="pendingMigrationIds">The migrations which require database protection.</param>
    /// <returns>The validation result.</returns>
    DatabaseProviderOperationResult ValidateExternalMigrationBackupAcknowledgement(
        IReadOnlyCollection<string> pendingMigrationIds);

    /// <summary>
    /// Converts a provider exception into stable, credential-safe startup diagnostics when possible.
    /// </summary>
    /// <param name="exception">The exception raised by the database stack.</param>
    /// <returns>A translated exception, or <see langword="null"/> when the exception is not recognized.</returns>
    DatabaseProviderStartupException? TranslateStartupException(Exception exception);
}
