using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;
using Monica.Generators.AutoController.Constants;
using Monica.Generators.AutoController.Diagnostics;
using Monica.Generators.AutoController.Models;

namespace Monica.Generators.AutoController.Helpers;

/// <summary>Reads explicit host ownership without inferring receivers from referenced protocol assemblies.</summary>
internal static class HostGenerationReader
{
    public static bool IsApplicationHost(IAssemblySymbol assembly) => Find(assembly, GeneratorConstants.APPLICATION_HOST_ATTRIBUTE) is not null;

    public static AttributeData? ApplicationConfig(IAssemblySymbol assembly) => Find(assembly, GeneratorConstants.APPLICATION_API_CONFIG_ATTRIBUTE);
    public static AttributeData? HttpConfig(IAssemblySymbol assembly) => Find(assembly, GeneratorConstants.WEB_API_CONFIG_ATTRIBUTE);
    private static AttributeData? Find(IAssemblySymbol assembly, string name) =>
        assembly.GetAttributes().FirstOrDefault(attribute => attribute.AttributeClass?.ToDisplayString() == name);

    public static bool HasOwnedGeneration(IAssemblySymbol assembly) => ApplicationConfig(assembly) is not null ||
        HttpConfig(assembly)?.NamedArguments.Any(argument => argument.Key == "HandlerAssemblies") == true;

    public static IEnumerable<INamedTypeSymbol> Types(INamespaceOrTypeSymbol owner)
    {
        foreach (var member in owner.GetMembers())
        {
            if (member is INamedTypeSymbol type)
            {
                yield return type;
                foreach (var nested in Types(type)) yield return nested;
            }
            else if (member is INamespaceSymbol ns)
                foreach (var typeInNamespace in Types(ns)) yield return typeInNamespace;
        }
    }

    private static IEnumerable<IAssemblySymbol> Assemblies(TypedConstant value) =>
        value.Kind == TypedConstantKind.Array && !value.IsNull
            ? value.Values.Select(item => item.Value).OfType<INamedTypeSymbol>().Select(type => type.ContainingAssembly)
            : Enumerable.Empty<IAssemblySymbol>();

    public static IEnumerable<IAssemblySymbol> HandlerAssemblies(IAssemblySymbol current)
    {
        var assemblies = new List<IAssemblySymbol> { current };
        foreach (var config in new[] { ApplicationConfig(current), HttpConfig(current) }.Where(attribute => attribute is not null))
            assemblies.AddRange(Assemblies(config!.NamedArguments.FirstOrDefault(argument => argument.Key == "HandlerAssemblies").Value));
        return assemblies.Distinct<IAssemblySymbol>(SymbolEqualityComparer.Default);
    }

    public static IEnumerable<DiagnosticSnapshot> ValidateOwnership(IAssemblySymbol current)
    {
        foreach (var config in new[] { ApplicationConfig(current), HttpConfig(current) }.Where(attribute => attribute is not null))
        foreach (var assembly in Assemblies(config!.NamedArguments.FirstOrDefault(argument => argument.Key == "HandlerAssemblies").Value)
                     .Distinct<IAssemblySymbol>(SymbolEqualityComparer.Default))
        {
            if (!Types(assembly.GlobalNamespace).Any(type => !type.IsAbstract && !type.IsGenericType &&
                    ApplicationServiceSymbolHelper.TryGetContract(type, out var request, out _) && EndpointModelFactory.GetEndpointAttribute(request) is not null))
                yield return DiagnosticSnapshot.Create(DiagnosticDescriptors.InvalidReceiverOwnership,
                    config.ApplicationSyntaxReference?.GetSyntax().GetLocation(), assembly.Name);
        }
    }

    public static HostClientAnalysis Clients(Compilation compilation, System.Threading.CancellationToken cancellationToken)
    {
        var result = new HostClientAnalysis();
        if (IsApplicationHost(compilation.Assembly)) return result;
        var attribute = ApplicationConfig(compilation.Assembly);
        if (attribute is null) return result;
        result.Diagnostics.AddRange(ValidateOwnership(compilation.Assembly));
        var location = attribute.ApplicationSyntaxReference?.GetSyntax(cancellationToken).GetLocation() ?? Location.None;
        if (attribute.ConstructorArguments.Length != 2 || attribute.ConstructorArguments[0].Value is not int transport || transport is < 0 or > 1)
        {
            result.Diagnostics.Add(DiagnosticSnapshot.Create(DiagnosticDescriptors.InvalidConfiguration, location,
                compilation.Assembly.Name, "ApplicationApiGenerationConfig requires Local or Http. Grpc has no implemented adapter."));
            return result;
        }

        var httpBase = ReadBase(attribute, "HttpClientBaseType", "Monica.Rpc.Abstractions.HttpRpcApi",
            new[] { "Monica.DependencyInjection.Abstractions.ICachedServiceProvider", "System.Net.Http.HttpClient" });
        var localBase = ReadBase(attribute, "LocalClientBaseType", "Monica.Rpc.Abstractions.LocalRpcApi",
            new[] { "Monica.DependencyInjection.Abstractions.ICachedServiceProvider" });
        result.Configuration = new GeneratorConfig("api/v1", null, transport == 0 ? 1 : 2, httpBase, localBase);
        var assemblies = Assemblies(attribute.ConstructorArguments[1]).Append(compilation.Assembly)
            .Distinct<IAssemblySymbol>(SymbolEqualityComparer.Default);
        foreach (var assembly in assemblies)
        foreach (var request in Types(assembly.GlobalNamespace))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (EndpointModelFactory.GetEndpointAttribute(request) is not { } endpointAttribute) continue;
            var analysis = EndpointModelFactory.Create(request, endpointAttribute, compilation.Assembly, location, publishedOnly: true);
            result.Diagnostics.AddRange(analysis.Diagnostics);
            if (analysis.Value is { } endpoint)
            {
                var transportName = transport == 0 ? "Http" : "Local";
                var implementation = $"{endpoint.ContractNamespaceRoot}.Implementations.{transportName}.{transportName}{endpoint.Domain}{endpoint.RequestKind}Api";
                if (!SymbolEqualityComparer.Default.Equals(assembly, compilation.Assembly) && assembly.GetTypeByMetadataName(implementation) is not null)
                    result.Diagnostics.Add(DiagnosticSnapshot.Create(DiagnosticDescriptors.InvalidConfiguration, location,
                        compilation.Assembly.Name, $"Contract assembly '{assembly.Name}' already generates {implementation}. Remove its client targets before generating the same adapters in this host."));
                result.Endpoints.Add(endpoint);
            }
        }
        return result;

        string? ReadBase(AttributeData config, string property, string required, string[] parameters)
        {
            if (config.NamedArguments.FirstOrDefault(argument => argument.Key == property).Value.Value is not INamedTypeSymbol type) return null;
            if (!GeneratorConfigurationReader.TryValidateClientBaseType(type, compilation.Assembly, required, parameters, out var error))
                result.Diagnostics.Add(DiagnosticSnapshot.Create(DiagnosticDescriptors.InvalidConfiguration, location, compilation.Assembly.Name, $"{property}: {error}"));
            return type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
        }
    }
}

internal sealed class HostClientAnalysis
{
    public GeneratorConfig? Configuration { get; set; }
    public List<EndpointModel> Endpoints { get; } = new();
    public List<DiagnosticSnapshot> Diagnostics { get; } = new();
}
