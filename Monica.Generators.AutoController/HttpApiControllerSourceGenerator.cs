using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Monica.Generators.AutoController.Diagnostics;
using Monica.Generators.AutoController.Generators;
using Monica.Generators.AutoController.Helpers;
using Monica.Generators.AutoController.Models;

namespace Monica.Generators.AutoController;

/// <summary>
/// Generates HTTP controllers for application services from endpoint contracts declared on requests.
/// </summary>
[Generator]
public sealed class HttpApiControllerSourceGenerator : IIncrementalGenerator
{
    internal const string CANDIDATE_ANALYSIS_STEP = "ControllerCandidateAnalysis";
    internal const string CANDIDATE_COLLECTION_STEP = "ControllerCandidateCollection";

    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        var analyzedCandidates = context.SyntaxProvider.CreateSyntaxProvider(
                static (node, _) => node is ClassDeclarationSyntax { BaseList: not null },
                static (syntaxContext, cancellationToken) =>
                    AnalyzeCandidate(syntaxContext, cancellationToken))
            .Where(static analysis => analysis is not null)
            .Select(static (analysis, _) => analysis!)
            .WithTrackingName(CANDIDATE_ANALYSIS_STEP);

        var candidateCollection = analyzedCandidates
            .Collect()
            .WithTrackingName(CANDIDATE_COLLECTION_STEP);

        context.RegisterSourceOutput(
            candidateCollection,
            static (productionContext, analyses) => Emit(productionContext, analyses));

