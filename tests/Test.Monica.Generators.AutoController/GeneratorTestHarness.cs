using System.Collections.Immutable;
using System.Reflection;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Text;
using Monica.Core.Results;
using Monica.DependencyInjection.Abstractions;
using Monica.Application.Abstractions;
using Monica.WebApi.Abstractions;
using Monica.WebApi.Annotations;
using Monica.Application.Annotations;
using Monica.ApplicationModel.Annotations;

namespace Test.Monica.Generators.AutoController;

internal static class GeneratorTestHarness
{
    private static readonly CSharpParseOptions ParseOptions = new(LanguageVersion.Preview);
    private static readonly ImmutableArray<MetadataReference> MetadataReferences = CreateMetadataReferences();

    public static GeneratorTestRun Run(
        string source,
        params IIncrementalGenerator[] generators)
    {
        return Run([source], optionsProvider: null, MetadataReferences, generators);
    }

    public static GeneratorTestRun Run(
        IReadOnlyList<string> sources,
        params IIncrementalGenerator[] generators)
    {
        return Run(sources, optionsProvider: null, MetadataReferences, generators);
    }

    public static GeneratorTestRun RunWithReferences(
        string source,
        IReadOnlyCollection<MetadataReference> additionalReferences,
        params IIncrementalGenerator[] generators)
    {
        return Run(
            [source],
            optionsProvider: null,
            MetadataReferences.AddRange(additionalReferences),
            generators);
    }

    public static GeneratorTestRun RunNamedWithReferences(string assemblyName, string source,
        IReadOnlyCollection<MetadataReference> additionalReferences, params IIncrementalGenerator[] generators)
    {
        var compilation = CreateCompilation(assemblyName, [source], MetadataReferences.AddRange(additionalReferences));
        var driver = CSharpGeneratorDriver.Create(generators.Select(static generator => generator.AsSourceGenerator()),
            parseOptions: ParseOptions);
        return Run(compilation, driver);
    }

    public static GeneratorTestRun RunCatalog(string assemblyName, IReadOnlyCollection<MetadataReference> contractReferences,
        IReadOnlyCollection<string> metadataInputs)
    {
        var compilation = CreateCompilation(assemblyName, [""], MetadataReferences.AddRange(contractReferences));
        var options = new TestAnalyzerConfigOptionsProvider(new Dictionary<string, string>
        {
            ["build_property.MonicaApplicationCatalog"] = "true"
        });
        var driver = CSharpGeneratorDriver.Create([new global::Monica.Generators.AutoController.ApplicationCatalogSourceGenerator().AsSourceGenerator()],
            metadataInputs.Select((value, index) => new InMemoryAdditionalText($"metadata-{index}.monica-application.xml", value)),
            ParseOptions, options);
        return Run(compilation, driver);
    }

    private sealed class InMemoryAdditionalText(string path, string content) : AdditionalText
    {
        public override string Path => path;
        public override SourceText GetText(CancellationToken cancellationToken = default) => SourceText.From(content);
    }

    public static PortableExecutableReference CompileToMetadataReference(
        string assemblyName,
        string source,
        CancellationToken cancellationToken = default)
    {
        var compilation = CreateCompilation(assemblyName, [source], MetadataReferences);
        using var assemblyStream = new MemoryStream();
        var emitResult = compilation.Emit(
            assemblyStream,
            cancellationToken: cancellationToken);
        var errors = emitResult.Diagnostics
            .Where(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error)
            .ToArray();
        if (errors.Length > 0)
        {
            throw new InvalidOperationException(string.Join(Environment.NewLine, errors));
        }

        return MetadataReference.CreateFromImage(
            ImmutableArray.CreateRange(assemblyStream.ToArray()),
            filePath: $"{assemblyName}.dll");
    }

    public static GeneratorTestRun RunWithProjectDirectory(
        string source,
        string projectDirectory,
        params IIncrementalGenerator[] generators)
    {
        var optionsProvider = new TestAnalyzerConfigOptionsProvider(
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["build_property.MSBuildProjectDirectory"] = projectDirectory,
                ["build_property.ProjectDir"] = projectDirectory
            });

