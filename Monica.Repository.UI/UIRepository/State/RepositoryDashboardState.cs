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
    /// <summary>Gets whether updates are permitted. Production is read-only unless the module explicitly opts in.</summary>
    public bool MigrationUpdatesEnabled => !hostEnvironment.IsProduction() || options.Value.AllowMigrationUpdateInProduction;
    /// <summary>Gets the host-configured migration timeout; the module default is one hour.</summary>
    public TimeSpan MigrationCommandTimeout => options.Value.MigrationCommandTimeout;

    /// <summary>Loads current registrations while preserving circuit selection when it still exists.</summary>
    public Task InitializeAsync(CancellationToken cancellationToken = default) => RefreshContextsAsync(cancellationToken: cancellationToken);

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
            RevealConnectionStrings = !RevealConnectionStrings;
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
    public async Task<RepositoryMigrationUpdateResult?> UpdateMigrationAsync(string contextId, CancellationToken cancellationToken = default)
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
                await RefreshCoreAsync(true, token);
            }
            finally { IsUpdatingMigrations = false; }
        }, cancellationToken);
        return outcome;
    }

    /// <summary>
    /// Applies pending migrations across all registered contexts in the facade's sequential order.
    /// A failed envelope is not a successful empty outcome; inspect MigrationCommandError before announcing success.
    /// </summary>
    public async Task<IReadOnlyList<RepositoryMigrationUpdateResult>> UpdateAllPendingMigrationsAsync(CancellationToken cancellationToken = default)
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
                await RefreshCoreAsync(true, token);
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

    private async Task RefreshCoreAsync(bool keepSelection, CancellationToken token)
    {
        var selectedId = keepSelection ? SelectedContext?.Registration.ContextId : null;
        IsLoadingContexts = true;
        ErrorMessage = null;
        try
        {
            var result = await facade.GetRegisteredContextsAsync(RevealConnectionStrings, token);
            token.ThrowIfCancellationRequested();
            var error = result.WithoutData();
            var snapshots = result.Data;
            if (result.IsFailure()) { ErrorMessage = error.Message; return; }
            if (snapshots is null) { ErrorMessage = "The repository context read returned no data."; return; }
            Contexts = snapshots.Select(snapshot => snapshot with { Options = snapshot.Options.ToArray() }).ToArray();
            ContextsCapturedAt = DateTime.UtcNow;
            SelectedContext = SelectSnapshot(selectedId) ?? Contexts.FirstOrDefault();
        }
        finally { IsLoadingContexts = false; }
        await LoadSelectedDetailsAsync(token);
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
            var migrationResult = await facade.GetMigrationStatusAsync(selected.Registration.ContextId, token);
            token.ThrowIfCancellationRequested();
            var migrationError = migrationResult.WithoutData();
            var migrationStatus = migrationResult.Data;
            var migrations = migrationResult.IsFailure() || migrationStatus is null
                ? new RepositoryMigrationStatus { Registration = selected.Registration, ErrorMessage = migrationError.Message ?? "The migration status was unavailable." }
                : migrationStatus with
                {
                    AllMigrations = migrationStatus.AllMigrations.ToArray(),
                    AppliedMigrations = migrationStatus.AppliedMigrations.ToArray(),
                    PendingMigrations = migrationStatus.PendingMigrations.ToArray()
                };
            var activityResult = await facade.GetActivityAsync(selected.Registration.ContextId, token);
            token.ThrowIfCancellationRequested();
            var activityError = activityResult.WithoutData();
            var activity = activityResult.Data;
            Activity = activityResult.IsFailure() || activity is null
                ? new RepositoryActivityResult { Registration = selected.Registration, Support = RepositoryActivitySupport.Failed, Message = activityError.Message ?? "The repository activity was unavailable." }
                : activity with { Rows = activity.Rows.Select(row => row with { }).ToArray() };
            MigrationStatus = migrations;
            DetailsCapturedAt = DateTime.UtcNow;
        }
        finally { IsLoadingDetails = false; }
    }

    private RepositoryDbContextSnapshot? SelectSnapshot(string? id) => Contexts.FirstOrDefault(context =>
        string.Equals(context.Registration.ContextId, id, StringComparison.Ordinal));
    private static RepositoryMigrationUpdateResult CopyResult(RepositoryMigrationUpdateResult result) =>
        result with { AttemptedMigrations = result.AttemptedMigrations.ToArray() };
}
