using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Monica.Generators.AutoController.Models;

namespace Monica.Generators.AutoController.Generators;

internal static class ApplicationHostCodeGenerator
{
    public static void Generate(SourceProductionContext context, ApplicationHostModel model)
    {
        var clients = model.Operations.Where(static operation => operation.Published)
            .GroupBy(static operation => operation.InterfaceTypeName!, StringComparer.Ordinal)
            .OrderBy(static group => group.Key, StringComparer.Ordinal)
            .ToArray();
        context.AddSource("GeneratedApplicationManifest.g.cs", GenerateManifest(model, clients));
        foreach (var client in clients)
        {
            var first = client.First().Endpoint!;
            context.AddSource($"Application{first.Domain}{first.RequestKind}ApiClient.g.cs", GenerateClient(client));
        }

        ControllerCodeGenerator.Generate(context, model.Operations
            .Where(operation => operation.Endpoint is not null && model.LocalAssemblies.Contains(operation.HandlerAssemblyName))
            .Select(static operation => new ControllerCandidate(operation.Endpoint!, operation.Tags, applicationReceiver: true))
            .ToArray());
    }

    private static string GenerateManifest(ApplicationHostModel model,
        IEnumerable<IGrouping<string, ApplicationOperationModel>> clients)
    {
        var builder = CreateFile();
        builder.AppendLine("[assembly: global::Monica.ApplicationModel.Annotations.GeneratedApplicationManifest(typeof(global::Monica.Generated.GeneratedApplicationManifest))]");
        builder.AppendLine("namespace Monica.Generated;");
        builder.AppendLine();
        builder.AppendLine("/// <summary>Product ownership and locally compiled implementation inventory for this application host.</summary>");
        builder.AppendLine("public sealed class GeneratedApplicationManifest : global::Monica.ApplicationModel.Abstractions.IApplicationManifest");
        builder.AppendLine("{");
        builder.AppendLine("    /// <inheritdoc />");
        builder.AppendLine("    public global::System.Collections.Generic.IReadOnlyList<string> LocalAssemblies { get; } =");
        builder.Append("        global::System.Array.AsReadOnly(new string[] { ")
            .Append(string.Join(", ", model.LocalAssemblies.Select(Literal))).AppendLine(" });");
        AppendList(builder, "ApplicationDomainDefinition", "Domains", model.Assemblies
            .Where(static assembly => assembly.RoleId is null)
            .Select(static assembly => $"{Literal(assembly.DomainId)}, {Literal(assembly.AssemblyName)}" +
                (assembly.BackgroundRole is null ? string.Empty : $", {Literal(assembly.BackgroundRole)}")));
        AppendList(builder, "ApplicationRoleDefinition", "Roles", model.Assemblies
            .Where(static assembly => assembly.RoleId is not null)
            .Select(static assembly => $"{Literal(assembly.RoleId!)}, {Literal(assembly.DomainId)}, {Literal(assembly.AssemblyName)}" +
                (assembly.WorkloadOwner is null ? string.Empty : $", {Literal(assembly.WorkloadOwner)}")));
        AppendList(builder, "ApplicationOperationDefinition", "Operations", model.Operations
            .Select(static operation => $"typeof({operation.RequestTypeName}), {Literal(operation.HandlerTypeName)}, " +
                $"{Literal(operation.HandlerAssemblyName)}, {Literal(operation.DomainId)}, {Literal(operation.RoleId)}, " +
                (operation.RequiredRole is null ? "null" : Literal(operation.RequiredRole))));
        AppendList(builder, "ApplicationApiClientDefinition", "ApiClients", clients
            .Select(static group => $"typeof({group.Key}), typeof({group.First().ImplementationTypeName})"));
        AppendList(builder, "ApplicationDependencyDefinition", "Dependencies", model.Dependencies
            .Select(static dependency => $"{Literal(dependency.SourceAssembly)}, {Literal(dependency.SourceType)}, typeof({dependency.RequestType})"));
        builder.AppendLine("}");
        return builder.ToString();
    }

