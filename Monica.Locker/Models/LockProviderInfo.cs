namespace Monica.Locker.Models;

/// <summary>Describes the selected adapter without acquiring a lock or exposing connection settings.</summary>
/// <param name="Kind">The ownership contract implemented by the adapter.</param>
/// <param name="Backend">Optional native provider type or Dapr component name, safe for operational display.</param>
/// <param name="DefaultLeaseDuration">The fixed lease duration when the adapter supplies one.</param>
public sealed record LockProviderInfo(LockProviderKind Kind, string? Backend = null, TimeSpan? DefaultLeaseDuration = null);

/// <summary>Ownership contracts supported by the built-in critical-section lock adapters.</summary>
public enum LockProviderKind
{
    /// <summary>The host supplies the contract.</summary>
    Custom,
    /// <summary>One process, with release on disposal.</summary>
    InProcess,
    /// <summary>A Medallion backend manages the handle.</summary>
    Medallion,
    /// <summary>A Dapr component grants a fixed lease.</summary>
    Dapr
}
