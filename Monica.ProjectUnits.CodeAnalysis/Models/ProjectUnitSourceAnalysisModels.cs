using System.Collections.Frozen;
using System.Text.Json.Serialization;

namespace Monica.ProjectUnits.CodeAnalysis.Models;

/// <summary>Stable semantic-analysis contract version used by persisted consumers.</summary>
public static class ProjectUnitSourceAnalysisContract
{
    /// <summary>
    /// Version 7 discovers native IRequest&lt;TResponse&gt; payloads used by custom-result application services.
    /// </summary>
    public const string Version = "monica-project-units-source/v7";

    /// <summary>Architectural roles the current source classifier can produce.</summary>
    public static IReadOnlySet<ProjectUnitSourceType> DiscoverableUnitTypes { get; } = new[]
    {
        ProjectUnitSourceType.ApplicationService,
        ProjectUnitSourceType.CrudApplicationService,
        ProjectUnitSourceType.DomainService,
        ProjectUnitSourceType.Repository,
        ProjectUnitSourceType.DomainEvent,
        ProjectUnitSourceType.DomainEventHandler,
        ProjectUnitSourceType.LocalEventHandler,
        ProjectUnitSourceType.Seeder,
        ProjectUnitSourceType.RecurringJob,
        ProjectUnitSourceType.TriggeredJob,
        ProjectUnitSourceType.HttpApi,
        ProjectUnitSourceType.Entity,
        ProjectUnitSourceType.RequestDto,
        ProjectUnitSourceType.Configuration,
        ProjectUnitSourceType.HostedService
    }.ToFrozenSet();
}

/// <summary>
/// Identifies the architectural role of a ProjectUnit discovered through source analysis.
/// </summary>
/// <remarks>
/// Numeric values are part of the persisted source-analysis contract. New roles must be appended so cached Workflow
/// snapshots remain stable without depending on the runtime ProjectUnits assembly.
/// </remarks>
public enum ProjectUnitSourceType
{
    /// <summary>No architectural role has been assigned.</summary>
    None = 0,

    /// <summary>Application service.</summary>
    ApplicationService = 1,

    /// <summary>CRUD application service that participates in automatic controller generation.</summary>
    CrudApplicationService = 2,

    /// <summary>Domain service.</summary>
    DomainService = 3,

    /// <summary>Repository.</summary>
    Repository = 4,

    /// <summary>Domain event.</summary>
    DomainEvent = 5,

    /// <summary>Distributed domain-event handler.</summary>
    DomainEventHandler = 6,

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
public enum ProjectUnitSourceDiagnosticSeverity
{
    /// <summary>Informational analysis context.</summary>
    Information,

    /// <summary>Analysis completed but discovered a condition requiring attention.</summary>
    Warning,