    private static void AppendList(StringBuilder builder, string definition, string property, IEnumerable<string> arguments)
    {
        var type = "global::Monica.ApplicationModel.Models." + definition;
        builder.AppendLine("    /// <inheritdoc />");
        builder.Append("    public global::System.Collections.Generic.IReadOnlyList<").Append(type).Append("> ")
            .Append(property).AppendLine(" { get; } =");
        builder.Append("        global::System.Array.AsReadOnly(new ").Append(type).AppendLine("[]");
        builder.AppendLine("        {");
        foreach (var argument in arguments)
            builder.Append("            new ").Append(type).Append('(').Append(argument).AppendLine("),");
        builder.AppendLine("        });");
        builder.AppendLine();
    }

    private static string GenerateClient(IGrouping<string, ApplicationOperationModel> client)
    {
        var first = client.First().Endpoint!;
        var builder = CreateFile();
        builder.Append("namespace ").Append(first.ContractNamespaceRoot).AppendLine(".Implementations.Application;");
        builder.AppendLine();
        builder.AppendLine("/// <summary>Dispatches each application operation through its host-owned placement policy.</summary>");
        builder.Append("public sealed class Application").Append(first.Domain).Append(first.RequestKind).AppendLine("ApiClient(");
        builder.Append("    global::Monica.Invocation.Abstractions.IApplicationApiInvoker invoker) : ").Append(client.Key).AppendLine();
        builder.AppendLine("{");
        builder.AppendLine("    private readonly global::Monica.Invocation.Models.ApiTransaction _transaction;");
        builder.Append("    private Application").Append(first.Domain).Append(first.RequestKind).AppendLine("ApiClient(");
        builder.AppendLine("        global::Monica.Invocation.Abstractions.IApplicationApiInvoker invoker,");
        builder.AppendLine("        global::Monica.Invocation.Models.ApiTransaction transaction) : this(invoker)");
        builder.AppendLine("    {");
        builder.AppendLine("        if (!global::System.Enum.IsDefined(transaction)) throw new global::System.ArgumentOutOfRangeException(nameof(transaction));");
        builder.AppendLine("        _transaction = transaction;");
        builder.AppendLine("    }");
        builder.AppendLine("    /// <inheritdoc />");
        builder.Append("    public ").Append(client.Key).AppendLine(" WithTransaction(global::Monica.Invocation.Models.ApiTransaction transaction)");
        builder.Append("        => new Application").Append(first.Domain).Append(first.RequestKind).AppendLine("ApiClient(invoker, transaction);");
        builder.AppendLine();
        foreach (var operation in client.OrderBy(static operation => operation.Endpoint!.OperationName, StringComparer.Ordinal))
        {
            var endpoint = operation.Endpoint!;
            builder.AppendLine("    /// <inheritdoc />");
            builder.Append("    public global::System.Threading.Tasks.Task<").Append(endpoint.ResultTypeName).Append("> ")
                .Append(endpoint.OperationName).Append('(').Append(endpoint.RequestTypeName).AppendLine(" request,");
            builder.AppendLine("        global::System.Threading.CancellationToken cancellationToken = default)");
            builder.Append("        => invoker.InvokeAsync<").Append(endpoint.ResultTypeName).AppendLine(">(request,");
            builder.Append("            new global::Monica.Invocation.Models.ApiHttpEndpoint(")
                .Append(Literal(endpoint.HttpMethod.ToUpperInvariant())).Append(", ").Append(Literal(endpoint.CompleteRoute))
                .Append(", ").Append(endpoint.Binding == "Query" ? "true" : "false")
                .Append(", ").Append(endpoint.Binding == "Body" ? "true" : "false")
                .AppendLine("), cancellationToken, _transaction);");
            builder.AppendLine();
        }
        builder.AppendLine("}");
        return builder.ToString();
    }

    private static StringBuilder CreateFile() => new StringBuilder()
        .AppendLine("// <auto-generated />").AppendLine("#nullable enable").AppendLine();

    private static string Literal(string value) => SymbolDisplay.FormatLiteral(value, quote: true);
}
