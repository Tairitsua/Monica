using Microsoft.EntityFrameworkCore;
using System.Diagnostics;
using Microsoft.EntityFrameworkCore.Storage;
using Monica.Repository.Outbox.Services;
using Monica.Repository.Inbox.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Logging;
using Monica.Core.Execution;
using Monica.Core.Results;
using Monica.Core.Results.Abstractions;
using Monica.Modules;
using Monica.Repository.Persistence.Models;
using Monica.Repository.Persistence.Abstractions;
using Monica.Repository.UnitOfWork.Abstractions;
using Monica.Repository.UnitOfWork.Models;
using Monica.Repository.Snapshots.Services;

namespace Monica.Repository.UnitOfWork.Services;

/// <summary>Scoped coordinator for one local transaction; never creates or disposes a DbContext scope.</summary>
public sealed class UnitOfWorkManager(
    IServiceProvider services,
    IEnumerable<RepositoryDbContextRegistration> registrations,
    IOptions<ModuleUnitOfWorkOption> configuration) : IUnitOfWorkManager, IUnitOfWork, IExecutionTransaction
{
    private const string ROLLBACK_EXCEPTION_DATA_KEY = "Monica.Repository.UnitOfWork.RollbackException";
    private readonly List<DbContext> _contexts = [];
    private IDbContextTransaction? _transaction;
    private bool _active;
    private bool _terminal;
    private UnitOfWorkScopeOptions _options = new();
    internal IReadOnlyList<DbContext> ParticipatingContexts => _contexts;

    /// <inheritdoc />
    public IUnitOfWork? Current => _active ? this : null;
    /// <inheritdoc />
    public Guid Id { get; } = Guid.NewGuid();

    /// <inheritdoc />
    public bool IsOperationActive
    {
        get { EnsureUsable(); return _active; }
    }

    /// <inheritdoc />
    public bool IsActive => _active && _transaction is not null;

    /// <inheritdoc />
    public ExecutionTransactionFailure? FirstFailure { get; private set; }

    /// <inheritdoc />
    public Task RunAsync(Func<Task> work, UnitOfWorkScopeOptions? options = null, CancellationToken cancellationToken = default)
        => RunAsync(async () => { await work(); return ExecutionUnit.Value; }, options, cancellationToken);

    /// <inheritdoc />
    public async Task<T> RunAsync<T>(Func<Task<T>> work, UnitOfWorkScopeOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(work);
        EnsureUsable();
        using var scope = services.GetRequiredService<ExecutionScopeContext>().Enter(services);
        if (_active)
        {
            try
            {
                if (options is not null) await JoinAsync(options, cancellationToken);
                var nestedResult = await work();
                cancellationToken.ThrowIfCancellationRequested();
                if (ExecutionOutcomePolicy.RequiresRollback(nestedResult, options?.IsReadOnly == true))
                    MarkRollbackOnly(ExecutionTransactionFailure.FromResult("Execution", nestedResult));
                return nestedResult;
            }
            catch (Exception exception) { MarkRollbackOnly(ExecutionTransactionFailure.FromException("Execution", exception)); throw; }
        }

        var domainEvents = services.GetRequiredService<DomainEventQueue>();
        Exception? failure = null;
        try
        {
            await BeginAsync(options ?? new(), cancellationToken);
            _active = true;
            var result = await work();
            cancellationToken.ThrowIfCancellationRequested();
            if (ExecutionOutcomePolicy.RequiresRollback(result, _options.IsReadOnly))
            {
                MarkRollbackOnly(ExecutionTransactionFailure.FromResult("Execution", result));
                await RollbackAsync();
                return result;
            }
            ThrowIfRollbackOnly("commit");
            if (!_options.IsReadOnly)
            {
                await domainEvents.DrainAsync(configuration.Value.MaximumDomainEvents, cancellationToken);
                await FlushAsync(cancellationToken);
                if (services.GetService<SnapshotTransaction>() is { } snapshots)
                    await snapshots.FinalizeAsync(cancellationToken);
            }
            ThrowIfRollbackOnly("commit");
            if (_transaction is not null) await _transaction.CommitAsync(cancellationToken);
            return result;
        }
        catch (Exception operationException)
        {
            failure = operationException;
            MarkRollbackOnly(ExecutionTransactionFailure.FromException("Execution", operationException));
            try { await RollbackAsync(); }
            catch (Exception rollbackException) { operationException.Data[ROLLBACK_EXCEPTION_DATA_KEY] = rollbackException; }
            throw;
        }
        finally
        {
            _active = false;
            _terminal = true;
            domainEvents.Close();
            if (_transaction is not null)
            {
                try { await _transaction.DisposeAsync(); }
                catch (Exception cleanup)
                {
                    if (failure is not null) failure.Data["Monica.Repository.UnitOfWork.DisposeException"] = cleanup;
                    // Commit/rollback has already established the outcome; a disposal error cannot reverse it.
                    services.GetService<ILogger<UnitOfWorkManager>>()?.LogError(cleanup, "Transaction cleanup failed for operation {OperationId}.", Id);
                }
            }
        }
    }

    /// <inheritdoc />
    public async Task<int> FlushAsync(CancellationToken cancellationToken = default)
    {
        EnsureUsable();
        ThrowIfRollbackOnly("flush changes");
        if (!_active) throw new InvalidOperationException("An active, healthy operation is required to flush.");
        try
        {
            var affected = 0;
            foreach (var context in _contexts) affected += await context.SaveChangesAsync(cancellationToken);
            // A later participant can stage a projection on the primary after its first save.
            if (_contexts.Count > 1 && _contexts[0] is IOutboxStoreContext { HasPendingOutboxMessages: true })
                affected += await _contexts[0].SaveChangesAsync(cancellationToken);
            return affected;
        }
        catch (Exception exception) { MarkRollbackOnly(ExecutionTransactionFailure.FromException("Flush", exception)); throw; }
    }

    /// <inheritdoc />
    public void MarkRollbackOnly(ExecutionTransactionFailure? failure = null)
    {
        if (FirstFailure is not null) return;
        failure ??= new("Explicit", "The caller explicitly requested rollback.");
        var execution = services.GetRequiredService<ExecutionScopeContext>();
        var operation = execution.Operation;
        // A caller naming a different operation must not inherit the parent's invocation identity.
        var inheritOperation = failure.Operation is null;
        var activity = Activity.Current;
        FirstFailure = failure with
        {
            Operation = failure.Operation ?? operation?.DisplayName,
            OperationKey = failure.OperationKey ?? (inheritOperation ? operation?.OperationKey : null),
            InvocationId = failure.InvocationId ?? (inheritOperation ? execution.InvocationId : null),
            TraceId = failure.TraceId ?? activity?.TraceId.ToString(),
            SpanId = failure.SpanId ?? activity?.SpanId.ToString()
        };
        services.GetService<ILogger<UnitOfWorkManager>>()?.LogDebug(FirstFailure.Exception,
            "Transaction {TransactionId} became rollback-only in {Operation} ({FailureSource}): {FailureReason}. " +
            "Status: {ResultStatus}; OperationKey: {OperationKey}; InvocationId: {InvocationId}; TraceId: {TraceId}; " +
            "SpanId: {SpanId}; DbContextType: {DbContextType}; CommandId: {CommandId}.",
            Id, FirstFailure.Operation, FirstFailure.Source, FirstFailure.Reason, FirstFailure.ResultStatus,
            FirstFailure.OperationKey, FirstFailure.InvocationId, FirstFailure.TraceId, FirstFailure.SpanId,
            FirstFailure.DbContextType, FirstFailure.CommandId);
    }

    internal void ThrowIfRollbackOnly(string action)
    {
        EnsureUsable();
        if (FirstFailure is { } failure) throw new TransactionRollbackOnlyException(Id, action, failure);
    }

    internal IOutboxStoreContext RequireOutboxOwner()
    {
        EnsureUsable();
        ThrowIfRollbackOnly("publish an Outbox message");
        if (!_active || _transaction is null || _contexts.Count == 0)
            throw new InvalidOperationException("Durable publishing requires an active, healthy local transaction.");
        var primary = _contexts[0];
        if (primary is not IOutboxStoreContext owner || !owner.HasOutbox)
            throw new InvalidOperationException("The primary transaction context must enable AddOutbox.");
        var current = primary.Database.CurrentTransaction;
        if (current is null || !ReferenceEquals(current.GetDbTransaction(), _transaction.GetDbTransaction()))
            throw new InvalidOperationException("The outbox owner is not enlisted in the operation's physical transaction.");
        return owner;
    }

    internal DbContext RequireInboxOwner()
    {
        EnsureUsable();
        ThrowIfRollbackOnly("handle an Inbox message");
        if (!_active || _transaction is null || _contexts.Count == 0)
            throw new InvalidOperationException("Inbox handling requires an active, healthy local transaction.");
        var primary = _contexts[0];
        if (primary is not IInboxStoreContext { HasInbox: true })
            throw new InvalidOperationException("The primary transaction context must enable AddInbox.");
        var current = primary.Database.CurrentTransaction;
        if (current is null || !ReferenceEquals(current.GetDbTransaction(), _transaction.GetDbTransaction()))
            throw new InvalidOperationException("The inbox owner is not enlisted in the operation's physical transaction.");
        return primary;
    }

    /// <summary>Validates participant access and reports whether the physical transaction is owned by this operation.</summary>
    internal bool ValidateSave(DbContext context)
    {
        EnsureUsable();
        ThrowIfRollbackOnly("access the database");
        var owner = context is IRepositoryContextAdapter { TransactionOwner: { } transactionOwner } ? transactionOwner : context;
        if (_transaction is null) return false;
        if (!_contexts.Contains(owner)
            || context.Database.ProviderName != _contexts[0].Database.ProviderName
            || !ReferenceEquals(context.Database.GetDbConnection(), _contexts[0].Database.GetDbConnection())
            || context.Database.CurrentTransaction is not { } current
            || !ReferenceEquals(current.GetDbTransaction(), _transaction.GetDbTransaction()))
        {
            var exception = new InvalidOperationException($"DbContext '{context.GetType().FullName}' (transaction owner '{owner.GetType().FullName}') " +
                $"must be a selected participant using active transaction '{Id}'. Participants: {string.Join(", ", _contexts.Select(x => x.GetType().FullName))}. " +
                "Select the executing domain's context and share its connection, or call through WithTransaction(ApiTransaction.Independent).");
            MarkRollbackOnly(ExecutionTransactionFailure.FromException("Participation", exception) with { DbContextType = context.GetType().FullName });
            throw exception;
        }
        return true;
    }

    private void EnsureUsable()
    {
        if (_terminal) throw new InvalidOperationException("This operation scope has ended. Resolve a fresh scope for the next operation.");
    }

    private async Task BeginAsync(UnitOfWorkScopeOptions options, CancellationToken token)
    {
        _options = options;
        token.ThrowIfCancellationRequested();
        var available = registrations.Where(x => x.ProviderType == DbContextProviderType.UnitOfWork)
            .Select(x => x.DbContextType).Distinct().ToArray();
        var selected = options.DbContextTypes?.Distinct().ToArray() ?? available;
        if (options.DbContextTypes is null && selected.Length > 1)
            throw new InvalidOperationException("Select the operation's DbContext explicitly when multiple write contexts are registered.");
        await EnlistAsync(selected, options, token);
    }

    private async Task JoinAsync(UnitOfWorkScopeOptions options, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (_transaction is not null && options.IsolationLevel is { } required &&
            required != _transaction.GetDbTransaction().IsolationLevel)
            throw new InvalidOperationException($"Nested isolation '{required}' cannot replace active isolation '{_transaction.GetDbTransaction().IsolationLevel}'.");
        if (options.CommandTimeout is { } timeout && timeout != _options.CommandTimeout)
            throw new InvalidOperationException("Nested work cannot replace the active operation's command timeout.");
        await EnlistAsync(options.DbContextTypes?.Distinct().ToArray() ?? [],
            _options with { IsolationLevel = options.IsolationLevel ?? _options.IsolationLevel }, token);
    }

    private async Task EnlistAsync(IReadOnlyList<Type> selected, UnitOfWorkScopeOptions options, CancellationToken token)
    {
        var additional = new List<DbContext>();
        foreach (var type in selected)
        {
            if (!registrations.Any(registration => registration.DbContextType == type))
                throw new InvalidOperationException($"Context '{type}' is not registered for unit-of-work participation.");
            var context = (DbContext)services.GetRequiredService(type);
            if (_contexts.Contains(context)) continue;
            if (!context.Database.IsRelational()) throw new NotSupportedException("Write operations require a relational provider with real transaction support.");
            if (context.Database.CurrentTransaction is not null)
                throw new InvalidOperationException("The operation boundary must own its transaction; an externally started transaction is already active.");
            additional.Add(context);
        }
        if (additional.Count == 0) return;
        var primary = _contexts.FirstOrDefault() ?? additional[0];
        // Validate all participants before opening a transaction. Independent stores cannot be committed atomically.
        if (additional.Any(x => !ReferenceEquals(x.Database.GetDbConnection(), primary.Database.GetDbConnection())
                               || x.Database.ProviderName != primary.Database.ProviderName))
            throw new NotSupportedException("Shared transactions require the same DbConnection instance and relational provider. " +
                "Use WithTransaction(ApiTransaction.Independent) for an independently committed API call.");
        foreach (var context in additional)
            if (options.CommandTimeout is { } timeout) context.Database.SetCommandTimeout(timeout);
        if (_transaction is null)
            _transaction = options.IsolationLevel is { } isolation
                ? await primary.Database.BeginTransactionAsync(isolation, token)
                : await primary.Database.BeginTransactionAsync(token);
        foreach (var context in additional)
        {
            if (!ReferenceEquals(context, primary))
                await context.Database.UseTransactionAsync(_transaction.GetDbTransaction(), token);
            _contexts.Add(context);
        }
    }

    private async Task RollbackAsync()
    {
        if (_transaction is not null) await _transaction.RollbackAsync(CancellationToken.None);
    }

}