        return Run([source], optionsProvider, MetadataReferences, generators);
    }

    private static GeneratorTestRun Run(
        IReadOnlyList<string> sources,
        AnalyzerConfigOptionsProvider? optionsProvider,
        ImmutableArray<MetadataReference> metadataReferences,
        params IIncrementalGenerator[] generators)
    {
        var compilation = CreateCompilation(
            $"GeneratorScenario_{Guid.NewGuid():N}",
            sources,
            metadataReferences);
        GeneratorDriver driver = CSharpGeneratorDriver.Create(
            generators.Select(static generator => generator.AsSourceGenerator()),
            additionalTexts: null,
            parseOptions: ParseOptions,
            optionsProvider: optionsProvider);

        return Run(compilation, driver);
    }

    private static CSharpCompilation CreateCompilation(
        string assemblyName,
        IReadOnlyList<string> sources,
        IEnumerable<MetadataReference> metadataReferences)
    {
        var syntaxTrees = sources
            .Select((source, index) => CSharpSyntaxTree.ParseText(
                source,
                ParseOptions,
                path: $"Source{index}.cs"))
            .ToArray();

        return CSharpCompilation.Create(
            assemblyName,
            syntaxTrees,
            metadataReferences,
            new CSharpCompilationOptions(
                OutputKind.DynamicallyLinkedLibrary,
                nullableContextOptions: NullableContextOptions.Enable));
    }

    private sealed class TestAnalyzerConfigOptionsProvider(
        IReadOnlyDictionary<string, string> globalValues)
        : AnalyzerConfigOptionsProvider
    {
        private static readonly AnalyzerConfigOptions EmptyOptions = new TestAnalyzerConfigOptions(
            new Dictionary<string, string>());
        private readonly AnalyzerConfigOptions _globalOptions = new TestAnalyzerConfigOptions(globalValues);

        public override AnalyzerConfigOptions GlobalOptions => _globalOptions;

        public override AnalyzerConfigOptions GetOptions(SyntaxTree tree)
        {
            return EmptyOptions;
        }

        public override AnalyzerConfigOptions GetOptions(AdditionalText textFile)
        {
            return EmptyOptions;
        }
    }

    private sealed class TestAnalyzerConfigOptions(
        IReadOnlyDictionary<string, string> values)
        : AnalyzerConfigOptions
    {
        public override bool TryGetValue(string key, out string value)
        {
            return values.TryGetValue(key, out value!);
        }
    }

    private static GeneratorTestRun Run(CSharpCompilation inputCompilation, GeneratorDriver driver)
    {
        driver = driver.RunGeneratorsAndUpdateCompilation(
            inputCompilation,
            out var outputCompilation,
            out var driverDiagnostics);

        return new GeneratorTestRun(
            inputCompilation,
            (CSharpCompilation)outputCompilation,
            driver,
            driver.GetRunResult(),
            driverDiagnostics);
    }

    private static ImmutableArray<MetadataReference> CreateMetadataReferences()
    {
        var assemblyPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var trustedPlatformAssemblies = (string?)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES");
        if (!string.IsNullOrWhiteSpace(trustedPlatformAssemblies))
        {
            foreach (var path in trustedPlatformAssemblies.Split(Path.PathSeparator))
            {
                assemblyPaths.Add(path);
            }
        }

        AddAssemblyClosure(typeof(Res).Assembly, assemblyPaths);
        AddAssemblyClosure(typeof(ICachedServiceProvider).Assembly, assemblyPaths);
        AddAssemblyClosure(typeof(ApplicationService).Assembly, assemblyPaths);
        AddAssemblyClosure(typeof(ApiEndpointAttribute).Assembly, assemblyPaths);
        AddAssemblyClosure(typeof(ApplicationHostAttribute).Assembly, assemblyPaths);

        return assemblyPaths
            .Where(File.Exists)
            .OrderBy(static path => path, StringComparer.OrdinalIgnoreCase)
            .Select(static path => (MetadataReference)MetadataReference.CreateFromFile(path))
            .ToImmutableArray();
    }

    private static void AddAssemblyClosure(Assembly rootAssembly, ISet<string> assemblyPaths)
    {
        var pending = new Queue<Assembly>();
        var visited = new HashSet<string>(StringComparer.Ordinal);
        pending.Enqueue(rootAssembly);

        while (pending.TryDequeue(out var assembly))
        {
            if (!visited.Add(assembly.FullName ?? assembly.GetName().Name ?? string.Empty))
            {
                continue;
            }

            if (!assembly.IsDynamic && !string.IsNullOrWhiteSpace(assembly.Location))
            {
                assemblyPaths.Add(assembly.Location);
            }

            foreach (var reference in assembly.GetReferencedAssemblies())
            {
                try
                {
                    pending.Enqueue(Assembly.Load(reference));
                }
                catch (FileNotFoundException)
                {
                    // Optional runtime dependencies that are irrelevant to the in-memory scenario may be absent.
                }
            }
        }
    }

    internal sealed record GeneratorTestRun(
        CSharpCompilation InputCompilation,
        CSharpCompilation OutputCompilation,
        GeneratorDriver Driver,
        GeneratorDriverRunResult RunResult,
        ImmutableArray<Diagnostic> DriverDiagnostics)
    {
        public IReadOnlyDictionary<string, string> GeneratedSources => RunResult.Results
            .SelectMany(static result => result.GeneratedSources)
            .ToDictionary(
                static source => source.HintName,
                static source => source.SourceText.ToString(),
                StringComparer.Ordinal);

        public ImmutableArray<Diagnostic> OutputErrors => OutputCompilation
            .GetDiagnostics()
            .Where(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error)
            .ToImmutableArray();

        public GeneratorTestRun RunAgain()
        {
            return Run(InputCompilation, Driver);
        }
    }
}
