using System;

namespace Jellyfin.Database.Implementations;

/// <summary>
/// Describes the outcome of a database provider operation that can fail without throwing an exception.
/// </summary>
public readonly record struct DatabaseProviderOperationResult
{
    private DatabaseProviderOperationResult(bool succeeded, string? errorMessage)
    {
        Succeeded = succeeded;
        ErrorMessage = errorMessage;
    }

    /// <summary>
    /// Gets a value indicating whether the operation succeeded.
    /// </summary>
    public bool Succeeded { get; }

    /// <summary>
    /// Gets the actionable, secret-free error message when the operation failed.
    /// </summary>
    public string? ErrorMessage { get; }

    /// <summary>
    /// Creates a successful result.
    /// </summary>
    /// <returns>A successful operation result.</returns>
    public static DatabaseProviderOperationResult Success() => new(true, null);

    /// <summary>
    /// Creates a failed result.
    /// </summary>
    /// <param name="errorMessage">The actionable, secret-free error message.</param>
    /// <returns>A failed operation result.</returns>
    public static DatabaseProviderOperationResult Failure(string errorMessage)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(errorMessage);
        return new(false, errorMessage);
    }
}
