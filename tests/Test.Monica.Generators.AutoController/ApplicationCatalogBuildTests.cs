using System.Diagnostics;
using System.Security;
using System.Xml.Linq;
using AwesomeAssertions;
using Xunit;

namespace Test.Monica.Generators.AutoController;

public sealed class ApplicationCatalogBuildTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Collect_WhenProducerIsBuilt_ShouldSupplyTheSameMetadataToBuildAndIde(bool designTime)
    {
        var directory = Directory.CreateTempSubdirectory("monica-catalog-build-");
        try
        {
            const string export = "<ApplicationCatalog Schema=\"1\" />";
            var compilation = GeneratorTestHarness.RunNamedWithReferences("CatalogProducer", """
                [assembly: Monica.ApplicationModel.Annotations.ApplicationDomain("Orders")]
                [assembly: System.Reflection.AssemblyMetadata("Monica.ApplicationModel.Export.v1", "<ApplicationCatalog Schema=\"1\" />")]
                """, []).OutputCompilation;
            var assembly = Path.Combine(directory.FullName, "CatalogProducer.dll");
            using (var stream = File.Create(assembly))
                compilation.Emit(stream, cancellationToken: TestContext.Current.CancellationToken).Success.Should().BeTrue();

            File.WriteAllText(Path.Combine(directory.FullName, "Producer.proj"), """
                <Project>
                  <Target Name="GetTargetPath" Returns="$(MSBuildThisFileDirectory)CatalogProducer.dll" />
                  <Target Name="Build"><Error Text="Design-time metadata collection must not build producers." /></Target>
                </Project>
                """);
            var targets = FindCatalogTargets();
            File.WriteAllText(Path.Combine(directory.FullName, "Catalog.proj"), $$"""
                <Project>
                  <PropertyGroup>
                    <MonicaApplicationCatalog>true</MonicaApplicationCatalog>
                    <IntermediateOutputPath>obj/</IntermediateOutputPath>
                  </PropertyGroup>
                  <ItemGroup>
                    <ProjectReference Include="Producer.proj" ReferenceOutputAssembly="false" Private="false" PrivateAssets="all" />
                  </ItemGroup>
                  <Import Project="{{SecurityElement.Escape(targets)}}" />
                  <Target Name="ResolveProjectReferences" />
                  <Target Name="CoreCompile">
                    <WriteLinesToFile File="inputs.txt" Lines="@(AdditionalFiles)" Overwrite="true" />
                  </Target>
                </Project>
                """);
            var start = new ProcessStartInfo("dotnet")
            {
                WorkingDirectory = directory.FullName,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            foreach (var argument in new[] { "msbuild", "Catalog.proj", "-t:CoreCompile", "-nologo", $"-p:DesignTimeBuild={designTime.ToString().ToLowerInvariant()}" })
                start.ArgumentList.Add(argument);
            using var process = Process.Start(start)!;
            var output = process.StandardOutput.ReadToEndAsync(TestContext.Current.CancellationToken);
            var error = process.StandardError.ReadToEndAsync(TestContext.Current.CancellationToken);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(60));
            try { await process.WaitForExitAsync(timeout.Token); }
            catch (OperationCanceledException)
            {
                process.Kill(entireProcessTree: true);
                process.WaitForExit();
                throw;
            }
            process.ExitCode.Should().Be(0, "{0}\n{1}", await output, await error);
            var inputs = File.ReadAllLines(Path.Combine(directory.FullName, "inputs.txt"));
            inputs.Should().ContainSingle();
            XNode.DeepEquals(XElement.Parse(File.ReadAllText(inputs[0])), XElement.Parse(export)).Should().BeTrue();
        }
        finally { directory.Delete(recursive: true); }
    }

    private static string FindCatalogTargets()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            var target = Path.Combine(directory.FullName, "Monica.Generators.AutoController", "build", "Monica.ApplicationCatalog.targets");
            if (File.Exists(target)) return target;
        }
        throw new FileNotFoundException("The catalog build integration must be tested from its repository checkout.");
    }
}
