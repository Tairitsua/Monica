using Monica.Repository.Persistence.Services;
using Monica.Repository.Snapshots.Abstractions;
using Monica.Repository.Snapshots.Services;

namespace Monica.Repository.Snapshots.Models;

/// <summary>Builds a stable dataset identity and conservative type-level invalidation dependencies.</summary>
public sealed class RepositoryDatasetBuilder<TContext> where TContext : RepositoryDbContext<TContext>
{
    internal string? Key { get; private set; }
    internal HashSet<Type> Dependencies { get; } = [];
    /// <summary>Sets the stable name (at most 200 characters).</summary>
    public RepositoryDatasetBuilder<TContext> Dataset(string key)
    {
        SnapshotNames.Validate(key, nameof(key));
        if (Key is not null) throw new InvalidOperationException("A dataset may be named once.");
        Key = key;
        return this;
    }
    /// <summary>Declares a root, child, relationship, or owned type. Include dependencies changed by cascades and native writes.</summary>
    public RepositoryDatasetBuilder<TContext> DependsOn<TEntity>() where TEntity : class
    {
        Dependencies.Add(typeof(TEntity));
        return this;
    }
}

/// <summary>Separates a projection's result identity from its dataset's SQL revision.</summary>
public sealed class RepositorySnapshotBuilder<TContext> where TContext : RepositoryDbContext<TContext>
{
    internal Type? DatasetType { get; private set; }
    internal string? Name { get; private set; }
    internal int AlgorithmVersion { get; private set; }
    internal int FormatVersion { get; private set; }
    /// <summary>References a dataset registered in the same context/source.</summary>
    public RepositorySnapshotBuilder<TContext> Dataset<TDefinition>() where TDefinition : RepositoryDatasetDefinition<TContext>
    {
        if (DatasetType is not null) throw new InvalidOperationException("A projection must reference one dataset.");
        DatasetType = typeof(TDefinition);
        return this;
    }
    /// <summary>Names a projection and its positive code versions. Increment them when semantics or representation changes.</summary>
    public RepositorySnapshotBuilder<TContext> Projection(string name, int algorithmVersion, int formatVersion)
    {
        SnapshotNames.Validate(name, nameof(name));
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(algorithmVersion);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(formatVersion);
        if (Name is not null) throw new InvalidOperationException("A projection may be named once.");
        Name = name;
        AlgorithmVersion = algorithmVersion;
        FormatVersion = formatVersion;
        return this;
    }
}

/// <summary>Host/source configuration. Frozen during registration; do not retain or mutate it afterwards.</summary>
public sealed class RepositorySnapshotOptions<TContext> where TContext : RepositoryDbContext<TContext>
{
    /// <summary>Required stable source identity (at most 200 characters), shared by replicas and isolated across databases/tenants.</summary>
    public string SourceId { get; set; } = string.Empty;
    /// <summary>False by default. Set true when another context owns this physical version table's migration.</summary>
    public bool ExcludeFromMigrations { get; set; }
    /// <summary>Maximum retained projection/input entries per context/source. Default 128; this bounds entries, not object bytes.</summary>
    public int MaximumCachedEntries { get; set; } = 128;
    /// <summary>Maximum concurrent constructions per context/source. Default 4.</summary>
    public int MaximumConcurrentBuilds { get; set; } = 4;
    /// <summary>Maximum distinct active and queued builds. Default 64; excess reads fail without unbounded queuing.</summary>
    public int MaximumPendingBuilds { get; set; } = 64;
    /// <summary>Cooperative deadline including queue time. Default 30 seconds; loaders must honor cancellation.</summary>
    public TimeSpan BuildTimeout { get; set; } = TimeSpan.FromSeconds(30);
    /// <summary>Maximum attempts after a concurrent source change. Default 3; never retries a business write.</summary>
    public int MaximumBuildAttempts { get; set; } = 3;
    internal Dictionary<Type, SnapshotDataset> Datasets { get; } = [];
    internal List<RepositorySnapshotDefinition<TContext>> Projections { get; } = [];
    /// <summary>Registers one source boundary. All writers must register it even if they register no projections.</summary>
    public void AddDataset<TDefinition>() where TDefinition : RepositoryDatasetDefinition<TContext>, new()
    {
        var builder = new RepositoryDatasetBuilder<TContext>();
        new TDefinition().Configure(builder);
        if (builder.Key is null || builder.Dependencies.Count == 0)
            throw new InvalidOperationException("A dataset requires a name and at least one entity dependency.");
        if (Datasets.Values.Any(x => x.Key == builder.Key) || !Datasets.TryAdd(typeof(TDefinition), new(builder.Key, builder.Dependencies.ToArray())))
            throw new InvalidOperationException($"Dataset '{builder.Key}' is already registered.");
    }
    /// <summary>Registers a stateless parameterless definition producing one unique read-model type.</summary>
    public void AddProjection<TDefinition>() where TDefinition : RepositorySnapshotDefinition<TContext>, new()
        => Projections.Add(new TDefinition());
}
