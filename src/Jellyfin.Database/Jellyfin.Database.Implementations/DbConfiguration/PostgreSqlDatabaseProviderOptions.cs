namespace Jellyfin.Database.Implementations.DbConfiguration;

/// <summary>
/// Defines the provider options accepted by the built-in PostgreSQL provider.
/// </summary>
public static class PostgreSqlDatabaseProviderOptions
{
    /// <summary>
    /// The custom provider option containing the command timeout in seconds. Values must be positive integers.
    /// </summary>
    public const string CommandTimeout = "command-timeout";

    /// <summary>
    /// The smallest accepted command timeout in seconds.
    /// </summary>
    public const int MinimumCommandTimeoutSeconds = 1;

    /// <summary>
    /// The command timeout used when <see cref="CommandTimeout"/> is not configured.
    /// </summary>
    public const int DefaultCommandTimeoutSeconds = 30;
}
