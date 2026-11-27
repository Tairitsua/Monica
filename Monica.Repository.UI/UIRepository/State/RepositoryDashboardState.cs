using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Monica.Core.Results;
using Monica.Modules;
using Monica.Repository.Facades;
using Monica.Repository.Persistence.Models;

namespace Monica.Repository.UI.UIRepository.State;

/// <summary>
/// Circuit-owned Repository evidence and context selection. Components own cancellation and drain
/// their operations before retirement. This borrowed state serializes reads and migration commands;
/// it owns no polling loop, component callback, or disposable DbContext.
/// </summary>
public sealed class RepositoryDashboardState(
    RepositoryDiagnosticsFacade facade,
    IOptions<ModuleRepositoryUIOption> options,
    IHostEnvironment hostEnvironment)
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dictionary<string, ActivitySample> _activitySamples = new(StringComparer.Ordinal);

    private sealed record ActivitySample(RepositoryActivityResult Result, DateTime CapturedAt, string? RefreshError = null);

    /// <summary>Gets the last successfully captured Repository registrations, including per-context read failures.</summary>
    public IReadOnlyList<RepositoryDbContextSnapshot> Contexts { get; private set; } = [];
    /// <summary>Gets the registration selected by its exact opaque context ID, or null when the requested ID is absent.</summary>
    public RepositoryDbContextSnapshot? SelectedContext { get; private set; }
    /// <summary>Gets the selected context's provider activity result; null means that no detail read has completed.</summary>
    public RepositoryActivityResult? Activity { get; private set; }
    /// <summary>Gets the selected context's migration inventory and history, including any provider read error.</summary>
    public RepositoryMigrationStatus? MigrationStatus { get; private set; }
    /// <summary>Gets detached per-context outcomes from the last completed migration command in this circuit.</summary>
    public IReadOnlyList<RepositoryMigrationUpdateResult> UpdateResults { get; private set; } = [];
    /// <summary>Gets the latest facade envelope error. A failed bulk command must not be interpreted as successful empty results.</summary>
    public string? ErrorMessage { get; private set; }
    /// <summary>Gets the last migration command's envelope error, separate from any subsequent registry refresh failure.</summary>
    public string? MigrationCommandError { get; private set; }
    /// <summary>Gets whether the serialized owner is reading the registration registry.</summary>
    public bool IsLoadingContexts { get; private set; }
    /// <summary>Gets whether the serialized owner is reading the selected context's migration and activity evidence.</summary>
    public bool IsLoadingDetails { get; private set; }
    /// <summary>Gets whether a migration command and its follow-up refresh are running.</summary>
    public bool IsUpdatingMigrations { get; private set; }
    /// <summary>Gets whether the operator explicitly requested full connection strings. The initial value is false.</summary>
    public bool RevealConnectionStrings { get; private set; }
    /// <summary>Gets the UTC adoption time of the last successful registration snapshot, or null before the first capture.</summary>
    public DateTime? ContextsCapturedAt { get; private set; }
    /// <summary>Gets the UTC adoption time of the selected detail results, including failed or unsupported provider results.</summary>
    public DateTime? DetailsCapturedAt { get; private set; }
    /// <summary>Gets the last activity read failure when a prior sample is still being displayed.</summary>
    public string? RetainedActivityError { get; private set; }
    /// <summary>Gets whether updates are permitted. Production is read-only unless the module explicitly opts in.</summary>
    public bool MigrationUpdatesEnabled => !hostEnvironment.IsProduction() || options.Value.AllowMigrationUpdateInProduction;
    /// <summary>Gets the host-configured migration timeout; the module default is one hour.</summary>
    public TimeSpan MigrationCommandTimeout => options.Value.MigrationCommandTimeout;

    /// <summary>Loads current registrations while preserving circuit selection when it still exists.</summary>
    public Task InitializeAsync(CancellationToken cancellationToken = default) => RefreshContextsAsync(cancellationToken: cancellationToken);

    /// <summary>
    /// Prepares a routed workspace. Registry evidence is shared across routes. A provider activity
    /// query runs only for the selected registration on the activity route, on first use or refresh.
    /// A missing explicit registration ID never falls back to another context.
    /// </summary>
    /// <param name="contextId">Optional exact registration ID. A null value preserves selection or uses the first registration.</param>
    /// <param name="readActivity">Whether the current route needs activity evidence.</param>
    /// <param name="refresh">Whether to refresh the registry and any selected activity.</param>
    /// <param name="revealConnectionStrings">Optional explicit change to connection-string visibility.</param>
    /// <param name="cancellationToken">Cancellation owned by the routed component.</param>
    public Task PrepareWorkspaceAsync(string? contextId, bool readActivity, bool refresh,
        bool? revealConnectionStrings, CancellationToken cancellationToken) =>
        SerializeAsync(async token =>
        {
            if (revealConnectionStrings is { } reveal)
            {
                SetReveal(reveal);
            }

            var previousId = SelectedContext?.Registration.ContextId;
            var capture = refresh || ContextsCapturedAt is null || revealConnectionStrings is not null;
            if (capture && !await CaptureContextsAsync(token))
            {
                if (contextId is not null && contextId != previousId)
                {
                    SelectWorkspaceContext(contextId, readActivity);
                }
                return;
            }

            SelectWorkspaceContext(contextId ?? previousId, readActivity, useDefault: contextId is null);
            if (!readActivity || SelectedContext is not { } selected ||
                (Activity is not null && !refresh)) return;

            IsLoadingDetails = true;
            try
            {
                var result = await ReadActivityAsync(selected, token);
                if (result.Support == RepositoryActivitySupport.Failed &&
                    _activitySamples.TryGetValue(selected.Registration.ContextId, out var previous) &&
                    previous.Result.Support == RepositoryActivitySupport.Supported)
                {
                    RetainedActivityError = result.Message;
                    _activitySamples[selected.Registration.ContextId] = previous with { RefreshError = result.Message };
                }
                else
                    AdoptActivity(result);
            }
            finally { IsLoadingDetails = false; }
        }, cancellationToken);

    /// <summary>Returns the last supported sample for a registration in this circuit, when available.</summary>
    public RepositoryActivitySummary? FindSampledActivity(string contextId) =>
        _activitySamples.TryGetValue(contextId, out var sample) ? sample.Result.Summary : null;

    private void SelectWorkspaceContext(string? contextId, bool readActivity, bool useDefault = false)
    {
        SelectedContext = SelectSnapshot(contextId) ?? (useDefault ? Contexts.FirstOrDefault() : null);
        MigrationStatus = SelectedContext?.MigrationStatus;
        Activity = null;
        DetailsCapturedAt = null;
        RetainedActivityError = null;
        if (readActivity && SelectedContext is { } selected &&
            _activitySamples.TryGetValue(selected.Registration.ContextId, out var sample))
        {
            Activity = sample.Result;
            DetailsCapturedAt = sample.CapturedAt;
            RetainedActivityError = sample.RefreshError;
        }
    }

    /// <summary>Refreshes registrations, retaining prior successful evidence on a failed read.</summary>
    public Task RefreshContextsAsync(bool keepSelection = true, CancellationToken cancellationToken = default) =>
        SerializeAsync(token => RefreshCoreAsync(keepSelection, token), cancellationToken);

    /// <summary>Selects the exact opaque registration ID. Missing IDs do not fall back to another context.</summary>
    public Task SelectContextAsync(string contextId, CancellationToken cancellationToken = default) =>
        SerializeAsync(async token =>
        {
            SelectedContext = SelectSnapshot(contextId);
            await LoadSelectedDetailsAsync(token);
        }, cancellationToken);

    /// <summary>Explicitly toggles full connection-string evidence and reloads provider snapshots.</summary>
    public Task ToggleConnectionStringRevealAsync(CancellationToken cancellationToken = default) =>
        SerializeAsync(async token =>
        {
            SetReveal(!RevealConnectionStrings);
            await RefreshCoreAsync(true, token);
        }, cancellationToken);

    /// <summary>Applies the selected context's pending migrations. The ID is frozen at admission.</summary>
    public Task<RepositoryMigrationUpdateResult?> UpdateSelectedMigrationAsync(CancellationToken cancellationToken = default) =>
        UpdateMigrationAsync(SelectedContext?.Registration.ContextId ?? string.Empty, cancellationToken);

    /// <summary>
    /// Applies migrations to the exact context confirmed by the operator. Production requires the
    /// module's explicit opt-in. Results describe the actual command, not an atomic multi-context transaction.
    /// Cancellation may interrupt observation after database changes have committed; refresh before retrying.
    /// </summary>
    /// <param name="contextId">The exact Repository registration ID captured before confirmation.</param>
    /// <param name="cancellationToken">Component-owned cancellation. The component must drain this task before retiring.</param>
    /// <returns>The actual provider outcome, or null when admission is denied or the facade read fails.</returns>
    public Task<RepositoryMigrationUpdateResult?> UpdateMigrationAsync(string contextId, CancellationToken cancellationToken = default) =>
        UpdateMigrationAsync(contextId, true, cancellationToken);

    internal async Task<RepositoryMigrationUpdateResult?> UpdateMigrationAsync(string contextId, bool refreshDetails, CancellationToken cancellationToken)
    {
        RepositoryMigrationUpdateResult? outcome = null;
        await SerializeAsync(async token =>
        {
            if (!MigrationUpdatesEnabled || SelectSnapshot(contextId) is null) return;
            IsUpdatingMigrations = true;
            ErrorMessage = null;
            MigrationCommandError = null;
            try
            {
                var result = await facade.UpdateMigrationAsync(contextId, MigrationCommandTimeout, token);
                token.ThrowIfCancellationRequested();
                var error = result.WithoutData();
                var update = result.Data;
                if (result.IsFailure())
                {
                    ErrorMessage = MigrationCommandError = error.Message;
                    UpdateResults = [];
                    return;
                }
                if (update is null)
                {
                    ErrorMessage = MigrationCommandError = "The migration update returned no data.";
                    UpdateResults = [];
                    return;
                }
                outcome = update;
                UpdateResults = [CopyResult(update)];
                await RefreshCoreAsync(true, token, refreshDetails);
            }
            finally { IsUpdatingMigrations = false; }
        }, cancellationToken);
        return outcome;
    }

    /// <summary>
    /// Applies pending migrations across all registered contexts in the facade's sequential order.
    /// A failed envelope is not a successful empty outcome; inspect MigrationCommandError before announcing success.
    /// </summary>
    public Task<IReadOnlyList<RepositoryMigrationUpdateResult>> UpdateAllPendingMigrationsAsync(CancellationToken cancellationToken = default) =>
        UpdateAllPendingMigrationsAsync(true, cancellationToken);

    internal async Task<IReadOnlyList<RepositoryMigrationUpdateResult>> UpdateAllPendingMigrationsAsync(bool refreshDetails, CancellationToken cancellationToken)
    {
        IReadOnlyList<RepositoryMigrationUpdateResult> outcomes = [];
        await SerializeAsync(async token =>
        {
            if (!MigrationUpdatesEnabled) return;
            IsUpdatingMigrations = true;
            ErrorMessage = null;
            MigrationCommandError = null;
            try
            {
                var result = await facade.UpdateAllPendingMigrationsAsync(MigrationCommandTimeout, token);
                token.ThrowIfCancellationRequested();
                var error = result.WithoutData();
                var updates = result.Data;
                if (result.IsFailure())
                {
                    ErrorMessage = MigrationCommandError = error.Message;
                    UpdateResults = [];
                    return;
                }
                if (updates is null)
                {
                    ErrorMessage = MigrationCommandError = "The migration updates returned no data.";
                    UpdateResults = [];
                    return;
                }
                outcomes = updates.Select(CopyResult).ToArray();
                UpdateResults = outcomes;
                await RefreshCoreAsync(true, token, refreshDetails);
            }
            finally { IsUpdatingMigrations = false; }
        }, cancellationToken);
        return outcomes;
    }

    private async Task SerializeAsync(Func<CancellationToken, Task> operation, CancellationToken token)
    {
        await _gate.WaitAsync(token);
        try { token.ThrowIfCancellationRequested(); await operation(token); }
        finally { _gate.Release(); }
    }

    private async Task RefreshCoreAsync(bool keepSelection, CancellationToken token, bool refreshDetails = true)
    {
        var selectedId = keepSelection ? SelectedContext?.Registration.ContextId : null;
        if (!await CaptureContextsAsync(token)) return;
        SelectWorkspaceContext(selectedId, readActivity: false, useDefault: true);
        if (refreshDetails) await LoadSelectedDetailsAsync(token);
    }

    private void SetReveal(bool reveal)
    {
        RevealConnectionStrings = reveal;
        if (reveal) return;
        var selectedId = SelectedContext?.Registration.ContextId;
        Contexts = Contexts.Select(snapshot => snapshot.Connection?.IsRevealed == true
            ? snapshot with { Connection = snapshot.Connection with { RevealedConnectionString = null } }
            : snapshot).ToArray();
        SelectedContext = SelectSnapshot(selectedId);
    }

    private async Task<bool> CaptureContextsAsync(CancellationToken token)
    {
        IsLoadingContexts = true;
        ErrorMessage = null;
        try
        {
            var result = await facade.GetRegisteredContextsAsync(RevealConnectionStrings, token);
            token.ThrowIfCancellationRequested();
            var error = result.WithoutData();
            var snapshots = result.Data;
            if (result.IsFailure()) { ErrorMessage = error.Message; return false; }
            if (snapshots is null) { ErrorMessage = "The repository context read returned no data."; return false; }
            Contexts = snapshots.Select(snapshot => snapshot with { Options = snapshot.Options.ToArray() }).ToArray();
            ContextsCapturedAt = DateTime.UtcNow;
            foreach (var id in _activitySamples.Keys.Where(id => SelectSnapshot(id) is null).ToArray())
                _activitySamples.Remove(id);
            return true;
        }
        finally { IsLoadingContexts = false; }
    }

    private async Task LoadSelectedDetailsAsync(CancellationToken token)
    {
        var selected = SelectedContext;
        Activity = null;
        MigrationStatus = null;
        DetailsCapturedAt = null;
        if (selected is null) return;
        IsLoadingDetails = true;
        try
        {
            var migrations = selected.MigrationStatus ?? await ReadMigrationsAsync(selected, token);
            AdoptActivity(await ReadActivityAsync(selected, token));
            MigrationStatus = migrations;
        }
        finally { IsLoadingDetails = false; }
    }

    private RepositoryDbContextSnapshot? SelectSnapshot(string? id) => Contexts.FirstOrDefault(context =>
        string.Equals(context.Registration.ContextId, id, StringComparison.Ordinal));

    private async Task<RepositoryMigrationStatus> ReadMigrationsAsync(RepositoryDbContextSnapshot selected, CancellationToken token)
    {
        var result = await facade.GetMigrationStatusAsync(selected.Registration.ContextId, token);
        token.ThrowIfCancellationRequested();
        if (result.IsFailure() || result.Data is not { } status)
            return new RepositoryMigrationStatus { Registration = selected.Registration, ErrorMessage = result.WithoutData().Message ?? "The migration status was unavailable." };
        return status with
        {
            AllMigrations = status.AllMigrations.ToArray(),
            AppliedMigrations = status.AppliedMigrations.ToArray(),
            PendingMigrations = status.PendingMigrations.ToArray()
        };
    }

    private async Task<RepositoryActivityResult> ReadActivityAsync(RepositoryDbContextSnapshot selected, CancellationToken token)
    {
        var result = await facade.GetActivityAsync(selected.Registration.ContextId, token);
        token.ThrowIfCancellationRequested();
        if (result.IsFailure() || result.Data is not { } activity)
            return new RepositoryActivityResult { Registration = selected.Registration, Support = RepositoryActivitySupport.Failed, Message = result.WithoutData().Message ?? "The repository activity was unavailable." };
        return activity with { Rows = activity.Rows.ToArray() };
    }

    private void AdoptActivity(RepositoryActivityResult result)
    {
        var capturedAt = DateTime.UtcNow;
        Activity = result;
        DetailsCapturedAt = capturedAt;
        RetainedActivityError = null;
        _activitySamples[result.Registration.ContextId] = new ActivitySample(result, capturedAt);
    }
    private static RepositoryMigrationUpdateResult CopyResult(RepositoryMigrationUpdateResult result) =>
        result with { AttemptedMigrations = result.AttemptedMigrations.ToArray() };
}
