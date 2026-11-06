using System.Collections.Frozen;
using System.Text.Json.Serialization;

namespace Monica.BuildingBlocks.CodeAnalysis.Models;

/// <summary>Stable semantic-analysis contract version used by persisted consumers.</summary>
public static class BuildingBlockSourceAnalysisContract
{
    /// <summary>
    /// Version 1 includes declared member structure, documentation and source provenance.
    /// </summary>
    public const string Version = "monica-building-blocks-source/v1";

    /// <summary>Building-block kinds the current source classifier can produce.</summary>
    public static IReadOnlySet<SourceBuildingBlockKind> DiscoverableKinds { get; } = new[]
    {
        SourceBuildingBlockKind.ApplicationRequestHandler,
        SourceBuildingBlockKind.CrudApplicationService,
        SourceBuildingBlockKind.DomainService,
        SourceBuildingBlockKind.Repository,
        SourceBuildingBlockKind.DomainEvent,
        SourceBuildingBlockKind.DistributedEventHandler,
        SourceBuildingBlockKind.LocalEventHandler,
        SourceBuildingBlockKind.Seeder,
        SourceBuildingBlockKind.RecurringJob,
        SourceBuildingBlockKind.TriggeredJob,
        SourceBuildingBlockKind.HttpApi,
        SourceBuildingBlockKind.Entity,
        SourceBuildingBlockKind.RequestDto,
        SourceBuildingBlockKind.Configuration,
        SourceBuildingBlockKind.HostedService
    }.ToFrozenSet();
}

/// <summary>
/// Identifies the kind of a building block discovered through source analysis.
/// </summary>
/// <remarks>
/// Numeric values are part of the persisted source-analysis contract. New kinds must be appended so cached Workflow
/// snapshots remain stable without depending on the runtime Building Blocks assembly.
/// Values 12 through 15 are reserved and are not currently discoverable.
/// </remarks>
public enum SourceBuildingBlockKind
{
    /// <summary>No kind has been assigned.</summary>
    None = 0,

    /// <summary>Application handler for one mediator request.</summary>
    ApplicationRequestHandler = 1,

    /// <summary>CRUD application service that participates in automatic controller generation.</summary>
    CrudApplicationService = 2,

    /// <summary>Domain service.</summary>
    DomainService = 3,

    /// <summary>Repository.</summary>
    Repository = 4,

    /// <summary>Domain event.</summary>
    DomainEvent = 5,

    /// <summary>Distributed event handler, including explicit batch consumers.</summary>
    DistributedEventHandler = 6,

    /// <summary>Local event handler.</summary>
    LocalEventHandler = 7,

    /// <summary>Startup data seeder.</summary>
    Seeder = 8,

    /// <summary>Recurring scheduled job.</summary>
    RecurringJob = 9,

    /// <summary>Triggered job.</summary>
    TriggeredJob = 10,

    /// <summary>HTTP API.</summary>
    HttpApi = 11,

    /// <summary>gRPC API.</summary>
    GrpcApi = 12,

    /// <summary>State store.</summary>
    StateStore = 13,

    /// <summary>Event bus.</summary>
    EventBus = 14,

    /// <summary>Actor.</summary>
    Actor = 15,

    /// <summary>Entity or aggregate.</summary>
    Entity = 16,

    /// <summary>Request DTO.</summary>
    RequestDto = 17,

    /// <summary>Configuration model.</summary>
    Configuration = 18,

    /// <summary>Host-managed long-running or lifecycle service.</summary>
    HostedService = 19
}

/// <summary>Severity assigned to a source-analysis diagnostic.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum BuildingBlockSourceDiagnosticSeverity
{
    /// <summary>Informational analysis context.</summary>
    Information,

    /// <summary>Analysis completed but discovered a condition requiring attention.</summary>
    Warning,

    /// <summary>A project or artifact could not be analyzed reliably.</summary>
    Error
}

