using Microsoft.Extensions.DependencyInjection;
using Monica.DependencyInjection.Abstractions;

namespace Monica.Testing.BuildingBlocks;

/// <summary>
/// Owns a scoped Building Block created by a raw fast-path test container.
/// </summary>
/// <typeparam name="TBuildingBlock">The Building Block implementation under test.</typeparam>
public sealed class BuildingBlockFixture<TBuildingBlock> : IDisposable, IAsyncDisposable
    where TBuildingBlock : class
{
    private readonly ServiceProvider _rootProvider;
    private readonly AsyncServiceScope _scope;
    private bool _disposed;

    internal BuildingBlockFixture(
        ServiceProvider rootProvider,
        AsyncServiceScope scope,
        TBuildingBlock unit,
        ICachedServiceProvider cachedServiceProvider)
    {
        _rootProvider = rootProvider;
        _scope = scope;
        BuildingBlock = unit;
        Services = scope.ServiceProvider;
        CachedServiceProvider = cachedServiceProvider;
    }

    /// <summary>
    /// Gets the Building Block instance under test.
    /// </summary>
    public TBuildingBlock BuildingBlock { get; }

    /// <summary>
    /// Gets the scoped service provider that created the Building Block instance.
    /// </summary>
    public IServiceProvider Services { get; }

    /// <summary>
    /// Gets the Monica cached service provider assigned to the building block when the building block supports it.
    /// </summary>
    public ICachedServiceProvider CachedServiceProvider { get; }

    /// <summary>
    /// Creates a builder for an arbitrary Building Block.
    /// </summary>
    public static BuildingBlockFixtureBuilder<TBuildingBlock> Builder() => new();

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        DisposeAsync().AsTask().GetAwaiter().GetResult();
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        await _scope.DisposeAsync();
        await _rootProvider.DisposeAsync();
    }
}
