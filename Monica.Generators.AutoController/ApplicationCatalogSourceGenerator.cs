using System;
using System.Linq;
using Microsoft.CodeAnalysis;
using Monica.Generators.AutoController.Diagnostics;
using Monica.Generators.AutoController.Helpers;

namespace Monica.Generators.AutoController;

/// <summary>Builds a lightweight product ownership catalog from intermediate metadata exported by core and role libraries.</summary>
/// <remarks>Set MonicaApplicationCatalog=true and import the packaged catalog build target. The catalog references contracts, never implementation types.</remarks>
[Generator]
public sealed class ApplicationCatalogSourceGenerator : IIncrementalGenerator
{
    /// <inheritdoc />
    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        var enabled = context.AnalyzerConfigOptionsProvider.Select(static (options, _) =>
            options.GlobalOptions.TryGetValue("build_property.MonicaApplicationCatalog", out var value) &&
            string.Equals(value, "true", StringComparison.OrdinalIgnoreCase));
        var inputs = context.AdditionalTextsProvider.Where(static file =>
                file.Path.EndsWith(ApplicationCatalogMetadata.INPUT_SUFFIX, StringComparison.OrdinalIgnoreCase))
            .Select(static (file, token) => file.GetText(token)?.ToString() ?? string.Empty).Collect();
        context.RegisterSourceOutput(context.CompilationProvider.Combine(enabled).Combine(inputs), static (productionContext, input) =>
        {
            if (!input.Left.Right) return;
            if (input.Right.Length == 0)
            {
                productionContext.ReportDiagnostic(Diagnostic.Create(DiagnosticDescriptors.InvalidApplicationCatalog, Location.None,
                    "No role metadata inputs were supplied. Import Monica.ApplicationCatalog.targets and configure build-only core/role project references."));
                return;
            }
            try
            {
                var analysis = ApplicationCatalogMetadata.Read(input.Left.Left, input.Right, validateCompleteness: true);
                foreach (var diagnostic in analysis.Diagnostics) productionContext.ReportDiagnostic(diagnostic.ToDiagnostic());
                if (analysis.Value is { } catalog)
                    productionContext.AddSource("GeneratedApplicationCatalog.g.cs",
                        ApplicationCatalogMetadata.GenerateSource(catalog, ApplicationCatalogMetadata.CATALOG_KEY));
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception exception)
            {
                productionContext.ReportDiagnostic(Diagnostic.Create(DiagnosticDescriptors.GenerationFailure, Location.None, exception.ToString()));
            }
        });
    }
}
