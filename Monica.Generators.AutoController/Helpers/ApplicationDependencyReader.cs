using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Threading;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Monica.Generators.AutoController.Models;

namespace Monica.Generators.AutoController.Helpers;

/// <summary>Collects direct published API calls from an owned compilation, never from its reference inventory.</summary>
internal static class ApplicationDependencyReader
{
    internal static ImmutableArray<ApplicationDependencyModel> Read(Compilation compilation, CancellationToken token)
    {
        var dependencies = new HashSet<ApplicationDependencyModel>();
        foreach (var tree in compilation.SyntaxTrees)
        {
            var semantics = compilation.GetSemanticModel(tree);
            foreach (var call in tree.GetRoot(token).DescendantNodes().OfType<InvocationExpressionSyntax>())
            {
                token.ThrowIfCancellationRequested();
                if (semantics.GetSymbolInfo(call, token).Symbol is not IMethodSymbol method ||
                    !method.ContainingType.AllInterfaces.Any(static contract =>
                        contract.ToDisplayString() == "Monica.Invocation.Abstractions.IApplicationApi")) continue;
                var request = method.Parameters.Select(static parameter => parameter.Type).OfType<INamedTypeSymbol>()
                    .FirstOrDefault(static type => EndpointModelFactory.IsPublishedRequest(type) &&
                        EndpointModelFactory.GetEndpointAttribute(type) is not null);
                var caller = semantics.GetEnclosingSymbol(call.SpanStart, token)?.ContainingType;
                if (request is null || caller is null) continue;
                dependencies.Add(new ApplicationDependencyModel(compilation.AssemblyName!,
                    caller.ToDisplayString(), request.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)));
            }
        }
        return Order(dependencies);
    }

    internal static ImmutableArray<ApplicationDependencyModel> Order(IEnumerable<ApplicationDependencyModel> dependencies) =>
        dependencies.Distinct().OrderBy(static item => item.SourceAssembly, StringComparer.Ordinal)
            .ThenBy(static item => item.SourceType, StringComparer.Ordinal)
            .ThenBy(static item => item.RequestType, StringComparer.Ordinal).ToImmutableArray();
}
