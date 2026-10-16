using System.Text.Json;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.MSBuild;
using Monica.ProjectUnits.CodeAnalysis.Models;

namespace Monica.ProjectUnits.CodeAnalysis.Services;

/// <summary>Loads the selected graph and returns every project from its final immutable solution.</summary>
internal sealed class ProjectUnitWorkspaceLoader(
    string root,
    IReadOnlyList<string> requestedPaths,
    ICollection<ProjectUnitSourceDiagnostic> diagnostics,
    Action<int, string?> report) : IProgress<ProjectLoadProgress>
{
    private readonly HashSet<string> _requestedPaths = new(requestedPaths, StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _completedPaths = new(StringComparer.OrdinalIgnoreCase);
    private readonly Lock _progressLock = new();

    public async Task<ProjectUnitWorkspaceLoadResult> LoadAsync(
        MSBuildWorkspace workspace,
        string? solutionPath,
        CancellationToken cancellationToken)
    {
        report(0, null);
        var existingPaths = new List<string>();
        foreach (var path in requestedPaths)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (File.Exists(path))
            {
                existingPaths.Add(path);
                continue;
            }

            diagnostics.Add(new ProjectUnitSourceDiagnostic(
                "ProjectUnit.Analysis.Project.Missing",
                ProjectUnitSourceDiagnosticSeverity.Error,
                "The selected project file does not exist.",
                RelativePath(path)));
            Complete(path);
        }

        if (solutionPath is not null && existingPaths.Count > 0)
        {
            await LoadSolutionAsync(workspace, solutionPath, existingPaths, cancellationToken);
        }

        var loadedIds = new List<ProjectId>();
        var failedPaths = new List<string>();
        foreach (var path in existingPaths)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var project = workspace.CurrentSolution.Projects.FirstOrDefault(project =>
                                  string.Equals(project.FilePath, path, StringComparison.OrdinalIgnoreCase))
                              ?? await workspace.OpenProjectAsync(path, progress: this, cancellationToken: cancellationToken);
                loadedIds.Add(project.Id);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                diagnostics.Add(new ProjectUnitSourceDiagnostic(
                    "ProjectUnit.Analysis.Project.LoadFailed",
                    ProjectUnitSourceDiagnosticSeverity.Error,
                    $"MSBuild could not load the project: {exception.GetType().Name}: {exception.Message}",
                    RelativePath(path)));
                failedPaths.Add(path);
            }
            Complete(path);
        }

        // Earlier Project objects retain earlier solution snapshots. Compiling those snapshots
        // repeats dependency compilations and source generators across otherwise shared projects.
        var solution = workspace.CurrentSolution;
        return new ProjectUnitWorkspaceLoadResult(
            loadedIds.Select(id => solution.GetProject(id)!).ToArray(),
            failedPaths);
    }

    public void Report(ProjectLoadProgress value)
    {
        var path = Path.GetFullPath(value.FilePath);
        lock (_progressLock)
        {
            if (value.Operation == ProjectLoadOperation.Resolve && _requestedPaths.Contains(path))
            {
                _completedPaths.Add(path);
            }
            report(_completedPaths.Count, RelativePath(path));
        }
    }

    private async Task LoadSolutionAsync(
        MSBuildWorkspace workspace,
        string solutionPath,
        IReadOnlyList<string> selectedPaths,
        CancellationToken cancellationToken)
    {
        var filterPath = Path.Combine(Path.GetTempPath(), $"monica-project-unit-analysis-{Guid.NewGuid():N}.slnf");
        try
        {
            // Roslyn resolves the filter back to the original solution before setting SolutionDir.
            // A filter preserves scoped refreshes without creating or editing a workspace manifest.
            await File.WriteAllTextAsync(filterPath, JsonSerializer.Serialize(new
            {
                solution = new { path = solutionPath, projects = selectedPaths }
            }), cancellationToken);
            await workspace.OpenSolutionAsync(filterPath, progress: this, cancellationToken: cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            diagnostics.Add(new ProjectUnitSourceDiagnostic(
                "ProjectUnit.Analysis.Solution.LoadFailed",
                ProjectUnitSourceDiagnosticSeverity.Warning,
                $"Solution loading failed; selected projects will be loaded individually: {exception.GetType().Name}: {exception.Message}",
                RelativePath(solutionPath)));
        }
        finally
        {
            File.Delete(filterPath);
        }
    }

    private void Complete(string path)
    {
        lock (_progressLock)
        {
            _completedPaths.Add(path);
            report(_completedPaths.Count, RelativePath(path));
        }
    }

    private string RelativePath(string path) => Path.GetRelativePath(root, path).Replace('\\', '/');
}

internal sealed record ProjectUnitWorkspaceLoadResult(
    IReadOnlyList<Project> Projects,
    IReadOnlyList<string> FailedProjectPaths);
