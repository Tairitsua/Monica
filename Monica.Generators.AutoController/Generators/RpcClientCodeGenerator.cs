using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Microsoft.CodeAnalysis;
using Monica.Generators.AutoController.Diagnostics;
using Monica.Generators.AutoController.Models;

namespace Monica.Generators.AutoController.Generators;

internal static class RpcClientCodeGenerator
{
    public static void Generate(
        SourceProductionContext context,
        IReadOnlyList<EndpointModel> endpoints,
        GeneratorConfig config,
        bool generateInterfaces = true)
    {
        var invalidEndpoints = ReportDuplicates(context, endpoints);
        foreach (var group in endpoints
                     .Where(endpoint => !invalidEndpoints.Contains(endpoint))
                     .GroupBy(static endpoint => new
                     {
                         endpoint.ContractNamespaceRoot,
                         endpoint.Domain,
                         endpoint.RequestKind
                     }))
        {
            var orderedEndpoints = group
                .OrderBy(static endpoint => endpoint.OperationName, StringComparer.Ordinal)
                .ToArray();
            var interfaceName = $"I{group.Key.Domain}{group.Key.RequestKind}Api";
            if (generateInterfaces)
            {
                context.AddSource(
                    $"{interfaceName}.g.cs",
                    GenerateInterface(group.Key.ContractNamespaceRoot, interfaceName, orderedEndpoints));
            }

            if (config.GenerateHttpClient)
            {
                var implementationName = $"Http{group.Key.Domain}{group.Key.RequestKind}Api";
                var baseType = config.HttpClientBaseTypeName
                               ?? "global::Monica.Rpc.Abstractions.HttpRpcApi";
                context.AddSource(
                    $"{implementationName}.g.cs",
                    GenerateHttpImplementation(
                        group.Key.ContractNamespaceRoot,
                        group.Key.Domain,
                        interfaceName,
                        implementationName,
                        baseType,
                        orderedEndpoints));
            }

            if (config.GenerateLocalClient)
            {
                var implementationName = $"Local{group.Key.Domain}{group.Key.RequestKind}Api";
                var baseType = config.LocalClientBaseTypeName
                               ?? "global::Monica.Rpc.Abstractions.LocalRpcApi";
                context.AddSource(
                    $"{implementationName}.g.cs",
                    GenerateLocalImplementation(
                        group.Key.ContractNamespaceRoot,
                        group.Key.Domain,
                        interfaceName,
                        implementationName,
                        baseType,
                        orderedEndpoints));
            }
        }
    }

    private static HashSet<EndpointModel> ReportDuplicates(
        SourceProductionContext context,
        IReadOnlyList<EndpointModel> endpoints)
    {
        var invalidEndpoints = new HashSet<EndpointModel>();
        foreach (var group in endpoints.GroupBy(static endpoint => new
                 {
                     endpoint.Domain,
                     endpoint.RequestKind,
                     endpoint.OperationName
                 }).Where(static group => group.Count() > 1))
        {
            var requestNames = string.Join(", ", group.Select(static endpoint =>
                endpoint.RequestDisplayName));
            foreach (var endpoint in group)
            {
                invalidEndpoints.Add(endpoint);
            }

            context.ReportDiagnostic(Diagnostic.Create(
                DiagnosticDescriptors.DuplicateOperation,
                group.First().Location?.ToLocation() ?? Location.None,
                group.Key.OperationName,
                group.Key.Domain,
                group.Key.RequestKind,
                requestNames));
        }

        foreach (var group in endpoints.GroupBy(static endpoint => new
                 {
                     endpoint.HttpMethod,
                     endpoint.NormalizedRoute
                 }).Where(static group => group.Count() > 1))
        {
            var requestNames = string.Join(", ", group.Select(static endpoint =>
                endpoint.RequestDisplayName));
            foreach (var endpoint in group)
            {
                invalidEndpoints.Add(endpoint);
            }

            context.ReportDiagnostic(Diagnostic.Create(
                DiagnosticDescriptors.DuplicateRoute,
                group.First().Location?.ToLocation() ?? Location.None,
                group.Key.HttpMethod.ToUpperInvariant(),
                group.First().CompleteRoute,
                requestNames));
        }

        return invalidEndpoints;
    }

    private static string GenerateInterface(
        string contractNamespaceRoot,
        string interfaceName,
        IReadOnlyList<EndpointModel> endpoints)
    {
        var builder = CreateFileBuilder();
        builder.Append("namespace ").Append(contractNamespaceRoot).AppendLine(".Contracts;");
        builder.AppendLine();
        builder.Append("public interface ").Append(interfaceName)
            .AppendLine(" : global::Monica.Rpc.Abstractions.IRpcApi");
        builder.AppendLine("{");
        foreach (var endpoint in endpoints)
        {
            AppendDocumentation(builder, endpoint.DocumentationComment, "    ");
            builder.Append("    global::System.Threading.Tasks.Task<")
                .Append(endpoint.ResultTypeName).Append("> ")
                .Append(endpoint.OperationName).Append('(')
                .Append(endpoint.RequestTypeName).AppendLine(" request,");
            builder.AppendLine("        global::System.Threading.CancellationToken cancellationToken = default);");
            builder.AppendLine();
        }

        builder.AppendLine("}");
        return builder.ToString();
    }

