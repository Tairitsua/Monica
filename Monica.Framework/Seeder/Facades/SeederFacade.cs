using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Monica.Core.Extensions;
using Monica.Core.Results;
using Monica.Framework.Seeder.Abstractions;
using Monica.Framework.Seeder.Models;
using Monica.Modules;

namespace Monica.Framework.Seeder.Facades;

/// <summary>
/// Exposes sanitized, current-process Seeder diagnostics to host and UI consumers.
/// </summary>
public sealed class SeederFacade(
    ISeederState state,
    IOptions<ModuleSeederOption> options,
    ILogger<SeederFacade> logger,
    ISeederExecutor executor)
{
    /// <summary>
    /// Explicitly runs one discovered seeder and its unsatisfied eligible prerequisites through the native executor.
    /// This can mutate external resources. Disabled dependencies, unknown identities, and overlapping batches are
    /// rejected before execution. Required failures continue to affect readiness until a successful explicit retry.
    /// </summary>
    public async Task<Res<SeederStateSnapshot>> RunAsync(string seederTypeName, CancellationToken cancellationToken = default)
    {
        try
        {
            return Res.Ok(await executor.RunAsync(seederTypeName, cancellationToken).ConfigureAwait(false));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Failed to execute Seeder {SeederTypeName}.", seederTypeName);
            return Res.Fail($"Failed to execute Seeder: {exception.GetMessageRecursively()}");
        }
    }

    /// <summary>
    /// Captures the effective module configuration and current host-owned Seeder run.
    /// </summary>
    /// <param name="cancellationToken">Cancels snapshot capture before it begins.</param>
    /// <returns>A successful result containing the diagnostics snapshot, or a failure result when capture fails.</returns>
    public Task<Res<SeederDiagnosticsSnapshot>> GetSnapshotAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var configured = options.Value;
            var snapshot = new SeederDiagnosticsSnapshot
            {
                Configuration = new SeederConfigurationSnapshot
                {
                    MaxConcurrency = configured.MaxConcurrency,
                    DefaultExecutionMode = configured.DefaultExecutionMode,
                    DefaultCriticality = configured.DefaultCriticality,
                    DefaultMaxAttempts = configured.DefaultMaxAttempts,
                    RetryBaseDelay = configured.RetryBaseDelay,
                    RetryMaxDelay = configured.RetryMaxDelay,
                    DefaultFailureBehavior = configured.DefaultFailureBehavior
                },
                Run = state.GetSnapshot()
            };
            return Task.FromResult<Res<SeederDiagnosticsSnapshot>>(Res.Ok(snapshot));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to capture Seeder diagnostics.");
            return Task.FromResult<Res<SeederDiagnosticsSnapshot>>(
                Res.Fail($"Failed to capture Seeder diagnostics: {ex.GetMessageRecursively()}"));
        }
    }
}
