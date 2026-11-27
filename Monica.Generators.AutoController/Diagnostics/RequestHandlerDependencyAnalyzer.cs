using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Operations;

namespace Monica.Generators.AutoController.Diagnostics;

/// <summary>Reports dependencies that let a caller bypass Monica request dispatch.</summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class RequestHandlerDependencyAnalyzer : DiagnosticAnalyzer
{
    /// <inheritdoc />
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics =>
        [DiagnosticDescriptors.DirectHandlerDependency];

    /// <inheritdoc />
    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.Analyze | GeneratedCodeAnalysisFlags.ReportDiagnostics);
        context.EnableConcurrentExecution();
        context.RegisterCompilationStartAction(start =>
        {
            var handler = start.Compilation.GetTypeByMetadataName("Monica.Core.Mediator.IRequestHandler`2");
            if (handler is null) return;

            start.RegisterSymbolAction(symbolContext =>
            {
                var method = (IMethodSymbol)symbolContext.Symbol;
                if (method.IsImplicitlyDeclared) return;
                foreach (var parameter in method.Parameters)
                {
                    if (method.MethodKind == MethodKind.Constructor || IsInjected(parameter))
                        ReportDependency(symbolContext, parameter, parameter.Type, handler);
                }
            }, SymbolKind.Method);

            start.RegisterSymbolAction(symbolContext =>
            {
                var property = (IPropertySymbol)symbolContext.Symbol;
                if (IsInjected(property))
                    ReportDependency(symbolContext, property, property.Type, handler);
            }, SymbolKind.Property);

            start.RegisterOperationAction(ctx => AnalyzeServiceLookup(ctx, handler), OperationKind.Invocation);
        });
    }

    private static void AnalyzeServiceLookup(OperationAnalysisContext context, INamedTypeSymbol handler)
    {
        var invocation = (IInvocationOperation)context.Operation;
        var method = invocation.TargetMethod;
        if (method.Name is not ("GetService" or "GetRequiredService" or "GetServices" or
            "GetKeyedService" or "GetRequiredKeyedService" or "GetKeyedServices")) return;

        if (method.ContainingType.ToDisplayString() is not ("System.IServiceProvider" or
            "Microsoft.Extensions.DependencyInjection.ServiceProvider" or
            "Microsoft.Extensions.DependencyInjection.IKeyedServiceProvider" or
            "Microsoft.Extensions.DependencyInjection.ServiceProviderServiceExtensions" or
            "Microsoft.Extensions.DependencyInjection.ServiceProviderKeyedServiceExtensions")) return;

        var serviceType = method.TypeArguments.FirstOrDefault();
        if (serviceType is null)
        {
            var argument = invocation.Arguments.FirstOrDefault(arg => arg.Parameter?.Name == "serviceType");
            serviceType = (argument?.Value as ITypeOfOperation)?.TypeOperand;
        }
        if (FindHandler(serviceType, handler) is { } dependency)
            context.ReportDiagnostic(Diagnostic.Create(DiagnosticDescriptors.DirectHandlerDependency,
                invocation.Syntax.GetLocation(), dependency.Name));
    }

    private static bool IsInjected(ISymbol symbol) =>
        symbol.GetAttributes().Any(attribute => attribute.AttributeClass?.ToDisplayString() is
            "Microsoft.AspNetCore.Components.InjectAttribute" or "Microsoft.AspNetCore.Mvc.FromServicesAttribute" or
            "Microsoft.Extensions.DependencyInjection.FromKeyedServicesAttribute");

    private static void ReportDependency(SymbolAnalysisContext context, ISymbol symbol, ITypeSymbol service,
        INamedTypeSymbol handler)
    {
        if (FindHandler(service, handler) is { } dependency)
            context.ReportDiagnostic(Diagnostic.Create(DiagnosticDescriptors.DirectHandlerDependency,
                symbol.Locations.FirstOrDefault(), dependency.Name));
    }

    private static INamedTypeSymbol? FindHandler(ITypeSymbol? service, INamedTypeSymbol handler)
    {
        if (service is not INamedTypeSymbol type) return null;
        if (type.OriginalDefinition.SpecialType == SpecialType.System_Collections_Generic_IEnumerable_T &&
            type.TypeArguments[0] is INamedTypeSymbol element)
            type = element;

        return SymbolEqualityComparer.Default.Equals(type.OriginalDefinition, handler) ||
               type.AllInterfaces.Any(contract => SymbolEqualityComparer.Default.Equals(contract.OriginalDefinition, handler))
            ? type
            : null;
    }
}
