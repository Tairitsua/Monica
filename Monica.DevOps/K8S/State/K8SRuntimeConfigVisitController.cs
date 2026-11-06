using Monica.Core.Results;
using Monica.DevOps.K8S.Facades;
using Monica.DevOps.K8S.Models;

namespace Monica.DevOps.K8S.State;

/// <summary>Owns a detached credential-bearing editor draft only for the current routed visit.</summary>
internal sealed class K8SRuntimeConfigVisitController(K8SFacade facade, Func<Task> publish) : K8SVisitController(publish)
{
    private K8SRuntimeConfig? _baseline;
    internal K8SRuntimeConfig? Draft { get; private set; }
    internal string MasterAddresses { get; set; } = string.Empty;
    internal string WorkerAddresses { get; set; } = string.Empty;
    internal string NamespaceScope { get; set; } = string.Empty;
    internal string NameKeywords { get; set; } = string.Empty;
    internal bool ShowPassword { get; set; }
    internal string? Failure { get; private set; }
    internal bool ReloadFailed { get; private set; }
    internal bool Saved { get; private set; }
    internal bool IsDirty => Draft is not null && _baseline is not null && !Equivalent(BuildDraft(), _baseline);
    internal bool AllowsAllNamespaces => K8SRuntimeConfig.ParseMultiValue(NamespaceScope).Count == 0;

    internal Task LoadAsync() => RunAsync(async token =>
    {
        Saved = false;
        ReloadFailed = false;
        var response = await facade.GetRuntimeConfigAsync(token);
        token.ThrowIfCancellationRequested();
        var error = response;
        var config = response.Data;
        if (response.IsFailure() || config is null)
        {
            Failure = error.Message ?? string.Empty;
            ReloadFailed = Draft is not null;
            return;
        }
        Adopt(config);
    });

    internal Task SaveAsync() => RunAsync(async token =>
    {
        if (Draft is null) return;
        var draft = BuildDraft();
        var response = await facade.SaveRuntimeConfigAsync(draft, token);
        token.ThrowIfCancellationRequested();
        var error = response;
        if (response.IsFailure()) { Failure = error.Message ?? string.Empty; Saved = false; return; }
        Adopt(draft);
        Saved = true;
    });

    private K8SRuntimeConfig BuildDraft()
    {
        var config = Draft!.Clone();
        config.MasterNodes = K8SRuntimeConfig.ParseMultiValue(MasterAddresses);
        config.WorkerNodes = K8SRuntimeConfig.ParseMultiValue(WorkerAddresses);
        config.NamespaceScope = K8SRuntimeConfig.ParseMultiValue(NamespaceScope);
        config.SensitiveOperationNameRestrictions = K8SRuntimeConfig.ParseMultiValue(NameKeywords);
        return config.Normalize();
    }

    private void Adopt(K8SRuntimeConfig config)
    {
        _baseline = config.Clone().Normalize();
        Draft = _baseline.Clone();
        MasterAddresses = K8SRuntimeConfig.ToMultilineText(Draft.MasterNodes);
        WorkerAddresses = K8SRuntimeConfig.ToMultilineText(Draft.WorkerNodes);
        NamespaceScope = K8SRuntimeConfig.ToMultilineText(Draft.NamespaceScope);
        NameKeywords = K8SRuntimeConfig.ToMultilineText(Draft.SensitiveOperationNameRestrictions);
        ShowPassword = false;
        Failure = null;
        ReloadFailed = false;
    }

    private static bool Equivalent(K8SRuntimeConfig left, K8SRuntimeConfig right) =>
        left.Provider == right.Provider && left.ExecutionNode == right.ExecutionNode && left.UserName == right.UserName &&
        left.Password == right.Password && left.MasterNodes.SequenceEqual(right.MasterNodes) &&
        left.WorkerNodes.SequenceEqual(right.WorkerNodes) && left.NamespaceScope.SequenceEqual(right.NamespaceScope) &&
        left.SensitiveOperationNameRestrictions.SequenceEqual(right.SensitiveOperationNameRestrictions);

    protected override void RetireEvidence()
    {
        if (Draft is not null) Draft.Password = string.Empty;
        if (_baseline is not null) _baseline.Password = string.Empty;
        Draft = null;
        _baseline = null;
    }
}
