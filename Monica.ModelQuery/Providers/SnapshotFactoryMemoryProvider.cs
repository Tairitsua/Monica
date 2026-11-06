using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;
using Monica.ModelQuery.Abstractions;
using Monica.ModelQuery.Models;

namespace Monica.ModelQuery.Providers;

internal sealed class SnapshotFactoryMemoryProvider(IServiceProvider serviceProvider) : IModelQuerySnapshotFactory
{
    private readonly ConcurrentDictionary<Type, ModelQuerySnapshot> _snapshots = [];

    public ModelQuerySnapshot PreloadSnapshot<TModel>()
    {
        _ = serviceProvider.GetRequiredService<IModelQuerySnapshot<TModel>>();
        return _snapshots.TryGetValue(typeof(TModel), out var snapshot)
            ? snapshot
            : throw new InvalidOperationException(
                $"ModelQuery snapshot activation completed without registering {typeof(TModel).FullName}.");
    }

    public void Register<TModel>(ModelQuerySnapshot snapshot)
    {
        if (!_snapshots.TryAdd(typeof(TModel), snapshot))
        {
            throw new InvalidOperationException($"An ModelQuery snapshot for {typeof(TModel).FullName} is already registered in this host.");
        }
    }

    public IReadOnlyList<ModelQuerySnapshot> GetSnapshots()
    {
        return _snapshots.Values
            .OrderBy(snapshot => snapshot.Model.FullTypeName, StringComparer.Ordinal)
            .ToArray();
    }
}