        context.RegisterSourceOutput(context.CompilationProvider.Select(static (compilation, cancellationToken) =>
        {
            var assembly = compilation.Assembly;
            if (HostGenerationReader.IsApplicationHost(assembly))
                return ImmutableArray<AnalysisSnapshot<ControllerCandidate>>.Empty;
            if (!HostGenerationReader.HasOwnedGeneration(assembly))
                return ImmutableArray<AnalysisSnapshot<ControllerCandidate>>.Empty;
            var app = HostGenerationReader.ApplicationConfig(assembly);
            var httpMode = app?.ConstructorArguments.FirstOrDefault().Value is 0;
            if (!httpMode && HostGenerationReader.HttpConfig(assembly) is null)
                return ImmutableArray<AnalysisSnapshot<ControllerCandidate>>.Empty;
            var results = ImmutableArray.CreateBuilder<AnalysisSnapshot<ControllerCandidate>>();
            if (app is null)
                results.Add(new AnalysisSnapshot<ControllerCandidate>(null, HostGenerationReader.ValidateOwnership(assembly)));
            foreach (var owned in HostGenerationReader.HandlerAssemblies(assembly))
            foreach (var handler in HostGenerationReader.Types(owned.GlobalNamespace))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (handler.IsAbstract || handler.IsGenericType) continue;
                var analysis = AnalyzeHandler(handler, assembly, Location.None);
                if (analysis is not null) results.Add(analysis);
            }
            return results.ToImmutable();
        }), static (productionContext, analyses) => Emit(productionContext, analyses));
    }

    private static AnalysisSnapshot<ControllerCandidate>? AnalyzeCandidate(
        GeneratorSyntaxContext syntaxContext,
        System.Threading.CancellationToken cancellationToken)
    {
        var declaration = (ClassDeclarationSyntax)syntaxContext.Node;
        var fallbackLocation = declaration.Identifier.GetLocation();
        try
        {
            var handler = syntaxContext.SemanticModel.GetDeclaredSymbol(declaration, cancellationToken)
                as INamedTypeSymbol;
            if (HostGenerationReader.IsApplicationHost(syntaxContext.SemanticModel.Compilation.Assembly) ||
                HostGenerationReader.HasOwnedGeneration(syntaxContext.SemanticModel.Compilation.Assembly)) return null;
            return AnalyzeHandler(handler, syntaxContext.SemanticModel.Compilation.Assembly, fallbackLocation);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception exception)
        {
            return new AnalysisSnapshot<ControllerCandidate>(null, new[] { DiagnosticSnapshot.Create(
                DiagnosticDescriptors.GenerationFailure, fallbackLocation, exception.ToString()) });
        }
    }

    private static AnalysisSnapshot<ControllerCandidate>? AnalyzeHandler(
        INamedTypeSymbol? handler, IAssemblySymbol currentAssembly, Location fallbackLocation)
    {
        try
        {
            if (handler is null || handler.IsAbstract || handler.IsGenericType) return null;
            if (!ApplicationServiceSymbolHelper.TryGetContract(handler, out var request, out var handlerResult))
            {
                return null;
            }

            var diagnostics = new List<DiagnosticSnapshot>();
            foreach (var (attribute, name) in ApplicationServiceSymbolHelper.GetLegacyEndpointAttributes(handler!))
            {
                diagnostics.Add(DiagnosticSnapshot.Create(
                    DiagnosticDescriptors.LegacyHandlerAttribute,
                    attribute.ApplicationSyntaxReference?.GetSyntax().GetLocation()
                    ?? fallbackLocation,
                    handler!.ToDisplayString(),
                    name));
            }

            if (diagnostics.Count > 0)
            {
                return new AnalysisSnapshot<ControllerCandidate>(null, diagnostics);
            }

            var endpointAttribute = EndpointModelFactory.GetEndpointAttribute(request);
            if (endpointAttribute is null)
            {
                // Application use cases can remain local. Only explicit endpoint contracts publish HTTP adapters.
                return null;
            }

            // Legacy request-assembly configuration remains supported. Shared wire metadata alone never publishes a receiver.
            var legacyPublication = HostGenerationReader.HttpConfig(request.ContainingAssembly) is not null;
            if (!legacyPublication && HostGenerationReader.HttpConfig(currentAssembly) is null &&
                HostGenerationReader.ApplicationConfig(currentAssembly)?.ConstructorArguments.FirstOrDefault().Value is not 0)
                return null;

            EndpointModelFactory.TryGetUniqueResultContract(
                request,
                out var requestResult,
                out _);
            var endpointAnalysis = EndpointModelFactory.Create(
                request,
                endpointAttribute,
                currentAssembly,
                fallbackLocation,
                publishedOnly: false);
            if (endpointAnalysis.Value is null)
            {
                return new AnalysisSnapshot<ControllerCandidate>(null, endpointAnalysis.Diagnostics);
            }

            if (!SymbolEqualityComparer.Default.Equals(handlerResult, requestResult))
            {
                return new AnalysisSnapshot<ControllerCandidate>(
                    null,
                    new[]
                    {
                        DiagnosticSnapshot.Create(
                            DiagnosticDescriptors.HandlerResultMismatch,
                            fallbackLocation,
                            handler!.ToDisplayString(),
                            handlerResult.ToDisplayString(),
                            request.ToDisplayString(),
                            requestResult.ToDisplayString())
                    });
            }

            var deleteQueryWarning = EndpointModelFactory.TryCreateDeleteQueryBindingWarning(
                endpointAnalysis.Value,
                request);
            return new AnalysisSnapshot<ControllerCandidate>(
                new ControllerCandidate(
                    endpointAnalysis.Value,
                    ApplicationServiceSymbolHelper.GetTags(handler!).ToImmutableArray()),
                deleteQueryWarning is null ? null : new[] { deleteQueryWarning });
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            return new AnalysisSnapshot<ControllerCandidate>(
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
        ImmutableArray<AnalysisSnapshot<ControllerCandidate>> analyses)
    {
        try
        {
            foreach (var diagnostic in analyses.SelectMany(static analysis => analysis.Diagnostics))
            {
                context.ReportDiagnostic(diagnostic.ToDiagnostic());
            }

            var candidates = analyses
                .Select(static analysis => analysis.Value)
                .Where(static candidate => candidate is not null)
                .Select(static candidate => candidate!)
                .ToArray();
            if (candidates.Length > 0)
            {
                ControllerCodeGenerator.Generate(context, candidates);
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
