using System;

namespace Monica.Generators.AutoController.Models;

/// <summary>
/// Immutable semantic model shared by controller and API generation.
/// </summary>
internal sealed class EndpointModel : IEquatable<EndpointModel>
{
    public EndpointModel(
        string requestDisplayName,
        string requestTypeName,
        string resultTypeName,
        string domain,
        string contractNamespaceRoot,
        string routePrefix,
        string relativeRoute,
        string requestKind,
        string operationName,
        string httpMethod,
        string binding,
        string documentationComment,
        string normalizedRoute,
        SourceLocationSnapshot? location,
        GeneratorConfig configuration)
    {
        RequestDisplayName = requestDisplayName;
        RequestTypeName = requestTypeName;
        ResultTypeName = resultTypeName;
        Domain = domain;
        ContractNamespaceRoot = contractNamespaceRoot;
        RoutePrefix = routePrefix;
        RelativeRoute = relativeRoute;
        RequestKind = requestKind;
        OperationName = operationName;
        HttpMethod = httpMethod;
        Binding = binding;
        DocumentationComment = documentationComment;
        NormalizedRoute = normalizedRoute;
        Location = location;
        Configuration = configuration;
    }

    public string RequestDisplayName { get; }

    public string RequestTypeName { get; }

    public string ResultTypeName { get; }

    public string Domain { get; }

    public string ContractNamespaceRoot { get; }

    public string RoutePrefix { get; }

    public string RelativeRoute { get; }

    public string RequestKind { get; }

    public string OperationName { get; }

    public string HttpMethod { get; }

    public string Binding { get; }

    public string DocumentationComment { get; }

    public string NormalizedRoute { get; }

    public SourceLocationSnapshot? Location { get; }

    public GeneratorConfig Configuration { get; }

    public string ControllerRoute => $"{RoutePrefix}/{Domain}";

    public string CompleteRoute => $"{ControllerRoute}/{RelativeRoute}";

    public bool Equals(EndpointModel? other)
    {
        return other is not null &&
               RequestDisplayName == other.RequestDisplayName &&
               RequestTypeName == other.RequestTypeName &&
               ResultTypeName == other.ResultTypeName &&
               Domain == other.Domain &&
               ContractNamespaceRoot == other.ContractNamespaceRoot &&
               RoutePrefix == other.RoutePrefix &&
               RelativeRoute == other.RelativeRoute &&
               RequestKind == other.RequestKind &&
               OperationName == other.OperationName &&
               HttpMethod == other.HttpMethod &&
               Binding == other.Binding &&
               DocumentationComment == other.DocumentationComment &&
               NormalizedRoute == other.NormalizedRoute &&
               Nullable.Equals(Location, other.Location) &&
               Configuration.Equals(other.Configuration);
    }

    public override bool Equals(object? obj)
    {
        return obj is EndpointModel other && Equals(other);
    }

    public override int GetHashCode()
    {
        unchecked
        {
            var hashCode = StringComparer.Ordinal.GetHashCode(RequestTypeName);
            hashCode = (hashCode * 397) ^ StringComparer.Ordinal.GetHashCode(ResultTypeName);
            hashCode = (hashCode * 397) ^ StringComparer.Ordinal.GetHashCode(CompleteRoute);
            hashCode = (hashCode * 397) ^ StringComparer.Ordinal.GetHashCode(OperationName);
            hashCode = (hashCode * 397) ^ Configuration.GetHashCode();
            return hashCode;
        }
    }
}
