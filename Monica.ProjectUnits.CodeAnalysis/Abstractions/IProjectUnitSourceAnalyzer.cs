using Monica.ProjectUnits.CodeAnalysis.Models;

namespace Monica.ProjectUnits.CodeAnalysis.Abstractions;

/// <summary>
/// Analyzes C# projects semantically and returns a source-level Monica ProjectUnit catalog without loading application
/// assemblies or starting application hosts.
/// </summary>
public interface IProjectUnitSourceAnalyzer
{
    /// <summary>
    /// Analyzes the requested projects and reports deterministic project-loading and semantic-analysis progress.
    /// </summary>
    /// <param name="request">
    /// Workspace root with normalized project files to analyze. Test project files are analyzed for test classes
    /// only and contribute no units. An optional workspace solution batches selected projects through a temporary
    /// solution filter without widening the requested catalog scope.
    /// </param>
    /// <param name="progress">Optional progress observer. Callbacks may occur on background threads.</param>
    /// <param name="cancellationToken">Cancellation token for project loading and semantic analysis.</param>
    /// <returns>A serializable source catalog. Project failures are represented as diagnostics and partial results.</returns>
    /// <remarks>
    /// OutgoingReferences preserve declared assembly/type targets even outside the requested scope. Dependencies
    /// and DependedBy are conveniences resolved only within that scope; they are not intrinsic source identities.
    /// Consumers assembling incremental workspace views must resolve outgoing declarations after merging project
    /// facts. Test class traits and method traits remain separate. InputPaths describes SDK-evaluated imports and
    /// source/reference files; EvaluationContexts records the corresponding design-time properties. Incomplete
    /// input evaluation yields partial results. InputsChangedDuringAnalysis reports stamp drift, not cryptographic evidence.
    /// Solution-backed analysis and individual-project recovery share SolutionDir. Compilations are obtained from
    /// the final loaded solution so referenced compilations and source generators can be shared across the analysis.
    /// </remarks>
    Task<ProjectUnitSourceCatalog> AnalyzeAsync(
        ProjectUnitSourceAnalysisRequest request,
        IProgress<ProjectUnitSourceAnalysisProgress>? progress = null,
        CancellationToken cancellationToken = default);
}
