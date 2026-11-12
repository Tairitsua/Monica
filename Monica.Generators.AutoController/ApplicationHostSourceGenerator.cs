using System;
using Microsoft.CodeAnalysis;
using Monica.Generators.AutoController.Diagnostics;
using Monica.Generators.AutoController.Generators;
using Monica.Generators.AutoController.Helpers;
using Monica.Generators.AutoController.Models;

namespace Monica.Generators.AutoController;

/// <summary>Generates the declared application inventory, unified API clients, and owned HTTP receiver actions.</summary>
[Generator]
public sealed class ApplicationHostSourceGenerator : IIncrementalGenerator
{
    /// <inheritdoc />
    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        var model = context.CompilationProvider.Select(static (compilation, cancellationToken) =>
        {
            try
            {
                return ApplicationHostModelReader.Read(compilation, cancellationToken);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception exception)
            {
                return new AnalysisSnapshot<ApplicationHostModel>(null,
                    new[] { DiagnosticSnapshot.Create(DiagnosticDescriptors.GenerationFailure, Location.None, exception.ToString()) });
            }
        }).WithTrackingName("ApplicationHostInventory");

        context.RegisterSourceOutput(model, static (productionContext, analysis) =>
        {
            foreach (var diagnostic in analysis.Diagnostics) productionContext.ReportDiagnostic(diagnostic.ToDiagnostic());
            if (analysis.Value is not { } application) return;
            try
            {
                ApplicationHostCodeGenerator.Generate(productionContext, application);
            }
            catch (Exception exception)
            {
                productionContext.ReportDiagnostic(Diagnostic.Create(DiagnosticDescriptors.GenerationFailure,
                    Location.None, exception.ToString()));
            }
        });
    }
}
