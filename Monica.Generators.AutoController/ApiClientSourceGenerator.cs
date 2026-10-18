using System;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Monica.Generators.AutoController.Constants;
using Monica.Generators.AutoController.Diagnostics;
using Monica.Generators.AutoController.Generators;
using Monica.Generators.AutoController.Helpers;
using Monica.Generators.AutoController.Models;

namespace Monica.Generators.AutoController;

/// <summary>
/// Generates API contracts and transport clients directly from attributed published request declarations.
/// </summary>
[Generator]
public sealed class ApiClientSourceGenerator : IIncrementalGenerator
{
    internal const string ENDPOINT_ANALYSIS_STEP = "ApiEndpointAnalysis";
    internal const string ENDPOINT_COLLECTION_STEP = "ApiEndpointCollection";

    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        var analyzedEndpoints = context.SyntaxProvider.ForAttributeWithMetadataName(
                GeneratorConstants.API_ENDPOINT_ATTRIBUTE,
                static (node, _) => node is TypeDeclarationSyntax,
                static (attributeContext, cancellationToken) =>
                    AnalyzeEndpoint(attributeContext, cancellationToken))
            .WithTrackingName(ENDPOINT_ANALYSIS_STEP);

        var endpointCollection = analyzedEndpoints.Collect()
            .WithTrackingName(ENDPOINT_COLLECTION_STEP);

        context.RegisterSourceOutput(
            endpointCollection,
            static (productionContext, analyses) => Emit(productionContext, analyses));

        context.RegisterSourceOutput(context.CompilationProvider.Select(static (compilation, cancellationToken) =>
            HostGenerationReader.Clients(compilation, cancellationToken)), static (productionContext, analysis) =>
        {
            foreach (var diagnostic in analysis.Diagnostics) productionContext.ReportDiagnostic(diagnostic.ToDiagnostic());
            if (analysis.Configuration is { } config && analysis.Diagnostics.Count == 0)
                ApiClientCodeGenerator.Generate(productionContext, analysis.Endpoints, config, generateInterfaces: false);
        });
    }

    private static AnalysisSnapshot<EndpointModel> AnalyzeEndpoint(
        GeneratorAttributeSyntaxContext attributeContext,
        System.Threading.CancellationToken cancellationToken)
    {
        var fallbackLocation = attributeContext.TargetNode.GetLocation();
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (attributeContext.TargetSymbol is not INamedTypeSymbol request)
            {
                return new AnalysisSnapshot<EndpointModel>(null);
            }

            return EndpointModelFactory.Create(
                request,
                attributeContext.Attributes[0],
                request.ContainingAssembly,
                fallbackLocation,
                publishedOnly: true);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            return new AnalysisSnapshot<EndpointModel>(
                null,
                new[]
                {
                    DiagnosticSnapshot.Create(
                        DiagnosticDescriptors.GenerationFailure,
                        fallbackLocation,
                        exception.ToString())
                });
        }
    }

    private static void Emit(
        SourceProductionContext context,
        System.Collections.Immutable.ImmutableArray<AnalysisSnapshot<EndpointModel>> analyses)
    {
        try
        {
            foreach (var diagnostic in analyses.SelectMany(static analysis => analysis.Diagnostics))
            {
                context.ReportDiagnostic(diagnostic.ToDiagnostic());
            }

            var endpoints = analyses
                .Select(static analysis => analysis.Value)
                .Where(static endpoint => endpoint is not null)
                .Select(static endpoint => endpoint!)
                .ToArray();
            if (endpoints.Length > 0)
            {
                ApiClientCodeGenerator.Generate(context, endpoints, endpoints[0].Configuration);
            }
        }
        catch (Exception exception)
        {
            context.ReportDiagnostic(Diagnostic.Create(
                DiagnosticDescriptors.GenerationFailure,
                Location.None,
                exception.ToString()));
        }
    }
}
