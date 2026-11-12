using Microsoft.AspNetCore.Mvc;
using Monica.Core.Results;
using Monica.Core.Execution;
using Monica.Repository.Entity.Abstractions;
using Monica.Repository.Persistence.Abstractions;
using Monica.Repository.Persistence.Exceptions;
using Monica.Repository.Persistence.Extensions;
using Monica.WebApi.Annotations;
using Monica.WebApi.AutoControllers.Abstractions;
using Monica.WebApi.AutoControllers.Models;

namespace Monica.WebApi.AutoControllers.Services;

/// <summary>
/// <inheritdoc/>
/// <para>
/// Simplified variant: create and update operations are disabled.
/// Implement <see cref="ICrudDisableDelete"/> as well to disable delete operations.
/// </para>
/// </summary>
/// <param name="repository">The repository used for persistence operations.</param>
public abstract class CrudApplicationService<TEntity, TEntityDto, TKey, TGetListInput, TRepository>(
    TRepository repository)
    : CrudApplicationService<TEntity, TEntityDto, TEntityDto, TKey, TGetListInput, CrudDisableDto, CrudDisableDto,
        CrudDisableDto, TRepository>(repository)
    where TEntity : class, IEntity<TKey>
    where TEntityDto : IEntityDto<TKey>
    where TRepository : IEfEntityStore<TEntity, TKey>
{
}

/// <summary>
/// <inheritdoc/>
/// <para>
/// Simplified variant: bulk delete is not generated, the single-item DTO matches the list-item DTO,
/// and the default paged request DTO is used.
/// </para>
/// </summary>
/// <param name="repository">The repository used for persistence operations.</param>
public abstract class CrudApplicationService<TEntity, TEntityDto, TKey, TCreateInput, TUpdateInput, TRepository>(
    TRepository repository)
    : CrudApplicationService<TEntity, TEntityDto, TEntityDto, TKey, CrudPageRequestDto, TCreateInput, TUpdateInput,
        CrudDisableDto, TRepository>(repository)
    where TEntity : class, IEntity<TKey>
    where TEntityDto : IEntityDto<TKey>
    where TRepository : IEfEntityStore<TEntity, TKey>
{
}

/// <summary>
/// <inheritdoc/>
/// <para>
/// Simplified variant: bulk delete is not generated, and the single-item DTO matches the list-item DTO.
/// </para>
/// </summary>
/// <param name="repository">The repository used for persistence operations.</param>
public abstract class CrudApplicationService<TEntity, TEntityDto, TKey, TGetListInput, TCreateInput, TUpdateInput,
    TRepository>(
        TRepository repository)
    : CrudApplicationService<TEntity, TEntityDto, TEntityDto, TKey, TGetListInput, TCreateInput, TUpdateInput, CrudDisableDto, TRepository>(
        repository)
    where TEntity : class, IEntity<TKey>
    where TEntityDto : IEntityDto<TKey>
    where TRepository : IEfEntityStore<TEntity, TKey>
{
}



