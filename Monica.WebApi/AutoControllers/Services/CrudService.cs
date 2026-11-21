using System.Linq.Dynamic.Core;
using System.Linq.Expressions;
using System.Runtime.CompilerServices;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.DynamicLinq;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Monica.Application.Abstractions;
using Monica.Core.Execution;
using Monica.Core.Results;
using Monica.ModelQuery.Abstractions;
using Monica.ModelQuery.Models;
using Monica.Repository.Entity.Abstractions;
using Monica.Repository.Entity.Abstractions.Auditing;
using Monica.Repository.Persistence.Abstractions;
using Monica.Repository.Persistence.Exceptions;
using Monica.Repository.Persistence.Extensions;
using Monica.Repository.UnitOfWork.Abstractions;
using Monica.Tool.Extensions;
using Monica.WebApi.AutoControllers.Abstractions;
using Monica.WebApi.AutoControllers.Models;

namespace Monica.WebApi.AutoControllers.Services;

/// <summary>
/// Publishes detail, page, and stream reads for an EF-backed resource through AutoControllers.
/// Define each supported write as a public action using the protected persistence operations.
/// </summary>
/// <typeparam name="TEntity">The entity read from and written to the repository.</typeparam>
/// <typeparam name="TKey">The entity key and the type of the conventional <c>{id}</c> route parameter.</typeparam>
/// <typeparam name="TDetail">The entity detail response type.</typeparam>
/// <typeparam name="TRow">The full list row, which exposes the entity key for cursor continuation.</typeparam>
/// <typeparam name="TQuery">The query input. Its implemented request interfaces enable the corresponding query features.</typeparam>
/// <param name="repository">The scoped EF store that owns entity visibility and persistence.</param>
/// <remarks>
/// Register AutoControllers, the scoped repository, and the execution transaction pipeline for write actions.
/// The configured <see cref="CrudControllerOption.CrudControllerPostfix"/> is stripped from the conventional route name.
/// </remarks>
public abstract class CrudService<TEntity, TKey, TDetail, TRow, TQuery>(IEfEntityStore<TEntity, TKey> repository)
    : ApplicationService, ICrudApplicationService
    where TEntity : class, IEntity<TKey>
    where TRow : IEntityDto<TKey>
{
    /// <summary>Gets the model query operator for entity filtering and field projection.</summary>
    protected IModelQuery<TEntity> ModelQuery => CachedServiceProvider.GetRequiredService<IModelQuery<TEntity>>();

    /// <summary>Gets the manager of the active execution transaction.</summary>
    protected IUnitOfWorkManager UnitOfWorkManager => CachedServiceProvider.GetRequiredService<IUnitOfWorkManager>();

    /// <summary>Gets the repository for the current resource.</summary>
    protected virtual IEfEntityStore<TEntity, TKey> Repository { get; } = repository;

    /// <summary>
    /// Gets request cancellation. The default is <c>RequestAborted</c> during HTTP execution and
    /// <see cref="CancellationToken.None"/> outside HTTP. Adapters may override it for another execution owner.
    /// </summary>
    protected virtual CancellationToken QueryCancellationToken => CachedServiceProvider
        .GetService<IHttpContextAccessor>()?.HttpContext?.RequestAborted ?? CancellationToken.None;

    /// <summary>Reads a visible entity and converts it to the published detail response.</summary>
    /// <param name="id">The entity key. Conventional routes bind this parameter as <c>{id}</c>.</param>
    /// <returns>The detail response or the resource's entity-not-found failure.</returns>
    [ExecutionTransaction(ExecutionTransactionMode.None)]
    public virtual async Task<Result<TDetail>> GetAsync(TKey id)
    {
        var ct = QueryCancellationToken;
        try
        {
            var entity = await GetEntityByIdAsync(id, ct);
            return Result.Success(await ToDetailAsync(entity, ct));
        }
        catch (EntityNotFoundException)
        {
            return Result.FromFailure<TDetail>(ResEntityNotFound(id?.ToString() ?? string.Empty));
        }
    }

    /// <summary>Reads a page of full rows or selected JSON properties using query-string input.</summary>
    [ExecutionTransaction(ExecutionTransactionMode.None)]
    public virtual async Task<Result<Page<dynamic>>> GetListAsync(TQuery input)
    {
        var ct = QueryCancellationToken;
        ct.ThrowIfCancellationRequested();
        var selection = (input as IFieldSelectionRequest)?.GetFieldSelection();
        if (selection is not null) ModelQuery.ResolveFields(selection);
        var filtered = await CreateFilteredQueryAsync(input);
        if (selection is not null)
        {
            return Result.Success(await ReadValidatedFieldPageAsync(input, filtered, selection, ct));
        }

        var page = await ReadPageAsync(input, filtered, ct);
        if (page.TotalCount == 0) return Result.Success(new Page<dynamic>(0, []));

        var enriched = await EnrichPageAsync(input, page.Items, ct);
        if (enriched.IsFailure()) return Result.FromFailure<Page<dynamic>>(enriched);

        var rows = enriched.Data ?? throw new InvalidOperationException("CRUD page enrichment returned no data.");
        return Result.Success(new Page<dynamic>(page.TotalCount, rows.Cast<object>().ToArray(),
            page.CurrentPage, page.PageSize, page.Cursor));
    }

    /// <summary>Reads the same page as <see cref="GetListAsync"/> using a request body.</summary>
    [HttpPost]
    [ExecutionTransaction(ExecutionTransactionMode.None)]
    public virtual Task<Result<Page<dynamic>>> ListAsync(TQuery input) => GetListAsync(input);

    /// <summary>
    /// Streams a JSON array of full rows or selected JSON properties. Applies the request's maximum row count
    /// when supported. Field selections are validated before enumeration starts.
    /// </summary>
    /// <param name="input">The stream query parameters.</param>
    /// <param name="cancellationToken">Optional execution cancellation for a direct call.</param>
    /// <returns>A stream to enumerate within the repository's DI scope.</returns>
    [HttpPost]
    [ExecutionTransaction(ExecutionTransactionMode.None)]
    public virtual IAsyncEnumerable<object> ListStreamAsync(TQuery input, CancellationToken cancellationToken = default)
    {
        var selection = (input as IFieldSelectionRequest)?.GetFieldSelection();
        if (selection is not null) ModelQuery.ResolveFields(selection);
        return ReadListStreamAsync(input, selection, QueryCancellationToken, cancellationToken);
    }

    /// <summary>Converts a visible entity from the detail query to the published detail type.</summary>
    protected abstract Task<TDetail> ToDetailAsync(TEntity entity, CancellationToken ct);

    /// <summary>
    /// Reads exactly one row per entity from the ordered, bounded query. Preserve the supplied order and key.
    /// Generated query projections and incremental conversions of materialized entities both implement this contract.
    /// The caller passes <paramref name="ct"/> to enumeration and closes the reader before page enrichment.
    /// </summary>
    protected abstract IAsyncEnumerable<TRow> ReadRowsAsync(IQueryable<TEntity> window, CancellationToken ct);

    /// <summary>
    /// Enriches the materialized full-row page. A successful replacement preserves order and row identity.
    /// The default returns <paramref name="rows"/>. A failure supplies the HTTP action's result status and metadata.
    /// </summary>
    protected virtual Task<Result<IReadOnlyList<TRow>>> EnrichPageAsync(
        TQuery input, IReadOnlyList<TRow> rows, CancellationToken ct)
        => Task.FromResult(Result.Success(rows));

    /// <summary>
    /// Reads a fixed-row page from a provider-backed filtered source using <see cref="ReadRowsAsync"/>.
    /// Returns the raw page before enrichment and treats any input field selections as caller-owned data.
    /// </summary>
    protected Task<Page<TRow>> ReadPageAsync(TQuery input, IQueryable<TEntity> filtered, CancellationToken ct)
        => ReadProjectedPageAsync(input, filtered, ReadRowsAsync, ct);

    /// <summary>
    /// Reads an alternate fixed-row page from a provider-backed filtered source. The supplied projection
    /// preserves the entity window's order and cardinality. A projected <see cref="IEntityDto{TKey}"/>
    /// supplies the cursor, and a row implementing <see cref="IHasDtoSequenceNumber"/> receives its sequence number.
    /// </summary>
    protected Task<Page<TItem>> ReadPageAsync<TItem>(TQuery input, IQueryable<TEntity> filtered,
        Func<IQueryable<TEntity>, IQueryable<TItem>> project, CancellationToken ct)
        => ReadProjectedPageAsync(input, filtered, (window, _) => project(window).AsAsyncEnumerable(), ct);

    /// <summary>
    /// Reads the selected properties from a provider-backed filtered source. A distinct request counts and pages
    /// the distinct selected tuples. Selected pages carry item and count data with null navigation metadata.
    /// </summary>
    protected async Task<Page<object>> ReadFieldPageAsync(TQuery input, IQueryable<TEntity> filtered,
        FieldSelection selection, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        ModelQuery.ResolveFields(selection);
        return await ReadValidatedFieldPageAsync(input, filtered, selection, ct);
    }

    private async Task<Page<object>> ReadValidatedFieldPageAsync(TQuery input, IQueryable<TEntity> filtered,
        FieldSelection selection, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        ApplyHostPagingDefaults(input);
        var distinct = (input as IHasRequestFeature)?.GetSetting()?.FeatureFlags?.HasFlag(ERequestFeature.Distinct) is true;
        int? total = null;
        if (!distinct && input is not IHasRequestPage { DisablePage: true })
        {
            total = await filtered.CountAsync(ct);
            if (total == 0) return new Page<object>(0, []);
        }

        var query = ModelQuery.SelectFields(ApplySorting(filtered, input, selection), selection);
        if (distinct)
        {
            query = query.Distinct();
            total = await query.CountAsync(ct);
        }

        var (offset, limit, _, _) = GetPagingWindow(input);
        if (offset is { } skip) query = query.Skip(skip);
        if (limit is { } take) query = query.Take(take);

        var rows = await query.ToDynamicListAsync(ct);
        return new Page<object>(total ?? rows.Count, rows);
    }

    private async Task<Page<TItem>> ReadProjectedPageAsync<TItem>(TQuery input, IQueryable<TEntity> filtered,
        Func<IQueryable<TEntity>, CancellationToken, IAsyncEnumerable<TItem>> read, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        ApplyHostPagingDefaults(input);

        int? total = null;
        if (input is not IHasRequestPage { DisablePage: true } && !ShouldSkipEntityCount(input))
        {
            total = await filtered.CountAsync(ct);
            if (total == 0) return new Page<TItem>(0, []);
        }

        var window = ApplySorting(filtered, input);
        var (offset, limit, currentPage, pageSize) = GetPagingWindow(input);
        if (offset is { } skip) window = window.Skip(skip);
        if (limit is { } take) window = window.Take(take);

        var rows = await read(window, ct).ToListAsync(ct);
        var cursor = rows.Count > 0 && rows[^1] is IEntityDto<TKey> dto ? dto.Id?.ToString() : null;
        if (currentPage is { } page && pageSize is { } size)
        {
            var start = (page - 1) * size + 1;
            for (var index = 0; index < rows.Count; index++)
            {
                if (rows[index] is IHasDtoSequenceNumber numbered)
                {
                    numbered.Num = start + index;
                    rows[index] = (TItem)numbered;
                }
            }
        }

        return new Page<TItem>(total ?? rows.Count, rows, currentPage, pageSize, cursor);
    }

    private async IAsyncEnumerable<object> ReadListStreamAsync(TQuery input, FieldSelection? selection,
        CancellationToken requestCancellation, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        using var linked = requestCancellation.CanBeCanceled && cancellationToken.CanBeCanceled
            && requestCancellation != cancellationToken
            ? CancellationTokenSource.CreateLinkedTokenSource(requestCancellation, cancellationToken)
            : null;
        var ct = linked?.Token ?? (cancellationToken.CanBeCanceled ? cancellationToken : requestCancellation);
        ct.ThrowIfCancellationRequested();
        ApplyHostPagingDefaults(input);
        var filtered = await CreateFilteredQueryAsync(input);
        var query = ApplySorting(filtered, input, selection);
        if (input is IHasRequestLimitedResult limited)
        {
            query = query.Take(limited.MaxResultCount);
        }

        if (selection is not null)
        {
            var selectedRows = (IAsyncEnumerable<object>)ModelQuery.SelectFields(query, selection);
            await foreach (var row in selectedRows.WithCancellation(ct)) yield return row;
            yield break;
        }

        await foreach (var row in ReadRowsAsync(query, ct).WithCancellation(ct))
        {
            ct.ThrowIfCancellationRequested();
            yield return row;
        }
    }

    /// <summary>Builds a filtered query over the current or supplied repository without materializing its entities.</summary>
    /// <param name="input">The list query input.</param>
    /// <param name="repository">An alternate scoped EF store, such as a history repository. Defaults to <see cref="Repository"/>.</param>
    protected virtual async Task<IQueryable<TEntity>> CreateFilteredQueryAsync(TQuery input,
        IEfEntityStore<TEntity, TKey>? repository = null)
    {
        repository ??= Repository;
        var query = repository.Query;
        if (WithDetail() && repository is IRepositoryDetailsConfigurator<TEntity> details)
        {
            query = details.ApplyDetails(query);
        }

        query = ApplyListInclude(query, input);
        if (input is not IModelFilterRequest filterRequest)
        {
            return await ApplyCustomFilterQueryAsync(input, query);
        }

        var filter = string.IsNullOrEmpty(filterRequest.Filter) ? null : ModelQuery.ParseFilter(filterRequest.Filter);
        if (filter?.ReferencedFields.Any(field => field.ReflectionName == nameof(IHasSoftDelete.IsDeleted)) is true)
        {
            query = query.IncludeSoftDeleted();
        }

        query = await ApplyCustomFilterQueryAsync(input, query);
        if (filter is not null) query = ModelQuery.ApplyFilter(query, filter);
        if (!string.IsNullOrEmpty(filterRequest.SearchText))
        {
            query = ModelQuery.ApplySearch(query, filterRequest.SearchText, filterRequest.SearchFields);
        }

        return query;
    }

    /// <summary>Applies additional authorization and query restrictions before counting and paging.</summary>
    /// <returns>A provider-backed query that retains the resource's visibility rules.</returns>
    protected virtual Task<IQueryable<TEntity>> ApplyCustomFilterQueryAsync(TQuery input, IQueryable<TEntity> query)
        => Task.FromResult(query);

    /// <summary>Controls application of the repository-configured list includes. Defaults to <see langword="false"/>.</summary>
    protected virtual bool WithDetail() => false;

    /// <summary>Applies resource-specific includes to the list query.</summary>
    protected virtual IQueryable<TEntity> ApplyListInclude(IQueryable<TEntity> query, TQuery input) => query;

    /// <summary>Applies resource-specific includes and query restrictions to detail and tracked update lookups.</summary>
    protected virtual IQueryable<TEntity> ApplyInclude(IQueryable<TEntity> query) => query;

    /// <summary>Loads a visible entity for the detail response.</summary>
    /// <exception cref="EntityNotFoundException">The supplied key could not be found in the detail query.</exception>
    protected virtual Task<TEntity> GetEntityByIdAsync(TKey id, CancellationToken ct)
        => ReadEntityByIdAsync(Repository.Query, id, ct);

    /// <summary>Loads a visible entity into the repository's change tracker for a write action.</summary>
    /// <exception cref="EntityNotFoundException">The supplied key could not be found in the detail query.</exception>
    protected virtual Task<TEntity> GetTrackedEntityByIdAsync(TKey id, CancellationToken ct)
        => ReadEntityByIdAsync(Repository.Query.AsTracking(), id, ct);

    private async Task<TEntity> ReadEntityByIdAsync(IQueryable<TEntity> query, TKey id, CancellationToken ct)
    {
        return await ApplyInclude(query).OrderBy(entity => entity.Id).FirstOrDefaultAsync(entity => entity.Id!.Equals(id), ct)
            ?? throw new EntityNotFoundException(typeof(TEntity), id);
    }

    /// <summary>
    /// Stages a valid entity and flushes the current execution transaction so the response can use its generated key.
    /// The surrounding pipeline owns commit and rollback.
    /// </summary>
    /// <exception cref="InvalidOperationException">There is no active execution transaction.</exception>
    protected async Task<Result> InsertEntityAsync(TEntity entity, CancellationToken ct)
    {
        var session = UnitOfWorkManager.Current
            ?? throw new InvalidOperationException("CRUD writes require an execution-pipeline transaction.");
        ct.ThrowIfCancellationRequested();
        Repository.Add(entity);
        await session.FlushAsync(ct);
        return ResEntityCreateSuccess(entity.Id?.ToString() ?? string.Empty);
    }

    /// <summary>
    /// Loads the tracked entity by route key and invokes <paramref name="apply"/> once. The action validates its input
    /// and owns mutation policy. The mutation preserves identity and is committed by the surrounding pipeline.
    /// </summary>
    protected async Task<Result> UpdateEntityAsync(TKey id, Action<TEntity> apply, CancellationToken ct)
    {
        var entity = await GetTrackedEntityByIdAsync(id, ct);
        ct.ThrowIfCancellationRequested();
        apply(entity);
        return ResEntityUpdateSuccess(entity.Id?.ToString() ?? string.Empty);
    }

    /// <summary>Stages policy-aware removal and returns success when the entity is already absent.</summary>
    protected async Task<Result> DeleteEntityAsync(TKey id, CancellationToken ct)
    {
        var entity = await Repository.FindAsync(id, ct);
        if (entity is not null) Repository.Remove(entity);
        return ResEntityDeleteSuccess(id?.ToString() ?? string.Empty);
    }

    /// <summary>Stages tracked, policy-aware removal of the matching entities in the supplied key collection.</summary>
    protected async Task<Result> DeleteEntitiesAsync(IEnumerable<TKey> ids, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var keys = ids.ToArray();
        await Repository.RemoveByIdsAsync(keys, ct);
        return ResEntityDeleteSuccess(string.Join(",", keys));
    }

    /// <summary>
    /// Orders the entity query and applies a keyset cursor where supported.
    /// Pass the selection only when executing a field projection, so sharded fixed-row queries retain their ordering.
    /// </summary>
    protected virtual IQueryable<TEntity> ApplySorting(IQueryable<TEntity> query, TQuery input,
        FieldSelection? selection = null)
    {
        if (input is IHasRequestSorting { Sorting: { } sorting } && !string.IsNullOrWhiteSpace(sorting))
        {
            query = ApplyRequestedSorting(query, sorting);
            return ShouldApplyStablePaginationSorting(input) ? ApplyStablePaginationSorting(query, sorting) : query;
        }

        // Sharding providers may sort after projection, where the entity's ordering fields are absent.
        if (selection is not null && Repository.IsShardingTable) return query;

        if (input is IHasRequestKeysetPage request && request.HasUsingKeyset())
        {
            query = FilterByKeyset(query, request.Cursor!);
        }

        return input is IHasRequestLimitedResult ? ApplyDefaultSorting(query) : query;
    }

    /// <summary>Applies explicit client ordering as the query's primary order.</summary>
    protected virtual IQueryable<TEntity> ApplyRequestedSorting(IQueryable<TEntity> query, string sorting)
        => query.OrderBy(sorting);

    /// <summary>Adds the entity key as a deterministic tie-breaker when the explicit ordering omits it.</summary>
    protected virtual IQueryable<TEntity> ApplyStablePaginationSorting(IQueryable<TEntity> query, string sorting)
        => ContainsEntityIdSorting(sorting) || !query.HasBeenOrdered(out var ordered)
            ? query
            : ordered.ThenBy(entity => entity.Id);

    /// <summary>Determines whether the input uses offset-style pagination requiring a stable ordering.</summary>
    protected virtual bool ShouldApplyStablePaginationSorting(TQuery input)
        => input is IHasRequestLimitedResult and not IHasRequestPage { DisablePage: true }
            && (input is not IHasRequestKeysetPage request || !request.HasUsingKeyset());

    /// <summary>Determines whether the explicit ordering contains the entity key.</summary>
    protected virtual bool ContainsEntityIdSorting(string sorting)
    {
        foreach (var segment in sorting.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var tokens = segment.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (tokens.Length > 0 && string.Equals(tokens[0], nameof(IEntity<TKey>.Id), StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Orders by descending creation time when available, followed by descending entity key.</summary>
    protected virtual IQueryable<TEntity> ApplyDefaultSorting(IQueryable<TEntity> query)
    {
        if (query.HasBeenOrdered(out var ordered))
        {
            return typeof(TEntity).IsAssignableTo<IHasCreationTime>()
                ? ordered.ThenByDescending(entity => ((IHasCreationTime)entity).CreationTime).ThenByDescending(entity => entity.Id)
                : ordered.ThenByDescending(entity => entity.Id);
        }

        return typeof(TEntity).IsAssignableTo<IHasCreationTime>()
            ? query.OrderByDescending(entity => ((IHasCreationTime)entity).CreationTime).ThenByDescending(entity => entity.Id)
            : query.OrderByDescending(entity => entity.Id);
    }

    /// <summary>Applies the existing descending key comparison for cursor continuation.</summary>
    protected virtual IQueryable<TEntity> FilterByKeyset(IQueryable<TEntity> query, string cursor)
    {
        if (string.IsNullOrEmpty(cursor)) return query;

        var parameter = Expression.Parameter(typeof(TEntity), "entity");
        var key = Expression.PropertyOrField(parameter, nameof(IEntity<TKey>.Id));
        var cursorValue = typeof(TKey) == typeof(Guid)
            ? (TKey)(object)Guid.Parse(cursor)
            : (TKey)Convert.ChangeType(cursor, typeof(TKey));
        var constant = Expression.Constant(cursorValue, typeof(TKey));

        var lessThan = typeof(TKey) == typeof(Guid)
            ? Expression.LessThan(Expression.Call(key, typeof(Guid).GetMethod(nameof(Guid.CompareTo), [typeof(Guid)])!, constant),
                Expression.Constant(0))
            : Expression.LessThan(key, constant);
        return query.Where(Expression.Lambda<Func<TEntity, bool>>(lessThan, parameter));
    }

    private static (int? Offset, int? Limit, int? CurrentPage, int? PageSize) GetPagingWindow(TQuery input)
    {
        if (input is not IHasRequestLimitedResult limit || limit is IHasRequestPage { DisablePage: true })
        {
            return (null, null, null, null);
        }

        int? currentPage = null;
        int? pageSize = null;
        if (limit is IHasRequestPage page)
        {
            if (page.SkipCount == default || page.Page is not null)
            {
                page.Page ??= 1;
                page.SkipCount = (page.Page.Value - 1) * limit.MaxResultCount;
            }

            currentPage = page.Page;
            pageSize = limit.MaxResultCount;
        }

        var keyset = limit is IHasRequestKeysetPage request && request.HasUsingKeyset();
        int? offset = null;
        if (!keyset && limit is IHasRequestSkipCount skip)
        {
            offset = skip.SkipCount ?? 0;
        }

        return (offset, limit.MaxResultCount, currentPage, pageSize);
    }

    private static bool ShouldSkipEntityCount(TQuery input)
        => input is IHasRequestFeature feature && feature.GetSetting()?.ShouldJumpCount() is true;

    private void ApplyHostPagingDefaults(TQuery input)
    {
        if (input is LimitedResultRequestDto request)
        {
            var pagination = CachedServiceProvider.GetRequiredService<IOptions<CrudControllerOption>>().Value.Pagination;
            request.ApplyDefaults(pagination);
        }
    }

    /// <summary>Gets the display name used in standard responses. Defaults to an unnamed resource.</summary>
    protected virtual string? EntityName => null;

    /// <summary>Creates the standard missing-entity failure.</summary>
    protected virtual Result ResEntityNotFound(string entityId)
        => Result.Failure(EntityName is { } name ? $"{name} was not found." : "The requested data was not found.");

    /// <summary>Creates the standard update acknowledgement for the entity key.</summary>
    protected virtual Result ResEntityUpdateSuccess(string entityId)
        => Result.Success().WithMessage(EntityName is { } name ? $"{name} updated successfully" : $"Updated successfully: {entityId}");

    /// <summary>Creates the standard update failure.</summary>
    protected virtual Result ResEntityUpdateFailed(string entityId)
        => Result.Failure(EntityName is { } name ? $"{name} update failed." : "Update failed.");

    /// <summary>Creates the standard creation acknowledgement for the saved entity key.</summary>
    protected virtual Result ResEntityCreateSuccess(string entityId)
        => Result.Success().WithMessage(EntityName is { } name ? $"{name} created successfully: {entityId}" : $"Created successfully: {entityId}");

    /// <summary>Creates the standard creation failure.</summary>
    protected virtual Result ResEntityCreateFailed()
        => Result.Failure(EntityName is { } name ? $"{name} creation failed." : "Creation failed.");

    /// <summary>Creates the standard removal acknowledgement for one key or a list of keys.</summary>
    protected virtual Result ResEntityDeleteSuccess(string id)
        => Result.Success().WithMessage(EntityName is { } name ? $"{name} deleted successfully: {id}" : $"Deleted successfully: {id}");

    /// <summary>Creates the standard removal failure.</summary>
    protected virtual Result ResEntityDeleteFailed()
        => Result.Failure(EntityName is { } name ? $"{name} deletion failed." : "Deletion failed.");
}
