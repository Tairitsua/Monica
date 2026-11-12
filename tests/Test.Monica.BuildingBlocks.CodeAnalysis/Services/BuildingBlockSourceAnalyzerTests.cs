using System.Diagnostics;
using System.Text.Json;
using AwesomeAssertions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Monica.BuildingBlocks.CodeAnalysis.Models;
using Monica.BuildingBlocks.CodeAnalysis.Services;
using Xunit;

namespace Test.Monica.BuildingBlocks.CodeAnalysis.Services;

public sealed class BuildingBlockSourceAnalyzerTests
{
    [Fact]
    public void Constructor_ShouldBePublicForWorkflowComposition()
    {
        typeof(BuildingBlockSourceAnalyzer).IsPublic.Should().BeTrue();
        new BuildingBlockSourceAnalyzer().Should().NotBeNull();
    }

    [Fact]
    public async Task AnalyzeAsync_WhenASelectedProjectIsMissing_ShouldReturnStablePartialCatalog()
    {
        using var fixture = new TemporaryProjectFixture();
        await fixture.RestoreAsync(TestContext.Current.CancellationToken);
        var progress = new ProgressRecorder();
        var analyzer = new BuildingBlockSourceAnalyzer();

        var result = await analyzer.AnalyzeAsync(
            new BuildingBlockSourceAnalysisRequest(
                fixture.Root,
                [fixture.ProjectPath, Path.Combine(fixture.Root, "Missing.csproj")]),
            progress,
            TestContext.Current.CancellationToken);

        result.ContractVersion.Should().Be(BuildingBlockSourceAnalysisContract.Version);
        result.RequestedProjectCount.Should().Be(2);
        result.AnalyzedProjectCount.Should().Be(1);
        result.IsPartial.Should().BeTrue();
        result.Diagnostics.Should().NotContain(diagnostic =>
            diagnostic.Code == "BuildingBlock.Analysis.MSBuild.Failure");
        result.BuildingBlocks.Should().ContainSingle();
        result.BuildingBlocks[0].Should().Match<SourceBuildingBlock>(unit =>
            unit.CatalogKey == "Sample.csproj::Sample.ManagedUnit"
            && unit.RuntimeKey == "Sample.ManagedUnit"
            && unit.Kind == SourceBuildingBlockKind.DomainService
            && unit.Title == "Managed unit"
            && unit.Description == "Coordinates the sample workflow."
            && unit.Owner == "Sample Team"
            && unit.ExecutionPoints.Count == 0
            && unit.Source.RelativePath == "ManagedUnit.cs"
            && unit.Source.Line > 0);
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(result, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        json.RootElement.GetProperty("contractVersion").GetString().Should().Be("monica-building-blocks-source/v1");
        var serializedBlock = json.RootElement.GetProperty("buildingBlocks")[0];
        serializedBlock.GetProperty("kind").GetInt32().Should().Be((int)SourceBuildingBlockKind.DomainService);
        serializedBlock.TryGetProperty("unitType", out _).Should().BeFalse();
        json.RootElement.TryGetProperty("units", out _).Should().BeFalse();
        result.TestClasses.Should().BeEmpty();
        result.Diagnostics.Should().Contain(diagnostic =>
            diagnostic.Code == "BuildingBlock.Analysis.Project.Missing"
            && diagnostic.Severity == BuildingBlockSourceDiagnosticSeverity.Error);
        progress.Items.Should().Contain(item => item.Stage == BuildingBlockSourceAnalysisStage.LoadingProjects);
        progress.Items.Should().Contain(item => item.Stage == BuildingBlockSourceAnalysisStage.AnalyzingProjects);
        progress.Items.Last().Should().Match<BuildingBlockSourceAnalysisProgress>(item =>
            item.Stage == BuildingBlockSourceAnalysisStage.Completed && item.Percentage == 100m);
    }

    [Fact]
    public async Task AnalyzeAsync_WithTestProjects_ShouldCollectTestClassesWithoutUnitsFromThem()
    {
        using var fixture = new TemporaryProjectFixture();
        await fixture.RestoreAsync(TestContext.Current.CancellationToken);
        var analyzer = new BuildingBlockSourceAnalyzer();
        var progress = new ProgressRecorder();

        var result = await analyzer.AnalyzeAsync(
            new BuildingBlockSourceAnalysisRequest(
                fixture.Root,
                [fixture.ProjectPath],
                [fixture.TestProjectPath]),
            progress,
            TestContext.Current.CancellationToken);

        result.ContractVersion.Should().Be(BuildingBlockSourceAnalysisContract.Version);
        result.RequestedProjectCount.Should().Be(2);
        result.AnalyzedProjectCount.Should().Be(2);
        result.IsPartial.Should().BeFalse();
        result.BuildingBlocks.Should().ContainSingle()
            .Which.RuntimeKey.Should().Be("Sample.ManagedUnit");
        result.TestClasses.Should().ContainSingle().Which.Should().Match<BuildingBlockSourceTestClass>(testClass =>
            testClass.RuntimeKey == "Sample.ManagedUnitTests"
            && testClass.ProjectPath == "Sample.Tests.csproj"
            && testClass.ProjectName == "Sample.Tests"
            && testClass.Namespace == "Sample"
            && testClass.Name == "ManagedUnitTests"
            && testClass.TestMethodCount == 2
            && testClass.Source.RelativePath == "ManagedUnitTests.cs"
            && testClass.Traits.Count == 2);
        result.TestClasses[0].Traits.Select(trait => (trait.Key, trait.Value)).Should().BeEquivalentTo(
        [
            ("REQ", "REQ-SAMPLE-1"),
            ("BuildingBlock", "Sample.ManagedUnit")
        ]);
        result.TestClasses[0].Methods.Should().HaveCount(2);
        result.InputPaths.Should().ContainKey(fixture.ProjectPath);
        result.EvaluationContexts[fixture.ProjectPath].Should().Contain(context => context["DesignTimeBuild"] == "True"
            && context["TargetFramework"] == "net10.0");
        result.Diagnostics.Should().NotContain(diagnostic =>
            diagnostic.Severity == BuildingBlockSourceDiagnosticSeverity.Error);
        var loading = progress.Items.Where(item => item.Stage == BuildingBlockSourceAnalysisStage.LoadingProjects).ToArray();
        loading.Should().NotBeEmpty();
        loading.Should().AllSatisfy(item =>
        {
            item.Total.Should().Be(2);
            item.Completed.Should().BeInRange(0, 2);
        });
        loading[^1].Completed.Should().Be(2);
        progress.Items.Select(item => item.Percentage!.Value).Should().BeInAscendingOrder();
    }

    [Fact]
    public async Task AnalyzeAsync_WhenSolutionContainsAnUnselectedBrokenProject_ShouldKeepTheRequestedScope()
    {
        using var fixture = new TemporaryProjectFixture();
        await fixture.RestoreAsync(TestContext.Current.CancellationToken);
        File.WriteAllText(Path.Combine(fixture.Root, "Unselected.csproj"),
            """<Project Sdk="Microsoft.NET.Sdk"><Import Project="Missing.props" /></Project>""");
        var solutionPath = fixture.AddSolution("Sample.csproj", "Sample.Tests.csproj", "Unselected.csproj");
        var analyzer = new BuildingBlockSourceAnalyzer();
        var scoped = await analyzer.AnalyzeAsync(
            new BuildingBlockSourceAnalysisRequest(fixture.Root, [fixture.ProjectPath], SolutionPath: solutionPath),
            cancellationToken: TestContext.Current.CancellationToken);
        var individual = await analyzer.AnalyzeAsync(
            new BuildingBlockSourceAnalysisRequest(fixture.Root, [fixture.ProjectPath]),
            cancellationToken: TestContext.Current.CancellationToken);

        scoped.RequestedProjectCount.Should().Be(1);
        scoped.AnalyzedProjectCount.Should().Be(1);
        scoped.IsPartial.Should().BeFalse();
        scoped.BuildingBlocks.Should().BeEquivalentTo(individual.BuildingBlocks);
        scoped.TestClasses.Should().BeEmpty();
        scoped.Diagnostics.Should().BeEquivalentTo(individual.Diagnostics);
        scoped.InputPaths[fixture.ProjectPath].Should().Contain(solutionPath);
        scoped.EvaluationContexts[fixture.ProjectPath].Should().Contain(context =>
            context["SolutionDir"] == fixture.Root + Path.DirectorySeparatorChar);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AnalyzeAsync_WhenProjectsShareAGenerator_ShouldReuseOneFinalCompilationGraph(bool useSolution)
    {
        using var fixture = new TemporaryProjectFixture();
        fixture.AddGenerationStampAnalyzer();
        File.WriteAllText(Path.Combine(fixture.Root, "ManagedUnit.cs"),
            File.ReadAllText(Path.Combine(fixture.Root, "ManagedUnit.cs"))
                .Replace("\"Managed unit\"", "AnalysisGeneration.Title", StringComparison.Ordinal));
        var consumerDirectory = Directory.CreateDirectory(Path.Combine(fixture.Root, "ZConsumer")).FullName;
        var consumerProject = Path.Combine(consumerDirectory, "ZConsumer.csproj");
        File.WriteAllText(consumerProject, """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup>
              <ItemGroup><ProjectReference Include="../Sample.csproj" /></ItemGroup>
            </Project>
            """);
        File.WriteAllText(Path.Combine(consumerDirectory, "DependentUnit.cs"), """
            namespace Consumer;
            [Monica.BuildingBlocks.Annotations.BuildingBlockMetadata(Sample.AnalysisGeneration.Title)]
            public sealed class DependentUnit : Monica.Application.Abstractions.DomainService { }
            """);
        await fixture.RestoreAsync(TestContext.Current.CancellationToken, consumerProject);
        var solutionPath = useSolution ? fixture.AddSolution("Sample.csproj", "ZConsumer/ZConsumer.csproj") : null;

        var catalog = await new BuildingBlockSourceAnalyzer().AnalyzeAsync(
            new BuildingBlockSourceAnalysisRequest(fixture.Root, [fixture.ProjectPath, consumerProject], SolutionPath: solutionPath),
            cancellationToken: TestContext.Current.CancellationToken);

        catalog.IsPartial.Should().BeFalse();
        catalog.BuildingBlocks.Should().HaveCount(2);
        catalog.BuildingBlocks.Should().AllSatisfy(unit => unit.Title.Should().Be("generation-1"));
    }

    [Fact]
    public async Task AnalyzeAsync_WhenASelectedProjectIsAbsentFromTheSolution_ShouldStillAnalyzeIt()
    {
        using var fixture = new TemporaryProjectFixture();
        await fixture.RestoreAsync(TestContext.Current.CancellationToken);
        var solutionPath = fixture.AddSolution("Sample.csproj");
        var progress = new ProgressRecorder();

        var catalog = await new BuildingBlockSourceAnalyzer().AnalyzeAsync(
            new BuildingBlockSourceAnalysisRequest(fixture.Root, [fixture.ProjectPath], [fixture.TestProjectPath], solutionPath),
            progress, TestContext.Current.CancellationToken);

        catalog.IsPartial.Should().BeFalse();
        catalog.AnalyzedProjectCount.Should().Be(2);
        catalog.BuildingBlocks.Should().ContainSingle();
        catalog.TestClasses.Should().ContainSingle();
        progress.Items.Select(item => item.Percentage!.Value).Should().BeInAscendingOrder();
    }

    [Fact]
    public async Task AnalyzeAsync_WhenSolutionLoadingFails_ShouldRecoverTheSelectedProjects()
    {
        using var fixture = new TemporaryProjectFixture();
        await fixture.RestoreAsync(TestContext.Current.CancellationToken);
        var solutionPath = Path.Combine(fixture.Root, "Invalid.slnx");
        File.WriteAllText(solutionPath, "invalid solution");

        var catalog = await new BuildingBlockSourceAnalyzer().AnalyzeAsync(
            new BuildingBlockSourceAnalysisRequest(fixture.Root, [fixture.ProjectPath], SolutionPath: solutionPath),
            cancellationToken: TestContext.Current.CancellationToken);

        catalog.IsPartial.Should().BeFalse();
        catalog.BuildingBlocks.Should().ContainSingle();
        catalog.Diagnostics.Should().Contain(diagnostic =>
            diagnostic.Code == "BuildingBlock.Analysis.Solution.LoadFailed"
            && diagnostic.Severity == BuildingBlockSourceDiagnosticSeverity.Warning);
    }

    [Fact]
    public async Task AnalyzeAsync_WhenImportsDependOnSolutionDir_ShouldObserveTheSameInputsThatRoslynLoads()
    {
        using var fixture = new TemporaryProjectFixture();
        await fixture.RestoreAsync(TestContext.Current.CancellationToken);
        var importPath = Path.Combine(fixture.Root, "Solution.props");
        File.WriteAllText(importPath, """<Project><PropertyGroup><DefineConstants>SOLUTION_CONTEXT</DefineConstants></PropertyGroup></Project>""");
        File.WriteAllText(fixture.ProjectPath, File.ReadAllText(fixture.ProjectPath).Replace("</Project>",
            """<Import Project="$(SolutionDir)Solution.props" Condition="'$(DesignTimeBuild)' == 'True'" /></Project>""",
            StringComparison.Ordinal));
        File.AppendAllText(Path.Combine(fixture.Root, "ManagedUnit.cs"), """

            #if SOLUTION_CONTEXT
            namespace Sample
            {
                public sealed class SolutionUnit : Monica.Application.Abstractions.DomainService { }
            }
            #endif
            """);
        var solutionPath = fixture.AddSolution("Sample.csproj");

        var catalog = await new BuildingBlockSourceAnalyzer().AnalyzeAsync(
            new BuildingBlockSourceAnalysisRequest(fixture.Root, [fixture.ProjectPath], SolutionPath: solutionPath),
            cancellationToken: TestContext.Current.CancellationToken);

        catalog.IsPartial.Should().BeFalse();
        catalog.BuildingBlocks.Should().Contain(unit => unit.RuntimeKey == "Sample.SolutionUnit");
        catalog.InputPaths[fixture.ProjectPath].Should().Contain(importPath);
        catalog.InputsChangedDuringAnalysis.Should().BeFalse();
    }

    private sealed class ProgressRecorder : IProgress<BuildingBlockSourceAnalysisProgress>
    {
        public List<BuildingBlockSourceAnalysisProgress> Items { get; } = [];

        public void Report(BuildingBlockSourceAnalysisProgress value) => Items.Add(value);
    }

    [Fact]
    public async Task AnalyzeAsync_WhenScopeChanges_ShouldPreserveOutgoingDeclarationsAndEvaluatedReferenceInputs()
    {
        using var fixture = new TemporaryProjectFixture();
        var consumerDirectory = Path.Combine(fixture.Root, "Consumer");
        Directory.CreateDirectory(consumerDirectory);
        var consumerProject = Path.Combine(consumerDirectory, "Consumer.csproj");
        File.WriteAllText(consumerProject, """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup>
              <ItemGroup><ProjectReference Include="../Sample.csproj" /></ItemGroup>
            </Project>
            """);
        File.WriteAllText(Path.Combine(consumerDirectory, "ConsumerUnit.cs"), """
            namespace Consumer;
            public sealed class ConsumerUnit(Sample.ManagedUnit target) : Monica.Application.Abstractions.DomainService
            {
                public Sample.ManagedUnit Target => target;
            }
            """);
        await fixture.RestoreAsync(TestContext.Current.CancellationToken, consumerProject);
        var analyzer = new BuildingBlockSourceAnalyzer();
        var full = await analyzer.AnalyzeAsync(new BuildingBlockSourceAnalysisRequest(fixture.Root, [fixture.ProjectPath, consumerProject]),
            cancellationToken: TestContext.Current.CancellationToken);
        var scoped = await analyzer.AnalyzeAsync(new BuildingBlockSourceAnalysisRequest(fixture.Root, [consumerProject]),
            cancellationToken: TestContext.Current.CancellationToken);

        scoped.IsPartial.Should().BeFalse();
        scoped.BuildingBlocks.Should().ContainSingle();
        scoped.BuildingBlocks[0].OutgoingReferences.Should().BeEquivalentTo(full.BuildingBlocks.Single(unit => unit.RuntimeKey == "Consumer.ConsumerUnit").OutgoingReferences);
        scoped.BuildingBlocks[0].OutgoingReferences.Should().Contain(reference => reference.RuntimeKey == "Sample.ManagedUnit" && reference.AssemblyName == "Sample");
        scoped.InputPaths[consumerProject].Should().Contain(Path.Combine(fixture.Root, "ManagedUnit.cs"));
        scoped.InputsChangedDuringAnalysis.Should().BeFalse();
    }

    [Fact]
    public async Task AnalyzeAsync_WhenDesignTimeImportIsMissing_ShouldExposeIncompleteInputObservation()
    {
        using var fixture = new TemporaryProjectFixture();
        await fixture.RestoreAsync(TestContext.Current.CancellationToken);
        var project = await File.ReadAllTextAsync(fixture.ProjectPath, TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(fixture.ProjectPath,
            project.Replace("</Project>", "<Import Project=\"Missing.props\" Condition=\"'$(DesignTimeBuild)' == 'True'\" /></Project>", StringComparison.Ordinal),
            TestContext.Current.CancellationToken);

        var catalog = await new BuildingBlockSourceAnalyzer().AnalyzeAsync(new BuildingBlockSourceAnalysisRequest(fixture.Root, [fixture.ProjectPath]),
            cancellationToken: TestContext.Current.CancellationToken);

        catalog.IsPartial.Should().BeTrue();
        catalog.Diagnostics.Should().Contain(diagnostic => diagnostic.Code == "BuildingBlock.Analysis.Project.InputsIncomplete"
            && diagnostic.ProjectPath == "Sample.csproj");
    }

    private sealed class TemporaryProjectFixture : IDisposable
    {
        public TemporaryProjectFixture()
        {
            Root = Directory.CreateTempSubdirectory("monica-building-block-analysis-").FullName;
            ProjectPath = Path.Combine(Root, "Sample.csproj");
            File.WriteAllText(ProjectPath, """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <TargetFramework>net10.0</TargetFramework>
                    <Nullable>enable</Nullable>
                    <EnableDefaultCompileItems>false</EnableDefaultCompileItems>
                  </PropertyGroup>
                  <ItemGroup><Compile Include="ManagedUnit.cs" /></ItemGroup>
                </Project>
                """);
            File.WriteAllText(Path.Combine(Root, "ManagedUnit.cs"), """
                namespace Monica.Application.Abstractions
                {
                    public abstract class DomainService { }
                }

                namespace Monica.BuildingBlocks.Annotations
                {
                    [System.AttributeUsage(System.AttributeTargets.Class)]
                    public sealed class BuildingBlockMetadataAttribute(string title) : System.Attribute
                    {
                        public string Title { get; } = title;
                        public string? Owner { get; set; }
                        public string? Description { get; set; }
                        public string[] Tags { get; set; } = [];
                    }
                }

                namespace Sample
                {
                    using Monica.BuildingBlocks.Annotations;
                    using Monica.Application.Abstractions;

                    /// <summary>Fallback documentation.</summary>
                    [BuildingBlockMetadata("Managed unit", Owner = "Sample Team",
                        Description = "Coordinates the sample workflow.")]
                    public sealed class ManagedUnit : DomainService { }
                }
                """);
            TestProjectPath = Path.Combine(Root, "Sample.Tests.csproj");
            File.WriteAllText(TestProjectPath, """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <TargetFramework>net10.0</TargetFramework>
                    <Nullable>enable</Nullable>
                    <IsTestProject>true</IsTestProject>
                    <EnableDefaultCompileItems>false</EnableDefaultCompileItems>
                  </PropertyGroup>
                  <ItemGroup>
                    <Compile Include="ManagedUnitTests.cs" />
                    <ProjectReference Include="Sample.csproj" />
                  </ItemGroup>
                </Project>
                """);
            File.WriteAllText(Path.Combine(Root, "ManagedUnitTests.cs"), """
                namespace Xunit
                {
                    [System.AttributeUsage(
                        System.AttributeTargets.Class | System.AttributeTargets.Method,
                        AllowMultiple = true)]
                    public sealed class TraitAttribute(string key, string value) : System.Attribute { }

                    public sealed class FactAttribute : System.Attribute { }
                }

                namespace Sample
                {
                    using Xunit;

                    [Trait("REQ", "REQ-SAMPLE-1")]
                    [Trait("BuildingBlock", "Sample.ManagedUnit")]
                    public sealed class ManagedUnitTests
                    {
                        [Fact]
                        public void Creates_the_unit() { }

                        [Fact]
                        public void Coordinates_the_workflow() { }
                    }
                }
                """);
        }

        public string Root { get; }

        public string ProjectPath { get; }

        public string TestProjectPath { get; }

        public string AddSolution(params string[] projectPaths)
        {
            var path = Path.Combine(Root, "Sample.slnx");
            new System.Xml.Linq.XDocument(new System.Xml.Linq.XElement("Solution",
                projectPaths.Select(project => new System.Xml.Linq.XElement("Project",
                    new System.Xml.Linq.XAttribute("Path", project))))).Save(path);
            return path;
        }

        public void AddGenerationStampAnalyzer()
        {
            const string source = """
                using System.Text;
                using System.Threading;
                using Microsoft.CodeAnalysis;
                using Microsoft.CodeAnalysis.Text;
                [Generator]
                public sealed class GenerationStampAnalyzer : ISourceGenerator
                {
                    private static int _generation;
                    public void Initialize(GeneratorInitializationContext context) { }
                    public void Execute(GeneratorExecutionContext context)
                    {
                        var title = "generation-" + Interlocked.Increment(ref _generation);
                        context.AddSource("AnalysisGeneration.g.cs", SourceText.From(
                            "namespace Sample { public static class AnalysisGeneration { public const string Title = \"" + title + "\"; } }",
                            Encoding.UTF8));
                    }
                }
                """;
            var references = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!)
                .Split(Path.PathSeparator)
                .Append(typeof(ISourceGenerator).Assembly.Location)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Select(path => MetadataReference.CreateFromFile(path));
            var compilation = CSharpCompilation.Create(
                $"GenerationStampAnalyzer_{Guid.NewGuid():N}",
                [CSharpSyntaxTree.ParseText(source)],
                references,
                new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
            var emitted = compilation.Emit(Path.Combine(Root, "GenerationStampAnalyzer.dll"));
            Assert.True(emitted.Success, string.Join(Environment.NewLine, emitted.Diagnostics));
            File.WriteAllText(ProjectPath, File.ReadAllText(ProjectPath).Replace("</Project>",
                """<ItemGroup><Analyzer Include="GenerationStampAnalyzer.dll" /></ItemGroup></Project>""",
                StringComparison.Ordinal));
        }

        public async Task RestoreAsync(CancellationToken cancellationToken, string? projectPath = null)
        {
            // MSBuildWorkspace needs restored framework references to bind attribute arguments.
            // This package-free fixture restores against an empty local source to stay offline.
            // Restoring the test project restores its production reference transitively.
            var startInfo = new ProcessStartInfo("dotnet")
            {
                WorkingDirectory = Root,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            foreach (var argument in new[] { "restore", projectPath ?? TestProjectPath, "--source", Root, "--nologo", "-p:NuGetAudit=false" })
            {
                startInfo.ArgumentList.Add(argument);
            }

            using var process = Process.Start(startInfo)!;
            var output = process.StandardOutput.ReadToEndAsync(cancellationToken);
            var error = process.StandardError.ReadToEndAsync(cancellationToken);
            try
            {
                await process.WaitForExitAsync(cancellationToken);
            }
            finally
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                }
            }

            Assert.True(process.ExitCode == 0, $"Fixture restore failed: {await output}\n{await error}");
        }

        public void Dispose()
        {
            try
            {
                Directory.Delete(Root, recursive: true);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // MSBuild may briefly retain a file handle after workspace disposal on Windows.
            }
        }
    }
}
