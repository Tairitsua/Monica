using Microsoft.Build.Locator;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.MSBuild;
using Monica.BuildingBlocks.CodeAnalysis.Abstractions;
using Monica.BuildingBlocks.CodeAnalysis.Models;

namespace Monica.BuildingBlocks.CodeAnalysis.Services;

/// <summary>
/// Performs on-demand MSBuild and Roslyn analysis without registering a Monica runtime module or loading consumer
/// application assemblies.
/// </summary>
/// <remarks>
/// The analyzer serializes calls made through one instance because MSBuild registration and workspace loading use
/// process-wide resources. Consumers such as Monica Workflow should register one shared instance. Analyzer and
/// source-generator references discovered in analyzed projects load through
/// <see cref="ShadowCopyAnalyzerAssemblyLoader"/>, so a long-lived analysis host never locks the analyzed
/// repository's build outputs.
/// </remarks>
public sealed class BuildingBlockSourceAnalyzer : IBuildingBlockSourceAnalyzer
{
    private static readonly Lock MSBUILD_REGISTRATION_LOCK = new();
    private readonly SemaphoreSlim _analysisLock = new(1, 1);
    private readonly BuildingBlockSymbolClassifier _classifier = new();

    public async Task<BuildingBlockSourceCatalog> AnalyzeAsync(
        BuildingBlockSourceAnalysisRequest request,
        IProgress<BuildingBlockSourceAnalysisProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.WorkspaceRoot);
        ArgumentNullException.ThrowIfNull(request.ProjectPaths);

