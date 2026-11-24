using Microsoft.Extensions.Options;
using Monica.Modules;

namespace Monica.Locker.Services;

/// <summary>
/// Converts caller-facing resource names into provider-ready keys.
/// </summary>
public sealed class LockKeyNormalizer(IOptions<ModuleLockerOption> moduleOptions)
{
    private ModuleLockerOption ModuleOptions { get; } = moduleOptions.Value;

    /// <summary>Prepends the configured prefix verbatim, without contacting the provider.</summary>
    public string Normalize(string resourceName)
    {
        // Keep normalization intentionally simple so every provider observes the same stable key format.
        return string.Concat(ModuleOptions.LockKeyPrefix, resourceName);
    }
}