/// <summary>Current phase of a source-level Building Block analysis.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum BuildingBlockSourceAnalysisStage
{
    /// <summary>The analyzer is validating input and locating MSBuild.</summary>
    Initializing,

    /// <summary>MSBuild projects are being loaded.</summary>
    LoadingProjects,

    /// <summary>Project compilations and declared source types are being analyzed.</summary>
    AnalyzingProjects,

    /// <summary>Cross-building block dependency references are being resolved.</summary>
    ResolvingDependencies,

    /// <summary>The source catalog is complete.</summary>
    Completed,

    /// <summary>Selected projects and their referenced build inputs are being observed.</summary>
    EvaluatingInputs
}

/// <summary>Workspace and project input for one source analysis.</summary>
/// <remarks>
/// Project paths are analyzed for Building Blocks; test project paths are analyzed only for test classes —
/// they contribute no building blocks of their own. Supply SolutionPath for solution-backed workspaces so the
/// selected projects can be loaded together through a temporary solution filter. The original solution
/// supplies SolutionDir; unselected projects contribute only when referenced by the selected graph.
/// Selected projects absent from that solution are loaded individually in the same workspace.
/// </remarks>
public sealed record BuildingBlockSourceAnalysisRequest(
    string WorkspaceRoot,
    IReadOnlyList<string> ProjectPaths,
    IReadOnlyList<string>? TestProjectPaths = null,
    string? SolutionPath = null);

/// <summary>Progress reported while loading and analyzing projects.</summary>
public sealed record BuildingBlockSourceAnalysisProgress(
    BuildingBlockSourceAnalysisStage Stage,
    int Completed,
    int Total,
    decimal? Percentage,
    string? CurrentProject,
    string Message);

/// <summary>Source position for one declared Building Block.</summary>
public sealed record BuildingBlockSourceLocation(
    string RelativePath,
    int Line,
    int Column);

/// <summary>Actionable diagnostic produced while loading projects or interpreting Building Block contracts.</summary>
public sealed record BuildingBlockSourceDiagnostic(
    string Code,
    BuildingBlockSourceDiagnosticSeverity Severity,
    string Message,
    string? ProjectPath = null,
    string? SourcePath = null,
    int? Line = null);

/// <summary>A semantically discovered source Building Block and its architecture context.</summary>
public sealed record SourceBuildingBlock(
    string CatalogKey,
    string RuntimeKey,
    string ProjectPath,
    string ProjectName,
    string AssemblyName,
    string Namespace,
    string Name,
    SourceBuildingBlockKind Kind,
    string Title,
    string? Description,
    string? Owner,
    IReadOnlyList<string> Tags,
    IReadOnlyList<string> RequirementIds,
    bool HasExplicitMetadata,
    BuildingBlockSourceLocation Source,
    IReadOnlyList<string> ExecutionPoints,
    IReadOnlyList<string> Dependencies,
    IReadOnlyList<string> DependedBy,
    IReadOnlyList<BuildingBlockSourceDiagnostic> Diagnostics)
{
    /// <summary>Declared outgoing symbols, including targets outside this analysis scope.</summary>
    public IReadOnlyList<BuildingBlockSourceReference> OutgoingReferences { get; init; } = [];

    /// <summary>Readable properties, including inherited members; these are not an inferred database schema.</summary>
    public IReadOnlyList<BuildingBlockSourceProperty> Properties { get; init; } = [];

    /// <summary>Public or protected behavior declared by this building block, excluding generated members.</summary>
    public IReadOnlyList<BuildingBlockSourceOperation> Operations { get; init; } = [];
}

/// <summary>One property observed through Roslyn. Null IsNullable means the source does not declare nullability.</summary>
public sealed record BuildingBlockSourceProperty(
    string Name, string Type, bool? IsNullable, bool IsRequired, string? Setter,
    string DeclaringType, bool IsInherited, string? Description,
    IReadOnlyList<string> Attributes, BuildingBlockSourceLocation? Source);

/// <summary>A declared constructor or method signature and its authored documentation.</summary>
public sealed record BuildingBlockSourceOperation(
    string Name, string Signature, string? Description, BuildingBlockSourceLocation? Source);

/// <summary>A scope-independent outgoing type reference. MissingDiagnosticCode identifies required building block associations.</summary>
public sealed record BuildingBlockSourceReference(string AssemblyName, string RuntimeKey, string? MissingDiagnosticCode = null);

