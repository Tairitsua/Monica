using Monica.Framework.Seeder.Models;

namespace Monica.Framework.Seeder.Abstractions;

/// <summary>Runs explicitly selected seeders through the current host's native execution pipeline.</summary>
public interface ISeederExecutor
{
    /// <summary>
    /// Runs the selected seeder and its unsatisfied eligible prerequisites. Successful prerequisites are not repeated.
    /// The identity must be a full type name present in this host's discovered graph; disabled dependencies are rejected.
    /// </summary>
    /// <remarks>
    /// Only one batch may execute at a time, including automatic startup. Admission failures throw before work begins.
    /// Cancellation stops this batch cooperatively and retains its outcomes; it does not cancel the host.
    /// Each attempt owns a fresh scope and uses the normal execution pipeline, retry, and failure policies.
    /// </remarks>
    Task<SeederStateSnapshot> RunAsync(string seederTypeName, CancellationToken cancellationToken = default);
}
