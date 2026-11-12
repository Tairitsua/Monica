using System;
using Microsoft.CodeAnalysis;
using Monica.Generators.AutoController.Diagnostics;
using Monica.Generators.AutoController.Helpers;
using Monica.Generators.AutoController.Models;

namespace Monica.Generators.AutoController;

/// <summary>Exports declared core/role ownership as dependency-free assembly metadata for product catalog builds.</summary>
/// <remarks>The generator writes only compiler sources. The optional MSBuild catalog target extracts the metadata into intermediate build assets.</remarks>
[Generator]
public sealed class ApplicationRoleMetadataSourceGenerator : IIncrementalGenerator
{
    /// <inheritdoc />
    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        var exports = context.CompilationProvider.Select(static (compilation, cancellationToken) =>
        {
            try { return ApplicationHostModelReader.Read(compilation, cancellationToken, exportOwnAssembly: true); }
            catch (OperationCanceledException) { throw; }
            catch (Exception exception)
            {
                return new AnalysisSnapshot<ApplicationHostModel>(null,
                    new[] { DiagnosticSnapshot.Create(DiagnosticDescriptors.GenerationFailure, Location.None, exception.ToString()) });
            }
        }).WithTrackingName("ApplicationRoleMetadata");
        context.RegisterSourceOutput(exports, static (productionContext, analysis) =>
        {
            foreach (var diagnostic in analysis.Diagnostics) productionContext.ReportDiagnostic(diagnostic.ToDiagnostic());
            if (analysis.Value is { } model)
                productionContext.AddSource("ApplicationRoleMetadata.g.cs",
                    ApplicationCatalogMetadata.GenerateSource(model, ApplicationCatalogMetadata.EXPORT_KEY));
        });
    }
}