/// <summary>One key/value pair declared through a trait attribute on a test class or one of its test methods.</summary>
public sealed record BuildingBlockSourceTestTrait(string Key, string Value)
{
    /// <summary>Location of the declaration, when available.</summary>
    public BuildingBlockSourceLocation? Source { get; init; }
}

/// <summary>One declared test method and only its method-level traits; class traits are inherited by consumers.</summary>
public sealed record BuildingBlockSourceTestMethod(string Name, BuildingBlockSourceLocation Source, IReadOnlyList<BuildingBlockSourceTestTrait> Traits);

/// <summary>
/// A concrete test class declared in a test project: it owns at least one Fact/Theory test method and
/// retains class traits separately from each method's declarations.
/// </summary>
public sealed record BuildingBlockSourceTestClass(
    string RuntimeKey,
    string ProjectPath,
    string ProjectName,
    string Namespace,
    string Name,
    BuildingBlockSourceLocation Source,
    int TestMethodCount,
    IReadOnlyList<BuildingBlockSourceTestTrait> Traits)
{
    /// <summary>Individual test methods, preserving trait pairing and source locations.</summary>
    public IReadOnlyList<BuildingBlockSourceTestMethod> Methods { get; init; } = [];
}

/// <summary>Serializable result of a multi-project semantic Building Block analysis.</summary>
public sealed record BuildingBlockSourceCatalog(
    string ContractVersion,
    int RequestedProjectCount,
    int AnalyzedProjectCount,
    bool IsPartial,
    DateTimeOffset CompletedAtUtc,
    IReadOnlyList<SourceBuildingBlock> BuildingBlocks,
    IReadOnlyList<BuildingBlockSourceTestClass> TestClasses,
    IReadOnlyList<BuildingBlockSourceDiagnostic> Diagnostics)
{
    /// <summary>Absolute evaluated imports, source, metadata and analyzer inputs for each requested project and its project-reference closure.</summary>
    public IReadOnlyDictionary<string, IReadOnlyList<string>> InputPaths { get; init; } = new Dictionary<string, IReadOnlyList<string>>();
    /// <summary>MSBuild global and target selection properties for the evaluated contexts of each project, keyed by absolute project path.</summary>
    public IReadOnlyDictionary<string, IReadOnlyList<IReadOnlyDictionary<string, string>>> EvaluationContexts { get; init; } = new Dictionary<string, IReadOnlyList<IReadOnlyDictionary<string, string>>>();
    /// <summary>Whether evaluated input paths or their file stamps moved during semantic analysis.</summary>
    public bool InputsChangedDuringAnalysis { get; init; }
    /// <summary>Ending file/directory stamps, allowing consumers to detect edits before publishing the observation.</summary>
    public IReadOnlyList<BuildingBlockSourceInput> InputObservations { get; init; } = [];
}

/// <summary>One evaluated input stamp. It is a freshness heuristic, not a content digest.</summary>
public sealed record BuildingBlockSourceInput(string Path, bool Exists, bool IsDirectory, long Length, long LastWriteTimeUtcTicks)
{
    /// <summary>Direct entry names for source directories, excluding disposable build output directories.</summary>
    public string? DirectoryEntries { get; init; }
    /// <summary>Observes the current file or directory at an absolute input path.</summary>
    public static BuildingBlockSourceInput Observe(string path)
    {
        if (Directory.Exists(path)) return new(path, true, true, 0, 0)
        {
            DirectoryEntries = string.Join('\n', Directory.EnumerateFileSystemEntries(path)
                .Select(System.IO.Path.GetFileName).Where(name => name is not null && !IgnoredDirectoryNames.Contains(name))
                .Order(StringComparer.OrdinalIgnoreCase))
        };
        var info = new FileInfo(path);
        return info.Exists ? new(path, true, false, info.Length, info.LastWriteTimeUtc.Ticks) : new(path, false, false, 0, 0);
    }

    /// <summary>Checks whether this ending stamp still describes its input.</summary>
    public bool MatchesCurrent() => this == Observe(Path);

    private static readonly HashSet<string> IgnoredDirectoryNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "bin", "obj", ".git", ".gitnexus", ".idea", ".vs", "node_modules", "TestResults", "test-results", ".workflow", ".pending", ".tmp"
    };
}