    private static string GenerateHttpImplementation(
        string contractNamespaceRoot,
        string domain,
        string interfaceName,
        string implementationName,
        string baseType,
        IReadOnlyList<EndpointModel> endpoints)
    {
        var builder = CreateFileBuilder();
        builder.Append("namespace ").Append(contractNamespaceRoot).AppendLine(".Implementations.Http;");
        builder.AppendLine();
        builder.Append("[global::Monica.Rpc.Annotations.RpcClientDomain(\"")
            .Append(EscapeString(domain)).AppendLine("\")]");
        builder.Append("public class ").Append(implementationName).AppendLine("(");
        builder.AppendLine("    global::System.Net.Http.HttpClient httpClient,");
        builder.AppendLine("    global::Monica.DependencyInjection.Abstractions.ICachedServiceProvider serviceProvider,");
        builder.AppendLine("    global::Monica.Rpc.Models.RpcServiceDescriptor serviceDescriptor)");
        builder.Append("    : ").Append(baseType).Append("(serviceProvider, httpClient), ")
            .Append(contractNamespaceRoot).Append(".Contracts.").AppendLine(interfaceName);
        builder.AppendLine("{");
        foreach (var endpoint in endpoints)
        {
            AppendHttpMethod(builder, endpoint);
        }

        builder.AppendLine("}");
        return builder.ToString();
    }

    private static string GenerateLocalImplementation(
        string contractNamespaceRoot,
        string domain,
        string interfaceName,
        string implementationName,
        string baseType,
        IReadOnlyList<EndpointModel> endpoints)
    {
        var builder = CreateFileBuilder();
        builder.Append("namespace ").Append(contractNamespaceRoot).AppendLine(".Implementations.Local;");
        builder.AppendLine();
        builder.Append("[global::Monica.Rpc.Annotations.RpcClientDomain(\"")
            .Append(EscapeString(domain)).AppendLine("\")]");
        builder.Append("public class ").Append(implementationName).AppendLine("(");
        builder.AppendLine("    global::Monica.DependencyInjection.Abstractions.ICachedServiceProvider serviceProvider)");
        builder.Append("    : ").Append(baseType).Append("(serviceProvider), ")
            .Append(contractNamespaceRoot).Append(".Contracts.").AppendLine(interfaceName);
        builder.AppendLine("{");
        foreach (var endpoint in endpoints)
        {
            builder.Append("    public virtual async global::System.Threading.Tasks.Task<")
                .Append(endpoint.ResultTypeName).Append("> ")
                .Append(endpoint.OperationName).Append('(')
                .Append(endpoint.RequestTypeName).AppendLine(" request,");
            builder.AppendLine("        global::System.Threading.CancellationToken cancellationToken = default)");
            builder.AppendLine("    {");
            builder.AppendLine("        return await InvokeAsync(request, cancellationToken);");
            builder.AppendLine("    }");
            builder.AppendLine();
        }

        builder.AppendLine("}");
        return builder.ToString();
    }

    private static void AppendHttpMethod(StringBuilder builder, EndpointModel endpoint)
    {
        var includeQueryString = endpoint.Binding == "Query" ? "true" : "false";
        builder.Append("    public virtual async global::System.Threading.Tasks.Task<")
            .Append(endpoint.ResultTypeName).Append("> ")
            .Append(endpoint.OperationName).Append('(')
            .Append(endpoint.RequestTypeName).AppendLine(" request,");
        builder.AppendLine("        global::System.Threading.CancellationToken cancellationToken = default)");
        builder.AppendLine("    {");
        builder.Append("        return await ExecuteAsync<").Append(endpoint.ResultTypeName).AppendLine(">(");
        builder.AppendLine("            token =>");
        builder.AppendLine("            {");
        builder.AppendLine("                token.ThrowIfCancellationRequested();");
        builder.Append("                return global::System.Threading.Tasks.ValueTask.FromResult(CreateHttpRequest(request, ")
            .Append(GetHttpMethodExpression(endpoint.HttpMethod)).Append(", \"")
            .Append(EscapeString(endpoint.CompleteRoute)).Append("\", includeQueryString: ")
            .Append(includeQueryString).Append(", includeBody: ")
            .Append(endpoint.Binding == "Body" ? "true" : "false").AppendLine("));");
        builder.AppendLine("            },");
        builder.Append("            new global::Monica.Rpc.Models.RemoteCallContext(serviceDescriptor, \"")
            .Append(EscapeString(endpoint.OperationName)).Append("\", \"")
            .Append(EscapeString(endpoint.CompleteRoute)).AppendLine("\"),");
        builder.AppendLine("            cancellationToken);");
        builder.AppendLine("    }");
        builder.AppendLine();
    }

    private static StringBuilder CreateFileBuilder()
    {
        return new StringBuilder()
            .AppendLine("// <auto-generated />")
            .AppendLine("#nullable enable")
            .AppendLine();
    }

    private static void AppendDocumentation(StringBuilder builder, string documentation, string indentation)
    {
        foreach (var line in documentation.Split(new[] { "\r\n", "\n", "\r" }, StringSplitOptions.None))
        {
            builder.Append(indentation).AppendLine(line);
        }
    }

    private static string GetHttpMethodExpression(string httpMethod)
    {
        return httpMethod switch
        {
            "Get" => "global::System.Net.Http.HttpMethod.Get",
            "Post" => "global::System.Net.Http.HttpMethod.Post",
            "Put" => "global::System.Net.Http.HttpMethod.Put",
            "Delete" => "global::System.Net.Http.HttpMethod.Delete",
            "Patch" => "new global::System.Net.Http.HttpMethod(\"PATCH\")",
            _ => throw new InvalidOperationException($"Unsupported HTTP method '{httpMethod}'.")
        };
    }

    private static string EscapeString(string value)
    {
        return value.Replace("\\", "\\\\").Replace("\"", "\\\"");
    }
}
