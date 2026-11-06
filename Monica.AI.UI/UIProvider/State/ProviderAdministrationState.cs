using Microsoft.Extensions.Localization;
using Monica.AI.Configuration.Facades;
using Monica.AI.Configuration.Models;
using Monica.AI.Providers;
using Monica.AI.UI.Localization;
using Monica.AI.UI.Shared.Support;
using Monica.AI.UI.UIProvider.Components;
using Monica.Core.Results;

namespace Monica.AI.UI.UIProvider.State;

/// <summary>
/// Component-owned provider evidence and revisioned operations. Facades and localizer are borrowed.
/// </summary>
internal sealed class ProviderAdministrationState(
    AIConfigurationFacade configuration,
    IStringLocalizer<AIResource> localizer) : IAsyncDisposable
{
    private readonly AIAdministrationOperationLifetime _lifetime = new();
    private long _selectionVersion;
    private string? _discoveryProviderId;
    private long _discoveryRevision;

    internal AIConfigurationSnapshot Snapshot { get; private set; } = new();
    internal bool HasLoaded { get; private set; }
    internal bool IsBusy { get; private set; }
    internal bool IsRetired => _lifetime.IsRetired;
    internal string? Error { get; private set; }
    internal string? SelectedProviderId { get; private set; }
    internal AIProviderSettings? SelectedProvider => Snapshot.Providers.FirstOrDefault(
        item => SameId(item.Configuration.ProviderId, SelectedProviderId));
    internal string ProviderQuery { get; private set; } = string.Empty;
    internal string ModelQuery { get; private set; } = string.Empty;
    internal string DiscoveryQuery { get; private set; } = string.Empty;
    internal EAIProviderType? ProviderTypeFilter { get; private set; }
    internal bool? EnabledFilter { get; private set; }
    internal AIModelKind? ModelKindFilter { get; private set; }
    internal bool HasDiscovered { get; private set; }
    internal string? DiscoveryError { get; private set; }
    internal IReadOnlyList<AIModelConfiguration> DiscoveredModels { get; private set; } = [];
    internal ProviderProbeOutcome? LastProbe { get; private set; }

    internal IReadOnlyList<AIProviderSettings> VisibleProviders => Snapshot.Providers.Where(item =>
        (ProviderTypeFilter is null || item.Configuration.ProviderType == ProviderTypeFilter)
        && (EnabledFilter is null || item.Configuration.Enabled == EnabledFilter)
        && (Matches(item.Configuration.ProviderId, ProviderQuery) || Matches(item.Configuration.DisplayName, ProviderQuery)
            || Matches(item.Configuration.BaseUrl, ProviderQuery)))
        .ToArray();
    internal IReadOnlyList<AIModelConfiguration> VisibleModels => (SelectedProvider?.Configuration.Models ?? [])
        .Where(model => (ModelKindFilter is null || model.Kind == ModelKindFilter)
            && (Matches(model.ModelName, ModelQuery) || Matches(model.DisplayName, ModelQuery)
                || Matches(model.Description, ModelQuery))).ToArray();
    internal IReadOnlyList<AIModelConfiguration> VisibleDiscoveredModels => DiscoveredModels
        .Where(model => Matches(model.ModelName, DiscoveryQuery)).ToArray();

    internal void SetProviderQuery(string? value) => _lifetime.TryApply(() => ProviderQuery = value ?? string.Empty);
    internal void SetModelQuery(string? value) => _lifetime.TryApply(() => ModelQuery = value ?? string.Empty);
    internal void SetDiscoveryQuery(string? value) => _lifetime.TryApply(() => DiscoveryQuery = value ?? string.Empty);
    internal void SetProviderTypeFilter(EAIProviderType? value) => _lifetime.TryApply(() => ProviderTypeFilter = value);
    internal void SetEnabledFilter(bool? value) => _lifetime.TryApply(() => EnabledFilter = value);
    internal void SetModelKindFilter(AIModelKind? value) => _lifetime.TryApply(() => ModelKindFilter = value);
    internal void ClearFilters() => _lifetime.TryApply(() =>
    {
        ProviderQuery = ModelQuery = DiscoveryQuery = string.Empty;
        ProviderTypeFilter = null;
        EnabledFilter = null;
        ModelKindFilter = null;
    });

    internal void SelectProvider(string id) => _lifetime.TryApply(() =>
    {
        if (SameId(id, SelectedProviderId)) return;
        SelectedProviderId = Snapshot.Providers.FirstOrDefault(item => SameId(item.Configuration.ProviderId, id))?.Configuration.ProviderId;
        _selectionVersion++;
        ModelQuery = string.Empty;
        ModelKindFilter = null;
        ClearDiscovery();
        LastProbe = null;
    });

    /// <summary>Captures the complete originating revision; callers must retain this value for the whole dialog.</summary>
    internal ProviderEditCapture CaptureProvider(AIProviderSettings? provider) => new(this, Snapshot.Revision, provider);
    internal ProviderEditCapture? CaptureSelectedProvider() => SelectedProvider is { } provider ? CaptureProvider(provider) : null;

    internal async Task<bool> RefreshAsync() => await RunOperationAsync(async token =>
    {
        var result = await configuration.GetAsync(token);
        token.ThrowIfCancellationRequested();
        var error = result.WithoutData();
        var snapshot = result.Data;
        if (result.IsFailure() || snapshot is null) return error.Message ?? GenericError;
        _lifetime.TryApply(() => Adopt(snapshot));
        return null;
    }) is null;

    internal Task<string?> SaveProviderAsync(ProviderEditCapture capture, ProviderConfigurationEdit edit, CancellationToken ct = default)
    {
        if (!Owns(capture) || capture.Provider is { } original && !SameId(original.Configuration.ProviderId, edit.Configuration.ProviderId))
            return Task.FromResult<string?>(GenericError);
        if (capture.Provider is null && Snapshot.Providers.Any(item => SameId(item.Configuration.ProviderId, edit.Configuration.ProviderId)))
            return Task.FromResult<string?>(GenericError);
        return RunOperationAsync(async token =>
        {
            var result = await configuration.UpsertProviderAsync(edit.Configuration, edit.ApiKey, edit.ClearApiKey, capture.Revision, token);
            token.ThrowIfCancellationRequested();
            var error = result.WithoutData();
            var snapshot = result.Data;
            if (result.IsFailure() || snapshot is null) return error.Message ?? GenericError;
            _lifetime.TryApply(() =>
            {
                Adopt(snapshot);
                if (capture.Provider is null) SelectedProviderId = edit.Configuration.ProviderId;
            });
            return _lifetime.IsRetired ? GenericError : null;
        }, ct);
    }

    internal Task<string?> SaveModelAsync(ProviderEditCapture capture, AIModelConfiguration? original, AIModelConfiguration saved, CancellationToken ct = default)
    {
        if (!Owns(capture) || capture.Provider is not { } provider) return Task.FromResult<string?>(GenericError);
        var models = provider.Configuration.Models;
        if (models.Any(model => !SameId(model.ModelName, original?.ModelName) && SameId(model.ModelName, saved.ModelName)))
            return Task.FromResult<string?>(localizer["Settings:DuplicateModelId"].Value);
        if (original is not null && !models.Contains(original)) return Task.FromResult<string?>(GenericError);
        var replacement = provider.Configuration with
        {
            Models = models.Where(model => !SameId(model.ModelName, original?.ModelName)).Append(saved).ToArray(),
            DefaultModel = original is not null && SameId(provider.Configuration.DefaultModel, original.ModelName)
                ? saved.Kind == AIModelKind.Chat ? saved.ModelName : null
                : provider.Configuration.DefaultModel
        };
        return SaveProviderAsync(capture, new(replacement, null, false), ct);
    }

    internal Task<string?> ToggleProviderAsync(ProviderEditCapture capture) => capture.Provider is { } provider
        ? SaveProviderAsync(capture, new(provider.Configuration with { Enabled = !provider.Configuration.Enabled }, null, false))
        : Task.FromResult<string?>(GenericError);

    internal Task<string?> SetDefaultModelAsync(ProviderEditCapture capture, AIModelConfiguration model)
    {
        if (capture.Provider is not { } provider || model.Kind != AIModelKind.Chat || !provider.Configuration.Models.Contains(model))
            return Task.FromResult<string?>(GenericError);
        return SaveProviderAsync(capture, new(provider.Configuration with { DefaultModel = model.ModelName }, null, false));
    }

    internal Task<string?> DeleteModelAsync(ProviderEditCapture capture, AIModelConfiguration model)
    {
        if (capture.Provider is not { } provider || !provider.Configuration.Models.Contains(model)) return Task.FromResult<string?>(GenericError);
        return SaveProviderAsync(capture, new(provider.Configuration with
        {
            Models = provider.Configuration.Models.Where(item => !SameId(item.ModelName, model.ModelName)).ToArray(),
            DefaultModel = SameId(provider.Configuration.DefaultModel, model.ModelName) ? null : provider.Configuration.DefaultModel
        }, null, false));
    }

    internal Task<string?> ImportModelAsync(ProviderEditCapture capture, AIModelConfiguration model)
    {
        if (!Owns(capture) || capture.Provider is null || !HasDiscovered || capture.Revision != _discoveryRevision
            || !SameId(capture.Provider.Configuration.ProviderId, _discoveryProviderId) || !DiscoveredModels.Contains(model))
            return Task.FromResult<string?>(GenericError);
        return SaveModelAsync(capture, null, model);
    }

    internal Task<string?> ResetProviderAsync(ProviderEditCapture capture) => MutateProviderAsync(capture, true);
    internal Task<string?> RemoveProviderAsync(ProviderEditCapture capture) => MutateProviderAsync(capture, false);

    private Task<string?> MutateProviderAsync(ProviderEditCapture capture, bool reset)
    {
        if (!Owns(capture) || capture.Provider is not { } provider || reset != provider.IsCodeDefined)
            return Task.FromResult<string?>(GenericError);
        return RunOperationAsync(async token =>
        {
            var result = reset
                ? await configuration.ResetProviderAsync(provider.Configuration.ProviderId, capture.Revision, token)
                : await configuration.RemoveProviderAsync(provider.Configuration.ProviderId, capture.Revision, token);
            token.ThrowIfCancellationRequested();
            var error = result.WithoutData();
            var snapshot = result.Data;
            if (result.IsFailure() || snapshot is null) return error.Message ?? GenericError;
            _lifetime.TryApply(() => Adopt(snapshot));
            return null;
        });
    }

    internal Task<string?> DiscoverAsync()
    {
        var capture = CaptureSelectedProvider();
        if (capture?.Provider is not { } provider) return Task.FromResult<string?>(GenericError);
        var selectionVersion = _selectionVersion;
        return RunOperationAsync(async token =>
        {
            _lifetime.TryApply(() => DiscoveryError = null);
            var result = await configuration.DiscoverModelsAsync(provider.Configuration.ProviderId, token);
            token.ThrowIfCancellationRequested();
            if (!MatchesSelection(capture, selectionVersion)) return null;
            var error = result.WithoutData();
            var models = result.Data;
            if (result.IsFailure() || models is null)
            {
                var diagnostic = error.Message ?? GenericError;
                _lifetime.TryApply(() => DiscoveryError = diagnostic);
                return diagnostic;
            }
            _lifetime.TryApply(() =>
            {
                HasDiscovered = true;
                DiscoveredModels = models;
                DiscoveryError = null;
                _discoveryProviderId = provider.Configuration.ProviderId;
                _discoveryRevision = capture.Revision;
            });
            return null;
        });
    }

    internal Task<string?> TestProviderAsync(ProviderEditCapture capture) => TestAsync(capture, null);
    internal Task<string?> TestModelAsync(ProviderEditCapture capture, AIModelConfiguration model) =>
        model.Kind == AIModelKind.Chat && capture.Provider?.Configuration.Models.Contains(model) == true
            ? TestAsync(capture, model.ModelName) : Task.FromResult<string?>(GenericError);

    private Task<string?> TestAsync(ProviderEditCapture capture, string? model)
    {
        if (!Owns(capture) || capture.Provider is not { } provider) return Task.FromResult<string?>(GenericError);
        var selectionVersion = _selectionVersion;
        return RunOperationAsync(async token =>
        {
            _lifetime.TryApply(() => LastProbe = null);
            Result result = model is null
                ? await configuration.TestProviderAsync(provider.Configuration.ProviderId, token)
                : await configuration.TestModelAsync(provider.Configuration.ProviderId, model, token);
            token.ThrowIfCancellationRequested();
            if (!MatchesSelection(capture, selectionVersion)) return null;
            var error = result;
            var diagnostic = result.IsFailure() ? error.Message ?? GenericError : null;
            _lifetime.TryApply(() => LastProbe = new(provider.Configuration.ProviderId, model, capture.Revision,
                model is not null, diagnostic is null, diagnostic));
            return diagnostic;
        });
    }

    private bool MatchesSelection(ProviderEditCapture capture, long version) => !IsRetired
        && _selectionVersion == version && Snapshot.Revision == capture.Revision
        && SameId(SelectedProviderId, capture.Provider?.Configuration.ProviderId);
    private bool Owns(ProviderEditCapture capture) => ReferenceEquals(capture.Owner, this) && !IsRetired;
    private string GenericError => localizer["Error:Generic"].Value;

    private async Task<string?> RunOperationAsync(Func<CancellationToken, Task<string?>> work, CancellationToken ct = default)
    {
        var admitted = false;
        _lifetime.TryApply(() =>
        {
            if (IsBusy) return;
            IsBusy = true;
            Error = null;
            admitted = true;
        });
        if (!admitted) return GenericError;
        try
        {
            return await _lifetime.RunAsync(async lifetimeToken =>
            {
                using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(lifetimeToken, ct);
                var diagnostic = await work(cancellation.Token);
                cancellation.Token.ThrowIfCancellationRequested();
                _lifetime.TryApply(() => Error = diagnostic);
                return diagnostic;
            });
        }
        catch (OperationCanceledException) when (_lifetime.Token.IsCancellationRequested || ct.IsCancellationRequested) { return GenericError; }
        catch (Exception) { _lifetime.TryApply(() => Error = GenericError); return GenericError; }
        finally { _lifetime.TryApply(() => IsBusy = false); }
    }

    private void Adopt(AIConfigurationSnapshot snapshot)
    {
        if (snapshot.Revision != Snapshot.Revision) { ClearDiscovery(); LastProbe = null; }
        Snapshot = snapshot;
        HasLoaded = true;
        if (SelectedProvider is null)
        {
            SelectedProviderId = snapshot.Providers.FirstOrDefault()?.Configuration.ProviderId;
            _selectionVersion++;
            ClearDiscovery();
            LastProbe = null;
        }
    }

    private void ClearDiscovery()
    {
        HasDiscovered = false;
        DiscoveredModels = [];
        DiscoveryQuery = string.Empty;
        DiscoveryError = null;
        _discoveryProviderId = null;
    }

    private static bool SameId(string? left, string? right) => left is not null && right is not null
        && string.Equals(left.Trim(), right.Trim(), StringComparison.OrdinalIgnoreCase);
    private static bool Matches(string? value, string query) => string.IsNullOrWhiteSpace(query)
        || value?.Contains(query.Trim(), StringComparison.OrdinalIgnoreCase) == true;
    public ValueTask DisposeAsync() => _lifetime.RetireAsync();
}

/// <summary>An exact provider draft origin; its revision is never replaced by refreshed page evidence.</summary>
internal sealed record ProviderEditCapture(object Owner, long Revision, AIProviderSettings? Provider);

/// <summary>Observed result bound to the tested provider/model; listing and billed inference remain distinct.</summary>
internal sealed record ProviderProbeOutcome(string ProviderId, string? ModelName, long Revision, bool IsInference, bool Success, string? Error);