        await _analysisLock.WaitAsync(cancellationToken);
        try
        {
            return await AnalyzeCoreAsync(request, progress, cancellationToken);
        }
        finally
        {
            _analysisLock.Release();
        }
    }

    private async Task<BuildingBlockSourceCatalog> AnalyzeCoreAsync(
        BuildingBlockSourceAnalysisRequest request,
        IProgress<BuildingBlockSourceAnalysisProgress>? progress,
        CancellationToken cancellationToken)
    {
        var root = Path.GetFullPath(request.WorkspaceRoot);
        if (!Directory.Exists(root))
        {
            throw new DirectoryNotFoundException($"Workspace root does not exist: {root}");
        }

        Report(progress, BuildingBlockSourceAnalysisStage.Initializing, 0, 0, null, "Preparing MSBuild analysis.");
        EnsureMsBuildRegistered();

        var diagnostics = new List<BuildingBlockSourceDiagnostic>();
        var projects = NormalizeProjectPaths(root, request.ProjectPaths, diagnostics);
        var testProjects = NormalizeProjectPaths(root, request.TestProjectPaths ?? [], diagnostics);
        var testProjectPaths = new HashSet<string>(testProjects, StringComparer.OrdinalIgnoreCase);
        // A path listed both ways is a caller error; the test classification wins so its types are
        // never misread as production building blocks.
        projects = projects.Where(path => !testProjectPaths.Contains(path)).ToList();
        var allProjects = projects
            .Concat(testProjects)
            .OrderBy(static path => path, StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (allProjects.Count == 0)
        {
            Report(progress, BuildingBlockSourceAnalysisStage.Completed, 0, 0, null, "No projects were selected.");
            return new BuildingBlockSourceCatalog(
                BuildingBlockSourceAnalysisContract.Version,
                0,
                0,
                diagnostics.Any(static diagnostic => diagnostic.Severity == BuildingBlockSourceDiagnosticSeverity.Error),
                DateTimeOffset.UtcNow,
                [],
                [],
                diagnostics);
        }

        var solutionPath = string.IsNullOrWhiteSpace(request.SolutionPath)
            ? null
            : Path.GetFullPath(request.SolutionPath, root);
        if (solutionPath is not null && !IsWithinRoot(root, solutionPath))
        {
            throw new ArgumentException("The analysis solution must be inside the workspace root.", nameof(request));
        }

        var inputObservation = new SourceInputObservation(root, diagnostics, solutionPath);
        for (var index = 0; index < allProjects.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var path = allProjects[index];
            var relativePath = ToRelativePath(root, path);
            Report(progress, BuildingBlockSourceAnalysisStage.EvaluatingInputs,
                index, allProjects.Count, relativePath, $"Observing build inputs for {relativePath}.");
            if (File.Exists(path))
            {
                inputObservation.ObserveBuildInputs(path, cancellationToken);
            }
        }
        Report(progress, BuildingBlockSourceAnalysisStage.EvaluatingInputs,
            allProjects.Count, allProjects.Count, null, "Build input observation completed.");

        using var workspace = ShadowCopyAnalyzerAssemblyLoader.CreateWorkspace(solutionPath);
        var workspaceDiagnostics = new List<WorkspaceDiagnostic>();
        var workspaceDiagnosticsLock = new Lock();
        workspace.RegisterWorkspaceFailedHandler(args =>
        {
            lock (workspaceDiagnosticsLock)
            {
                workspaceDiagnostics.Add(args.Diagnostic);
            }
        });

        var loader = new BuildingBlockWorkspaceLoader(root, allProjects, diagnostics, (completed, path) =>
            Report(progress, BuildingBlockSourceAnalysisStage.LoadingProjects, completed, allProjects.Count, path,
                path is null ? "Loading the selected project graph." : $"Loading {path}."));
        var loaded = await loader.LoadAsync(workspace, solutionPath, cancellationToken);
        var loadedProjects = loaded.Projects;
        var transientFailures = loaded.FailedProjectPaths
            .Select(path => (AbsolutePath: path, RelativePath: ToRelativePath(root, path)))
            .ToList();

        lock (workspaceDiagnosticsLock)
        {
            diagnostics.AddRange(workspaceDiagnostics.Select(diagnostic => new BuildingBlockSourceDiagnostic(
                diagnostic.Kind == WorkspaceDiagnosticKind.Failure
                    ? "BuildingBlock.Analysis.MSBuild.Failure"
                    : "BuildingBlock.Analysis.MSBuild.Warning",
                diagnostic.Kind == WorkspaceDiagnosticKind.Failure
                    ? BuildingBlockSourceDiagnosticSeverity.Warning
                    : BuildingBlockSourceDiagnosticSeverity.Information,
                diagnostic.Message)));
        }

        inputObservation.Observe(workspace.CurrentSolution, loadedProjects, cancellationToken);
        var analyzedProjects = 0;
        var candidates = new List<AnalyzedBuildingBlockCandidate>();
        var testClasses = new List<BuildingBlockSourceTestClass>();
        for (var index = 0; index < loadedProjects.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var project = loadedProjects[index];
            var projectPath = Path.GetFullPath(project.FilePath!);
            var relativeProjectPath = ToRelativePath(root, projectPath);
            Report(
                progress,
                BuildingBlockSourceAnalysisStage.AnalyzingProjects,
                index,
                loadedProjects.Count,
                relativeProjectPath,
                $"Analyzing {relativeProjectPath}.");

            if (project.Language != LanguageNames.CSharp)
            {
                diagnostics.Add(new BuildingBlockSourceDiagnostic(
                    "BuildingBlock.Analysis.Project.LanguageUnsupported",
                    BuildingBlockSourceDiagnosticSeverity.Error,
                    $"Only C# projects are supported; the project language is '{project.Language}'.",
                    relativeProjectPath));
                continue;
            }

            Compilation? compilation;
            try
            {
                compilation = await project.GetCompilationAsync(cancellationToken);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                diagnostics.Add(new BuildingBlockSourceDiagnostic(
                    "BuildingBlock.Analysis.Project.CompilationFailed",
                    BuildingBlockSourceDiagnosticSeverity.Error,
                    $"Roslyn could not create the project compilation: {exception.GetType().Name}: {exception.Message}",
                    relativeProjectPath));
                transientFailures.Add((projectPath, relativeProjectPath));
                continue;
            }

            if (compilation is null)
            {
                diagnostics.Add(new BuildingBlockSourceDiagnostic(
                    "BuildingBlock.Analysis.Project.CompilationMissing",
                    BuildingBlockSourceDiagnosticSeverity.Error,
                    "Roslyn did not produce a project compilation.",
                    relativeProjectPath));
                transientFailures.Add((projectPath, relativeProjectPath));
                continue;
            }

            analyzedProjects++;
            if (testProjectPaths.Contains(projectPath))
            {
                await CollectTestClassesAsync(
                    root, project, relativeProjectPath, testClasses, diagnostics, cancellationToken);
            }
            else
            {
                await CollectBuildingBlocksAsync(
                    root, project, compilation, relativeProjectPath, candidates, diagnostics, cancellationToken);
            }
        }

        analyzedProjects += await RetryTransientFailuresAsync(
            root, transientFailures, testProjectPaths, candidates, testClasses, diagnostics, progress, inputObservation, cancellationToken);

        Report(
            progress,
            BuildingBlockSourceAnalysisStage.ResolvingDependencies,
            0,
            candidates.Count,
            null,
            "Resolving building-block dependencies.");
        var units = ResolveUnits(candidates, diagnostics, root);
        var isPartial = analyzedProjects != allProjects.Count
                        || diagnostics.Any(static diagnostic =>
                            diagnostic.Code is "BuildingBlock.Analysis.Project.Missing"
                                or "BuildingBlock.Analysis.Project.LoadFailed"
                                or "BuildingBlock.Analysis.Project.CompilationFailed"
                                or "BuildingBlock.Analysis.Project.CompilationMissing"
                                or "BuildingBlock.Analysis.Project.LanguageUnsupported"
                                or "BuildingBlock.Analysis.Project.InputsIncomplete");
        Report(
            progress,
            BuildingBlockSourceAnalysisStage.Completed,
            allProjects.Count,
            allProjects.Count,
            null,
            isPartial ? "Building-block analysis completed with partial results." : "Building-block analysis completed.");

        var finalInputs = inputObservation.Capture();
        return new BuildingBlockSourceCatalog(
            BuildingBlockSourceAnalysisContract.Version,
            allProjects.Count,
            analyzedProjects,
            isPartial,
            DateTimeOffset.UtcNow,
            units,
            testClasses
                .OrderBy(static testClass => testClass.ProjectPath, StringComparer.OrdinalIgnoreCase)
                .ThenBy(static testClass => testClass.RuntimeKey, StringComparer.Ordinal)
                .ToArray(),
            diagnostics
                .OrderByDescending(static diagnostic => diagnostic.Severity)
                .ThenBy(static diagnostic => diagnostic.ProjectPath, StringComparer.OrdinalIgnoreCase)
                .ThenBy(static diagnostic => diagnostic.SourcePath, StringComparer.OrdinalIgnoreCase)
                .ThenBy(static diagnostic => diagnostic.Line)
                .ThenBy(static diagnostic => diagnostic.Code, StringComparer.Ordinal)
                .ToArray())
        {
            InputPaths = inputObservation.SnapshotPaths(),
            EvaluationContexts = inputObservation.Contexts,
            InputsChangedDuringAnalysis = inputObservation.HasChanged(finalInputs),
            InputObservations = finalInputs
        };
    }

    private static IReadOnlyDictionary<string, IReadOnlyList<string>> CaptureInputPaths(Solution solution, IReadOnlyList<Project> selected)
    {
        var result = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var selectedProject in selected)
        {
            var visited = new HashSet<ProjectId>();
            var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var pending = new Queue<Project>();
            pending.Enqueue(selectedProject);
            while (pending.TryDequeue(out var project))
            {
                if (!visited.Add(project.Id)) continue;
                foreach (var path in project.Documents.Select(document => document.FilePath)
                             .Concat(project.AdditionalDocuments.Select(document => document.FilePath))
                             .Concat(project.AnalyzerConfigDocuments.Select(document => document.FilePath))
                             .Concat(project.MetadataReferences.OfType<PortableExecutableReference>().Select(reference => reference.FilePath))
                             .Concat(project.AnalyzerReferences.Select(reference => reference.FullPath)).Append(project.FilePath))
                    if (!string.IsNullOrWhiteSpace(path))
                    {
                        paths.Add(Path.GetFullPath(path));
                        if (Path.GetExtension(path) is ".cs" or ".csproj") paths.Add(Path.GetDirectoryName(Path.GetFullPath(path))!);
                    }
                foreach (var reference in project.ProjectReferences)
                    if (solution.GetProject(reference.ProjectId) is { } referenced) pending.Enqueue(referenced);
            }
            result[Path.GetFullPath(selectedProject.FilePath!)] = paths.Order(StringComparer.OrdinalIgnoreCase).ToArray();
        }
        return result;
    }

    private sealed class SourceInputObservation(
        string root,
        List<BuildingBlockSourceDiagnostic> diagnostics,
        string? solutionPath)
    {
        public string? SolutionPath { get; } = solutionPath;
        private readonly Dictionary<string, BuildingBlockSourceInput> _initialStamps = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, EvaluatedProjectInputs?> _evaluations = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, HashSet<string>> _projectPaths = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, IReadOnlyList<IReadOnlyDictionary<string, string>>> Contexts { get; } = new(StringComparer.OrdinalIgnoreCase);
        public bool HasChanged(IReadOnlyList<BuildingBlockSourceInput> finalInputs)
            => finalInputs.Any(input => _initialStamps[input.Path] != input);
        public IReadOnlyList<BuildingBlockSourceInput> Capture()
            => _initialStamps.Keys.Order(StringComparer.OrdinalIgnoreCase).Select(BuildingBlockSourceInput.Observe).ToArray();

        /// <summary>Materializes the per-project input sets once, when the catalog is constructed.</summary>
        public IReadOnlyDictionary<string, IReadOnlyList<string>> SnapshotPaths()
            => _projectPaths.ToDictionary(
                static pair => pair.Key,
                static pair => (IReadOnlyList<string>)pair.Value.Order(StringComparer.OrdinalIgnoreCase).ToArray(),
                StringComparer.OrdinalIgnoreCase);

        public void Observe(Solution solution, IReadOnlyList<Project> projects, CancellationToken cancellationToken)
        {
            foreach (var (project, paths) in CaptureInputPaths(solution, projects))
            {
                cancellationToken.ThrowIfCancellationRequested();
                ObserveBuildInputs(project, cancellationToken);
                Record(project, paths);
            }
        }

        public void ObserveBuildInputs(string selectedProject, CancellationToken cancellationToken)
        {
            if (SolutionPath is not null)
            {
                Record(selectedProject, [SolutionPath]);
            }
            var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var pending = new Queue<string>();
            pending.Enqueue(Path.GetFullPath(selectedProject));
            while (pending.TryDequeue(out var path))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!visited.Add(path)) continue;
                if (!_evaluations.TryGetValue(path, out var inputs))
                {
                    try { inputs = BuildingBlockBuildInputs.Evaluate(path, SolutionPath); }
                    catch (Exception exception) when (exception is not OperationCanceledException)
                    {
                        inputs = null;
                        diagnostics.Add(new BuildingBlockSourceDiagnostic("BuildingBlock.Analysis.Project.InputsIncomplete",
                            BuildingBlockSourceDiagnosticSeverity.Error,
                            $"MSBuild input evaluation is incomplete for '{ToRelativePath(root, path)}': {exception.Message}",
                            ToRelativePath(root, selectedProject)));
                    }
                    _evaluations[path] = inputs;
                }
                if (inputs is null)
                {
                    if (!diagnostics.Any(diagnostic => diagnostic.Code == "BuildingBlock.Analysis.Project.InputsIncomplete"
                        && diagnostic.ProjectPath == ToRelativePath(root, selectedProject)))
                        diagnostics.Add(new BuildingBlockSourceDiagnostic("BuildingBlock.Analysis.Project.InputsIncomplete",
                            BuildingBlockSourceDiagnosticSeverity.Error,
                            $"Inputs from referenced project '{ToRelativePath(root, path)}' were not completely evaluated.",
                            ToRelativePath(root, selectedProject)));
                    continue;
                }
                Contexts[path] = inputs.Contexts;
                Record(selectedProject, inputs.Paths.SelectMany(input =>
                {
                    var directory = Path.GetDirectoryName(input)!;
                    // Design-time loading can create obj/<configuration> after evaluation. Imported
                    // files still participate in drift checks; disposable directory entries do not.
                    var isBuildOutput = directory.Split(['/', '\\']).Any(segment =>
                        segment.Equals("obj", StringComparison.OrdinalIgnoreCase) || segment.Equals("bin", StringComparison.OrdinalIgnoreCase));
                    return isBuildOutput ? new[] { input } : new[] { input, directory };
                }));
                foreach (var reference in inputs.ProjectReferences) pending.Enqueue(reference);
            }
        }

        private void Record(string project, IEnumerable<string> paths)
        {
            // One filesystem observation per distinct input path: Record runs once per closure
            // member per selected project, so re-observing the merged set would stat every input
            // thousands of times over a full solution.
            if (!_projectPaths.TryGetValue(project, out var set))
            {
                set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                _projectPaths[project] = set;
            }
            foreach (var path in paths)
            {
                if (!set.Add(path)) continue;
                if (!_initialStamps.ContainsKey(path))
                    _initialStamps[path] = BuildingBlockSourceInput.Observe(path);
            }
        }
    }

    private async Task CollectBuildingBlocksAsync(
        string root,
        Project project,
        Compilation compilation,
        string relativeProjectPath,
        List<AnalyzedBuildingBlockCandidate> candidates,
        List<BuildingBlockSourceDiagnostic> diagnostics,
        CancellationToken cancellationToken)
    {
        var symbols = await GetDeclaredTypesAsync(project, cancellationToken);
        foreach (var symbol in symbols)
        {
            if (_classifier.Classify(symbol) is not { } candidate)
            {
                continue;
            }

            var location = CreateLocation(root, candidate.Symbol);
            if (location is null)
            {
                continue;
            }

            var attachedDiagnostics = candidate.Diagnostics
                .Select(diagnostic => diagnostic with
                {
                    ProjectPath = relativeProjectPath,
                    SourcePath = location.RelativePath,
                    Line = location.Line
                })
                .ToList();
            diagnostics.AddRange(attachedDiagnostics);
            candidates.Add(new AnalyzedBuildingBlockCandidate(
                relativeProjectPath,
                project.Name,
                compilation.AssemblyName ?? project.Name,
                candidate,
                location,
                attachedDiagnostics));
        }
    }

    private async Task CollectTestClassesAsync(
        string root,
        Project project,
        string relativeProjectPath,
        List<BuildingBlockSourceTestClass> testClasses,
        List<BuildingBlockSourceDiagnostic> diagnostics,
        CancellationToken cancellationToken)
    {
        var symbols = await GetDeclaredTypesAsync(project, cancellationToken);
        foreach (var candidate in BuildingBlockTestClassScanner.Scan(symbols))
        {
            var location = CreateLocation(root, candidate.Symbol);
            if (location is null)
            {
                continue;
            }

            diagnostics.AddRange(candidate.Diagnostics.Select(diagnostic => diagnostic with
            {
                ProjectPath = relativeProjectPath,
                SourcePath = location.RelativePath,
                Line = location.Line
            }));
            testClasses.Add(new BuildingBlockSourceTestClass(
                candidate.Symbol.GetRuntimeName(),
                relativeProjectPath,
                project.Name,
                candidate.Symbol.ContainingNamespace?.ToDisplayString() ?? string.Empty,
                candidate.Symbol.Name,
                location,
                candidate.TestMethodCount,
                candidate.Traits.Select(trait => MapTraitLocation(root, trait)).ToArray())
            {
                Methods = candidate.Methods.Select(method => new BuildingBlockSourceTestMethod(
                    method.Symbol.ToDisplayString(SymbolDisplayFormat.CSharpErrorMessageFormat),
                    CreateLocation(root, method.Symbol) ?? location,
                    method.Traits.Select(trait => MapTraitLocation(root, trait)).ToArray())).ToArray()
            });
        }
    }

    /// <summary>
    /// Project-load and compilation failures observed in large batch runs can be transient
    /// MSBuild/Roslyn workspace states. Each failed project gets exactly one retry in a fresh
    /// workspace — the manual recovery that has been observed to work — before the failure is
    /// accepted as real. Recovered projects drop their failure diagnostic and keep an information
    /// note; persistent failures keep the original error and gain the retry evidence.
    /// </summary>
    private async Task<int> RetryTransientFailuresAsync(
        string root,
        IReadOnlyList<(string AbsolutePath, string RelativePath)> failures,
        IReadOnlySet<string> testProjectPaths,
        List<AnalyzedBuildingBlockCandidate> candidates,
        List<BuildingBlockSourceTestClass> testClasses,
        List<BuildingBlockSourceDiagnostic> diagnostics,
        IProgress<BuildingBlockSourceAnalysisProgress>? progress,
        SourceInputObservation inputObservation,
        CancellationToken cancellationToken)
    {
        if (failures.Count == 0)
        {
            return 0;
        }

        Report(
            progress,
            BuildingBlockSourceAnalysisStage.AnalyzingProjects,
            0,
            failures.Count,
            null,
            $"Retrying {failures.Count} transiently failed project(s) in a fresh workspace.");
        var recovered = 0;
        using var retryWorkspace = ShadowCopyAnalyzerAssemblyLoader.CreateWorkspace(inputObservation.SolutionPath);
        var retryWorkspaceDiagnostics = new List<WorkspaceDiagnostic>();
        var retryWorkspaceDiagnosticsLock = new Lock();
        retryWorkspace.RegisterWorkspaceFailedHandler(args =>
        {
            lock (retryWorkspaceDiagnosticsLock)
            {
                retryWorkspaceDiagnostics.Add(args.Diagnostic);
            }
        });

        foreach (var (absolutePath, relativePath) in failures)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                inputObservation.ObserveBuildInputs(absolutePath, cancellationToken);
                var project = await retryWorkspace.OpenProjectAsync(
                    absolutePath,
                    cancellationToken: cancellationToken);
                inputObservation.Observe(retryWorkspace.CurrentSolution, [project], cancellationToken);
                if (project.Language != LanguageNames.CSharp)
                {
                    continue;
                }

                var compilation = await project.GetCompilationAsync(cancellationToken);
                if (compilation is null)
                {
                    continue;
                }

                diagnostics.RemoveAll(diagnostic =>
                    diagnostic.ProjectPath is not null
                    && string.Equals(diagnostic.ProjectPath, relativePath, StringComparison.OrdinalIgnoreCase)
                    && diagnostic.Code
                        is "BuildingBlock.Analysis.Project.LoadFailed"
                        or "BuildingBlock.Analysis.Project.CompilationFailed"
                        or "BuildingBlock.Analysis.Project.CompilationMissing");
                diagnostics.Add(new BuildingBlockSourceDiagnostic(
                    "BuildingBlock.Analysis.Project.RecoveredOnRetry",
                    BuildingBlockSourceDiagnosticSeverity.Information,
                    "The project succeeded on a fresh-workspace retry after a transient load or compilation failure.",
                    relativePath));
                if (testProjectPaths.Contains(absolutePath))
                {
                    await CollectTestClassesAsync(
                        root,
                        project,
                        relativePath,
                        testClasses,
                        diagnostics,
                        cancellationToken);
                }
                else
                {
                    await CollectBuildingBlocksAsync(
                        root,
                        project,
                        compilation,
                        relativePath,
                        candidates,
                        diagnostics,
                        cancellationToken);
                }

                recovered++;
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                diagnostics.Add(new BuildingBlockSourceDiagnostic(
                    "BuildingBlock.Analysis.Project.RetryFailed",
                    BuildingBlockSourceDiagnosticSeverity.Warning,
                    $"A fresh-workspace retry also failed: {exception.GetType().Name}: {exception.Message}",
                    relativePath));
            }
        }

        lock (retryWorkspaceDiagnosticsLock)
        {
            diagnostics.AddRange(retryWorkspaceDiagnostics.Select(diagnostic => new BuildingBlockSourceDiagnostic(
                diagnostic.Kind == WorkspaceDiagnosticKind.Failure
                    ? "BuildingBlock.Analysis.MSBuild.Failure"
                    : "BuildingBlock.Analysis.MSBuild.Warning",
                diagnostic.Kind == WorkspaceDiagnosticKind.Failure
                    ? BuildingBlockSourceDiagnosticSeverity.Warning
                    : BuildingBlockSourceDiagnosticSeverity.Information,
                diagnostic.Message)));
        }

        return recovered;
    }

    private static List<string> NormalizeProjectPaths(
        string root,
        IEnumerable<string> projectPaths,
        ICollection<BuildingBlockSourceDiagnostic> diagnostics)
    {
        var results = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var value in projectPaths)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                continue;
            }

            var fullPath = Path.GetFullPath(
                Path.IsPathRooted(value) ? value : Path.Combine(root, value));
            if (!IsWithinRoot(root, fullPath))
            {
                diagnostics.Add(new BuildingBlockSourceDiagnostic(
                    "BuildingBlock.Analysis.Project.OutsideWorkspace",
                    BuildingBlockSourceDiagnosticSeverity.Warning,
                    "External projects are excluded from workspace building-block analysis.",
                    fullPath.Replace('\\', '/')));
                continue;
            }

            if (seen.Add(fullPath))
            {
                results.Add(fullPath);
            }
        }

        return results.OrderBy(path => path, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static async Task<IReadOnlyList<INamedTypeSymbol>> GetDeclaredTypesAsync(
        Project project,
        CancellationToken cancellationToken)
    {
        var results = new List<INamedTypeSymbol>();
        var seen = new HashSet<INamedTypeSymbol>(SymbolEqualityComparer.Default);
        foreach (var document in project.Documents)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!document.SupportsSyntaxTree)
            {
                continue;
            }

            var root = await document.GetSyntaxRootAsync(cancellationToken);
            var semanticModel = await document.GetSemanticModelAsync(cancellationToken);
            if (root is null || semanticModel is null)
            {
                continue;
            }

            foreach (var declaration in root.DescendantNodes().OfType<BaseTypeDeclarationSyntax>())
            {
                if (semanticModel.GetDeclaredSymbol(declaration, cancellationToken) is INamedTypeSymbol symbol
                    && seen.Add(symbol))
                {
                    results.Add(symbol);
                }
            }
        }

        return results;
    }

    private static BuildingBlockSourceTestTrait MapTraitLocation(string root, BuildingBlockSourceTestTrait trait)
        => trait with { Source = trait.Source is { } source ? source with { RelativePath = ToRelativePath(root, source.RelativePath) } : null };

    private static BuildingBlockSourceLocation? CreateLocation(string root, ISymbol symbol)
    {
        var syntax = symbol.DeclaringSyntaxReferences
            .OrderBy(reference => reference.SyntaxTree.FilePath, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();
        if (syntax is null || string.IsNullOrWhiteSpace(syntax.SyntaxTree.FilePath))
        {
            return null;
        }

        var span = syntax.SyntaxTree.GetLineSpan(syntax.Span);
        return new BuildingBlockSourceLocation(
            ToRelativePath(root, syntax.SyntaxTree.FilePath),
            span.StartLinePosition.Line + 1,
            span.StartLinePosition.Character + 1);
    }

    private static IReadOnlyList<SourceBuildingBlock> ResolveUnits(
        IReadOnlyList<AnalyzedBuildingBlockCandidate> candidates,
        ICollection<BuildingBlockSourceDiagnostic> allDiagnostics,
        string root)
    {
        var byIdentity = new Dictionary<string, AnalyzedBuildingBlockCandidate>(StringComparer.Ordinal);
        foreach (var candidate in candidates)
        {
            var identity = SymbolIdentity.Create(candidate.Candidate.Symbol);
            if (!byIdentity.TryAdd(identity, candidate))
            {
                allDiagnostics.Add(new BuildingBlockSourceDiagnostic(
                    "BuildingBlock.Analysis.BuildingBlock.IdentityDuplicate",
                    BuildingBlockSourceDiagnosticSeverity.Error,
                    $"Multiple source declarations resolve to the same assembly/type identity '{identity}'.",
                    candidate.ProjectPath,
                    candidate.Location.RelativePath,
                    candidate.Location.Line));
            }
        }

        var dependenciesByKey = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
        var diagnosticsByKey = new Dictionary<string, IReadOnlyList<BuildingBlockSourceDiagnostic>>(StringComparer.Ordinal);
        foreach (var candidate in candidates)
        {
            var key = CreateCatalogKey(candidate.ProjectPath, candidate.Candidate.Symbol);
            var dependencies = new HashSet<string>(StringComparer.Ordinal);
            var unitDiagnostics = candidate.Diagnostics.ToList();
            foreach (var dependency in candidate.Candidate.Dependencies)
            {
                if (byIdentity.TryGetValue(SymbolIdentity.Create(dependency.Symbol), out var resolved))
                {
                    var dependencyKey = CreateCatalogKey(resolved.ProjectPath, resolved.Candidate.Symbol);
                    if (!string.Equals(dependencyKey, key, StringComparison.Ordinal))
                    {
                        dependencies.Add(dependencyKey);
                    }
                }

            }

            dependenciesByKey[key] = dependencies.Order(StringComparer.Ordinal).ToArray();
            diagnosticsByKey[key] = unitDiagnostics;
        }

        var dependedBy = candidates
            .Select(candidate => CreateCatalogKey(candidate.ProjectPath, candidate.Candidate.Symbol))
            .ToDictionary(static key => key, static _ => new List<string>(), StringComparer.Ordinal);
        foreach (var (source, dependencies) in dependenciesByKey)
        {
            foreach (var dependency in dependencies)
            {
                if (dependedBy.TryGetValue(dependency, out var dependents))
                {
                    dependents.Add(source);
                }
            }
        }

        return candidates
            .Select(candidate =>
            {
                var symbol = candidate.Candidate.Symbol;
                var key = CreateCatalogKey(candidate.ProjectPath, symbol);
                return new SourceBuildingBlock(
                    key,
                    symbol.GetRuntimeName(),
                    candidate.ProjectPath,
                    candidate.ProjectName,
                    candidate.AssemblyName,
                    symbol.ContainingNamespace?.ToDisplayString() ?? string.Empty,
                    symbol.Name,
                    candidate.Candidate.Kind,
                    candidate.Candidate.Title,
                    candidate.Candidate.Description,
                    candidate.Candidate.Owner,
                    candidate.Candidate.Tags,
                    candidate.Candidate.RequirementIds,
                    candidate.Candidate.HasExplicitMetadata,
                    candidate.Location,
                    candidate.Candidate.ExecutionPoints,
                    dependenciesByKey[key],
                    dependedBy[key].Order(StringComparer.Ordinal).ToArray(),
                    diagnosticsByKey[key])
                {
                    Properties = BuildingBlockMemberReader.ReadProperties(symbol, root),
                    Operations = BuildingBlockMemberReader.ReadOperations(symbol, root),
                    OutgoingReferences = candidate.Candidate.Dependencies.Select(dependency => new BuildingBlockSourceReference(
                        dependency.Symbol.ContainingAssembly.Name,
                        dependency.Symbol.GetRuntimeName(),
                        dependency.MissingDiagnosticCode)).ToArray()
                };
            })
            .OrderBy(static unit => unit.ProjectPath, StringComparer.OrdinalIgnoreCase)
            .ThenBy(static unit => unit.Kind)
            .ThenBy(static unit => unit.RuntimeKey, StringComparer.Ordinal)
            .ToArray();
    }

    private static string CreateCatalogKey(string projectPath, INamedTypeSymbol symbol)
        => $"{projectPath}::{symbol.GetRuntimeName()}";

    private static bool IsWithinRoot(string root, string path)
    {
        var relative = Path.GetRelativePath(root, path);
        return !Path.IsPathRooted(relative)
               && relative != ".."
               && !relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
               && !relative.StartsWith($"..{Path.AltDirectorySeparatorChar}", StringComparison.Ordinal);
    }

    private static string ToRelativePath(string root, string path)
        => Path.GetRelativePath(root, Path.GetFullPath(path)).Replace('\\', '/');

    private static void EnsureMsBuildRegistered()
    {
        if (MSBuildLocator.IsRegistered)
        {
            return;
        }

        lock (MSBUILD_REGISTRATION_LOCK)
        {
            if (!MSBuildLocator.IsRegistered)
            {
                MSBuildLocator.RegisterDefaults();
            }
        }
    }

    private static void Report(
        IProgress<BuildingBlockSourceAnalysisProgress>? progress,
        BuildingBlockSourceAnalysisStage stage,
        int completed,
        int total,
        string? currentProject,
        string message)
    {
        progress?.Report(new BuildingBlockSourceAnalysisProgress(
            stage,
            completed,
            total,
            CalculatePercentage(stage, completed, total),
            currentProject,
            message));
    }

    private static decimal? CalculatePercentage(
        BuildingBlockSourceAnalysisStage stage,
        int completed,
        int total)
    {
        var ratio = total == 0 ? 0m : Math.Clamp((decimal)completed / total, 0m, 1m);
        return stage switch
        {
            BuildingBlockSourceAnalysisStage.Initializing => 0m,
            BuildingBlockSourceAnalysisStage.EvaluatingInputs => 5m + 15m * ratio,
            BuildingBlockSourceAnalysisStage.LoadingProjects => 20m + 20m * ratio,
            BuildingBlockSourceAnalysisStage.AnalyzingProjects => 40m + 50m * ratio,
            BuildingBlockSourceAnalysisStage.ResolvingDependencies => 95m,
            BuildingBlockSourceAnalysisStage.Completed => 100m,
            _ => null
        };
    }

    private sealed record AnalyzedBuildingBlockCandidate(
        string ProjectPath,
        string ProjectName,
        string AssemblyName,
        BuildingBlockSymbolCandidate Candidate,
        BuildingBlockSourceLocation Location,
        IReadOnlyList<BuildingBlockSourceDiagnostic> Diagnostics);
}