/// <summary>
/// Base class for auto-generated CRUD application services.
/// Subclasses must end with <see cref="CrudControllerOption.CrudControllerPostfix"/> to be registered automatically.
/// The remaining class name prefix is converted into a kebab-case route segment, for example:
/// <c>UserListAppService</c> becomes <c>user-list</c>.
/// </summary>
/// <typeparam name="TEntity">The entity type. Must implement <see cref="IEntity"/>.</typeparam>
/// <typeparam name="TGetOutputDto">The DTO type returned by single-entity queries. Must implement <see cref="IEntityDto"/>.</typeparam>
/// <typeparam name="TGetListOutputDto">The DTO type returned by list queries. Must implement <see cref="IEntityDto"/>.</typeparam>
/// <typeparam name="TKey">The entity primary key type.</typeparam>
/// <typeparam name="TGetListInput">The input type used for list queries.</typeparam>
/// <typeparam name="TCreateInput">The input type used for create operations.</typeparam>
/// <typeparam name="TUpdateInput">The input type used for update operations.</typeparam>
/// <typeparam name="TBulkDeleteInput">The input type used for bulk delete operations.</typeparam>
/// <typeparam name="TRepository">The repository type. Must implement <see cref="IRepository{TEntity}"/>.</typeparam>
/// <param name="repository">The repository instance.</param>
public abstract class CrudApplicationService<TEntity, TGetOutputDto, TGetListOutputDto, TKey, TGetListInput, TCreateInput,
    TUpdateInput, TBulkDeleteInput, TRepository>(
        TRepository repository) :
    AbstractKeyCrudApplicationService<TEntity, TGetOutputDto, TGetListOutputDto, TKey, TGetListInput, TCreateInput, TUpdateInput>(
        repository), ICrudApplicationService
    where TEntity : class, IEntity<TKey>
    where TGetOutputDto : IEntityDto<TKey>
    where TGetListOutputDto : IEntityDto<TKey>
    where TRepository : IEfEntityStore<TEntity, TKey>
{
    /// <summary>
    /// Creates an entity.
    /// </summary>
    /// <param name="input">The input used to create the entity.</param>
    /// <returns>A standardized success response for the created entity.</returns>
    /// <remarks>Overrides the base method to return the standardized response format.</remarks>
    [OverrideService(-999)]
    public new virtual async Task<Result> CreateAsync(TCreateInput input)
    {
        var dto = await base.CreateAsync(input);
        return ResEntityCreateSuccess(dto);
    }

    /// <summary>
    /// Deletes the entity with the specified ID.
    /// </summary>
    /// <param name="id">The ID of the entity to delete.</param>
    /// <returns>A standardized success response for the deleted entity.</returns>
    /// <remarks>Overrides the base method to return the standardized response format.</remarks>
    [OverrideService(-999)]
    public new virtual async Task<Result> DeleteAsync(TKey id)
    {
        await base.DeleteAsync(id);
        return ResEntityDeleteSuccess(id?.ToString() ?? "");
    }

    /// <summary>
    /// Deletes multiple entities.
    /// </summary>
    /// <param name="input">The input containing the IDs of the entities to delete.</param>
    /// <returns>A standardized response for the bulk delete operation.</returns>
    /// <remarks>Bulk deletion is executed only when the input implements <see cref="IHasRequestIds{TKey}"/>.</remarks>
    public virtual async Task<Result> BulkDeleteAsync(TBulkDeleteInput input)
    {
        if (input is IHasRequestIds<TKey> keys)
        {
            await repository.RemoveByIdsAsync(keys.Ids);
            return ResEntityDeleteSuccess(string.Join(",", keys.Ids));
        }

        return ResEntityDeleteFailed();
    }

    /// <summary>
    /// Updates the entity with the specified ID.
    /// </summary>
    /// <param name="id">The ID of the entity to update.</param>
    /// <param name="input">The input used to update the entity.</param>
    /// <returns>A standardized success response for the updated entity.</returns>
    /// <remarks>
    /// Overrides the base method to return the standardized response format.
    /// Guideline: <typeparamref name="TUpdateInput"/> and <typeparamref name="TCreateInput"/> should not inherit from entity-style base classes.
    /// </remarks>
    [OverrideService(-999)]
    public new virtual async Task<Result> UpdateAsync(TKey id, TUpdateInput input)
    {
        // Guideline: TUpdateInput and TCreateInput should not inherit from entity-style base classes.
        var dto = await base.UpdateAsync(id, input);
        return ResEntityUpdateSuccess(dto);
    }

    /// <summary>
    /// The generated <c>{id}</c> route token depends on the method parameter being named <c>id</c>.
    /// </summary>
    /// <param name="id">The entity ID.</param>
    /// <returns>The standardized response that wraps the requested entity.</returns>
    [OverrideService(-999)]
    [ExecutionTransaction(ExecutionTransactionMode.None)]
    public new virtual async Task<Result<TGetOutputDto>> GetAsync(TKey id)
    {
        try
        {
            return Result.Success(await base.GetAsync(id));
        }
        catch (EntityNotFoundException)
        {
            return Result.FromFailure<TGetOutputDto>(ResEntityNotFound(id!.ToString()!));
        }
    }

    // TODO: Remove this feature or move it elsewhere.
    // TODO: When overriding a method with a different signature, add the POST attribute explicitly because it is not inherited.
    [HttpPost]
    [ExecutionTransaction(ExecutionTransactionMode.None)]
    public virtual async Task<PagedResult<dynamic>> ListAsync(TGetListInput input)
    {
        return await GetListAsync(input);
    }
    #region Template Responses
    /// <summary>
    /// Entity display name used when composing template responses.
    /// </summary>
    protected virtual string? EntityName => null;
    /// <summary>
    /// Creates the standard response for a missing entity.
    /// </summary>
    /// <param name="entityId">The identifier that could not be found.</param>
    /// <returns>A standardized not-found response.</returns>
    protected virtual Result ResEntityNotFound(string entityId)
    {
        if (EntityName is { } name)
        {
            return Result.Failure($"{name} was not found.");
        }
        return Result.Failure("The requested data was not found.");
    }
    /// <summary>
    /// Creates the standard success response after an entity update.
    /// </summary>
    /// <param name="dto">The updated DTO.</param>
    /// <returns>A standardized success response.</returns>
    protected virtual Result ResEntityUpdateSuccess(TGetOutputDto dto)
    {
        if (EntityName is { } name)
        {
            return Result.Success().WithMessage($"{name} updated successfully");
        }
        return Result.Success().WithMessage($"Updated successfully: {dto.Id}");
    }
    /// <summary>
    /// Creates the standard success response after an entity update.
    /// </summary>
    /// <param name="entityId">The identifier of the updated entity.</param>
    /// <returns>A standardized success response.</returns>
    protected virtual Result ResEntityUpdateSuccess(string entityId)
    {
        if (EntityName is { } name)
        {
            return Result.Success().WithMessage($"{name} updated successfully");
        }
        return Result.Success().WithMessage($"Updated successfully: {entityId}");
    }
    /// <summary>
    /// Creates the standard failure response for an entity update.
    /// </summary>
    /// <param name="entityId">The identifier of the entity that failed to update.</param>
    /// <returns>A standardized failure response.</returns>
    protected virtual Result ResEntityUpdateFailed(string entityId)
    {
        if (EntityName is { } name)
        {
            return Result.Failure($"{name} update failed.");
        }
        return Result.Failure("Update failed.");
    }
    /// <summary>
    /// Creates the standard success response after an entity is created.
    /// </summary>
    /// <param name="dto">The created DTO.</param>
    /// <returns>A standardized success response.</returns>
    protected virtual Result ResEntityCreateSuccess(TGetOutputDto dto)
    {
        if (EntityName is { } name)
        {
            return Result.Success().WithMessage($"{name} created successfully: {dto.Id}");
        }
        return Result.Success().WithMessage($"Created successfully: {dto.Id}");
    }
    /// <summary>
    /// Creates the standard success response after an entity is created.
    /// </summary>
    /// <param name="entityId">The identifier of the created entity.</param>
    /// <returns>A standardized success response.</returns>
    protected virtual Result ResEntityCreateSuccess(string entityId)
    {
        if (EntityName is { } name)
        {
            return Result.Success().WithMessage($"{name} created successfully: {entityId}");
        }
        return Result.Success().WithMessage($"Created successfully: {entityId}");
    }
    /// <summary>
    /// Creates the standard failure response for entity creation.
    /// </summary>
    /// <returns>A standardized failure response.</returns>
    protected virtual Result ResEntityCreateFailed()
    {
        if (EntityName is { } name)
        {
            return Result.Failure($"{name} creation failed.");
        }
        return Result.Failure("Creation failed.");
    }
    /// <summary>
    /// Creates the standard success response after an entity is deleted.
    /// </summary>
    /// <param name="id">The identifier of the deleted entity.</param>
    /// <returns>A standardized success response.</returns>
    protected virtual Result ResEntityDeleteSuccess(string id)
    {
        if (EntityName is { } name)
        {
            return Result.Success().WithMessage($"{name} deleted successfully: {id}");
        }
        return Result.Success().WithMessage($"Deleted successfully: {id}");
    }
    /// <summary>
    /// Creates the standard failure response for entity deletion.
    /// </summary>
    /// <returns>A standardized failure response.</returns>
    protected virtual Result ResEntityDeleteFailed()
    {
        if (EntityName is { } name)
        {
            return Result.Failure($"{name} deletion failed.");
        }
        return Result.Failure("Deletion failed.");
    }
    #endregion
}
