using Microsoft.Extensions.Options;
using Monica.Modules;
using Monica.UI.Shell.Support;

namespace Monica.JobScheduler.UI.UIJobScheduler.Shared;

/// <summary>
/// Re-evaluates the current Blazor circuit's operational authorization before scheduler data access or mutation.
/// </summary>
internal interface IJobSchedulerUiAccess
{
    /// <summary>
    /// Determines whether the current circuit may use the JobScheduler operational workspace.
    /// </summary>
    Task<bool> IsAuthorizedAsync(CancellationToken cancellationToken = default);

    /// <summary>Rechecks view access and any stricter policy for definition removal and owner retirement.</summary>
    Task<bool> IsDefinitionMaintenanceAuthorizedAsync(CancellationToken cancellationToken = default);
}

internal sealed class JobSchedulerUiAccess(
    OperationalPageAccessEvaluator evaluator,
    IOptions<ModuleJobSchedulerUIOption> options) : IJobSchedulerUiAccess
{
    /// <inheritdoc />
    public Task<bool> IsAuthorizedAsync(CancellationToken cancellationToken = default) =>
        evaluator.IsAuthorizedAsync(options.Value.AuthorizationPolicyOverride, cancellationToken);

    /// <inheritdoc />
    public async Task<bool> IsDefinitionMaintenanceAuthorizedAsync(CancellationToken cancellationToken = default) =>
        await IsAuthorizedAsync(cancellationToken)
        && (string.IsNullOrWhiteSpace(options.Value.DefinitionMaintenanceAuthorizationPolicy)
            || await evaluator.IsAuthorizedAsync(options.Value.DefinitionMaintenanceAuthorizationPolicy, cancellationToken));
}
