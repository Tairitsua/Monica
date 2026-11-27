using Microsoft.Extensions.Options;
using Monica.Modules;
using Monica.UI.Shell.Support;

namespace Monica.DevOps.FileOps.Support;

internal sealed class FileOpsPolicyPageAccessPolicy(IOptions<ModuleFileOpsUIOption> options, IServiceProvider services) : IPageAccessPolicy
{
    public Task<bool> IsAuthorizedAsync(CancellationToken cancellationToken = default)
    {
        var policyType = options.Value.PolicyAccessPolicyType;
        if (policyType is null) return Task.FromResult(true);
        return services.GetService(policyType) is IPageAccessPolicy policy
            ? policy.IsAuthorizedAsync(cancellationToken) : Task.FromResult(false);
    }
}