    /// <summary>A project or artifact could not be analyzed reliably.</summary>
    Error
}

/// <summary>Current phase of a source-level ProjectUnit analysis.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ProjectUnitSourceAnalysisStage
{
    /// <summary>The analyzer is validating input and locating MSBuild.</summary>
    Initializing,

    /// <summary>MSBuild projects are being loaded.</summary>
    LoadingProjects,

    /// <summary>Project compilations and declared source types are being analyzed.</summary>
    AnalyzingProjects,

    /// <summary>Cross-unit dependency references are being resolved.</summary>
    ResolvingDependencies,

    /// <summary>The source catalog is complete.</summary>
    Completed,

    /// <summary>Selected projects and their referenced build inputs are being observed.</summary>
    EvaluatingInputs
}

/// <summary>Workspace and project input for one source analysis.</summary>
/// <remarks>
/// Project paths are analyzed for ProjectUnits; test project paths are analyzed only for test classes —
/// they contribute no units of their own. Supply SolutionPath for solution-backed workspaces so the
/// selected projects can be loaded together through a temporary solution filter. The original solution
/// supplies SolutionDir; unselected projects contribute only when referenced by the selected graph.
/// Selected projects absent from that solution are loaded individually in the same workspace.
/// </remarks>
public sealed record ProjectUnitSourceAnalysisRequest(
    string WorkspaceRoot,
    IReadOnlyList<string> ProjectPaths,
    IReadOnlyList<string>? TestProjectPaths = null,
    string? SolutionPath = null);

/// <summary>Progress reported while loading and analyzing projects.</summary>
public sealed record ProjectUnitSourceAnalysisProgress(
    ProjectUnitSourceAnalysisStage Stage,
    int Completed,
    int Total,
    decimal? Percentage,
    string? CurrentProject,
    string Message);

/// <summary>Source position for one declared ProjectUnit.</summary>
public sealed record ProjectUnitSourceLocation(
    string RelativePath,
    int Line,
    int Column);

/// <summary>Actionable diagnostic produced while loading projects or interpreting ProjectUnit contracts.</summary>
public sealed record ProjectUnitSourceDiagnostic(
    string Code,
    ProjectUnitSourceDiagnosticSeverity Severity,
    string Message,
    string? ProjectPath = null,
    string? SourcePath = null,
    int? Line = null);

/// <summary>A semantically discovered source ProjectUnit and its architecture context.</summary>
public sealed record ProjectUnitSourceUnit(
    string CatalogKey,
    string RuntimeKey,
    string ProjectPath,
    string ProjectName,
    string AssemblyName,
    string Namespace,
    string Name,
    ProjectUnitSourceType UnitType,
    string Title,
    string? Description,
    string? Owner,
    IReadOnlyList<string> Tags,
    IReadOnlyList<string> RequirementIds,
    bool HasExplicitMetadata,
    ProjectUnitSourceLocation Source,
    IReadOnlyList<string> ExecutionPoints,
    IReadOnlyList<string> Dependencies,
    IReadOnlyList<string> DependedBy,
    IReadOnlyList<ProjectUnitSourceDiagnostic> Diagnostics)
{
    /// <summary>Declared outgoing symbols, including targets outside this analysis scope.</summary>
    public IReadOnlyList<ProjectUnitSourceReference> OutgoingReferences { get; init; } = [];
}

/// <summary>A scope-independent outgoing type reference. MissingDiagnosticCode identifies required unit associations.</summary>
public sealed record ProjectUnitSourceReference(string AssemblyName, string RuntimeKey, string? MissingDiagnosticCode = null);

/// <summary>One key/value pair declared through a trait attribute on a test class or one of its test methods.</summary>
public sealed record ProjectUnitSourceTestTrait(string Key, string Value)
{
    /// <summary>Location of the declaration, when available.</summary>
    public ProjectUnitSourceLocation? Source { get; init; }
}

/// <summary>One declared test method and only its method-level traits; class traits are inherited by consumers.</summary>
public sealed record ProjectUnitSourceTestMethod(string Name, ProjectUnitSourceLocation Source, IReadOnlyList<ProjectUnitSourceTestTrait> Traits);

/// <summary>
/// A concrete test class declared in a test project: it owns at least one Fact/Theory test method and
/// retains class traits separately from each method's declarations.
/// </summary>
public sealed record ProjectUnitSourceTestClass(
    string RuntimeKey,
    string ProjectPath,
    string ProjectName,
    string Namespace,
    string Name,
    ProjectUnitSourceLocation Source,
    int TestMethodCount,
    IReadOnlyList<ProjectUnitSourceTestTrait> Traits)
{
    /// <summary>Individual test methods, preserving trait pairing and source locations.</summary>
    public IReadOnlyList<ProjectUnitSourceTestMethod> Methods { get; init; } = [];
}

/// <summary>Serializable result of a multi-project semantic ProjectUnit analysis.</summary>
public sealed record ProjectUnitSourceCatalog(
    string ContractVersion,
    int RequestedProjectCount,
    int AnalyzedProjectCount,
    bool IsPartial,
    DateTimeOffset CompletedAtUtc,
    IReadOnlyList<ProjectUnitSourceUnit> Units,
    IReadOnlyList<ProjectUnitSourceTestClass> TestClasses,
    IReadOnlyList<ProjectUnitSourceDiagnostic> Diagnostics)
{
    /// <summary>Absolute evaluated imports, source, metadata and analyzer inputs for each requested project and its project-reference closure.</summary>
    public IReadOnlyDictionary<string, IReadOnlyList<string>> InputPaths { get; init; } = new Dictionary<string, IReadOnlyList<string>>();
    /// <summary>MSBuild global and target selection properties for the evaluated contexts of each project, keyed by absolute project path.</summary>
    public IReadOnlyDictionary<string, IReadOnlyList<IReadOnlyDictionary<string, string>>> EvaluationContexts { get; init; } = new Dictionary<string, IReadOnlyList<IReadOnlyDictionary<string, string>>>();
    /// <summary>Whether evaluated input paths or their file stamps moved during semantic analysis.</summary>
    public bool InputsChangedDuringAnalysis { get; init; }
    /// <summary>Ending file/directory stamps, allowing consumers to detect edits before publishing the observation.</summary>
    public IReadOnlyList<ProjectUnitSourceInput> InputObservations { get; init; } = [];
}

/// <summary>One evaluated input stamp. It is a freshness heuristic, not a content digest.</summary>
public sealed record ProjectUnitSourceInput(string Path, bool Exists, bool IsDirectory, long Length, long LastWriteTimeUtcTicks)
{
    /// <summary>Direct entry names for source directories, excluding disposable build output directories.</summary>
    public string? DirectoryEntries { get; init; }
    /// <summary>Observes the current file or directory at an absolute input path.</summary>
    public static ProjectUnitSourceInput Observe(string path)
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
