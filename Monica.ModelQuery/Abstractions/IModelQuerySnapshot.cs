using Monica.ModelQuery.Models;

namespace Monica.ModelQuery.Abstractions;

/// <summary>
/// Provides access to registered ModelQuery snapshots.
/// </summary>
public interface IModelQuerySnapshotFactory
{
    /// <summary>
    /// Builds and registers the snapshot for <typeparamref name="TModel"/> if it has not been activated yet.
    /// </summary>
    /// <typeparam name="TModel">The model type whose metadata must be available.</typeparam>
    /// <returns>The host-owned snapshot for <typeparamref name="TModel"/>.</returns>
    /// <remarks>
    /// Use this operation when startup work must validate ModelQuery metadata eagerly. Ordinary consumers can
    /// continue resolving <see cref="IModelQuerySnapshot{TModel}"/> lazily.
    /// </remarks>
    ModelQuerySnapshot PreloadSnapshot<TModel>();

    /// <summary>
    /// Gets all registered ModelQuery snapshots.
    /// </summary>
    /// <returns>All registered snapshots.</returns>
    IReadOnlyList<ModelQuerySnapshot> GetSnapshots();
}

/// <summary>
/// Represents a strongly typed ModelQuery snapshot.
/// </summary>
/// <typeparam name="TModel">The model type.</typeparam>
public interface IModelQuerySnapshot<TModel>
{
    /// <summary>
    /// Gets all query names exposed by the snapshot fields.
    /// </summary>
    /// <returns>All supported query names.</returns>
    IReadOnlyList<string> GetAllQueryNames();

    /// <summary>
    /// Gets field metadata for the specified query name using an ordinal case-insensitive comparison.
    /// </summary>
    /// <param name="fieldQueryName">The query name of the field.</param>
    /// <returns>The matching field metadata, or <c>null</c> if no match exists.</returns>
    QueryField? GetField(string fieldQueryName);

    /// <summary>
    /// Gets field metadata.
    /// </summary>
    /// <param name="fieldQueryNames">Optional query names used to filter the result.</param>
    /// <returns>The matching field metadata.</returns>
    IReadOnlyList<QueryField> GetFields(IReadOnlyList<string>? fieldQueryNames = null);
}
