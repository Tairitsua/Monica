using System;
using System.Collections.Immutable;
using System.Linq;

namespace Monica.Generators.AutoController.Models;

/// <summary>Compilation-independent inventory emitted by an application host.</summary>
internal sealed class ApplicationHostModel(
    ImmutableArray<ApplicationAssemblyModel> assemblies,
    ImmutableArray<ApplicationOperationModel> operations,
    ImmutableArray<string> localAssemblies = default,
    ImmutableArray<ApplicationDependencyModel> dependencies = default) : IEquatable<ApplicationHostModel>
{
    public ImmutableArray<ApplicationDependencyModel> Dependencies { get; } = dependencies.IsDefault ? [] : dependencies;
    public ImmutableArray<ApplicationAssemblyModel> Assemblies { get; } = assemblies;
    public ImmutableArray<ApplicationOperationModel> Operations { get; } = operations;
    public ImmutableArray<string> LocalAssemblies { get; } = localAssemblies.IsDefault
        ? assemblies.Select(static assembly => assembly.AssemblyName).ToImmutableArray() : localAssemblies;

    public bool Equals(ApplicationHostModel? other) => other is not null &&
        Assemblies.SequenceEqual(other.Assemblies) && Operations.SequenceEqual(other.Operations) &&
        LocalAssemblies.SequenceEqual(other.LocalAssemblies, StringComparer.Ordinal) && Dependencies.SequenceEqual(other.Dependencies);

    public override bool Equals(object? obj) => obj is ApplicationHostModel other && Equals(other);

    public override int GetHashCode()
    {
        unchecked
        {
            var hash = 17;
            foreach (var assembly in Assemblies) hash = (hash * 397) ^ assembly.GetHashCode();
            foreach (var operation in Operations) hash = (hash * 397) ^ operation.GetHashCode();
            foreach (var dependency in Dependencies) hash = (hash * 397) ^ dependency.GetHashCode();
            foreach (var assembly in LocalAssemblies) hash = (hash * 397) ^ StringComparer.Ordinal.GetHashCode(assembly);
            return hash;
        }
    }
}

internal sealed class ApplicationDependencyModel(string sourceAssembly, string sourceType, string requestType)
    : IEquatable<ApplicationDependencyModel>
{
    public string SourceAssembly { get; } = sourceAssembly;
    public string SourceType { get; } = sourceType;
    public string RequestType { get; } = requestType;
    public bool Equals(ApplicationDependencyModel? other) => other is not null &&
        SourceAssembly == other.SourceAssembly && SourceType == other.SourceType && RequestType == other.RequestType;
    public override bool Equals(object? obj) => obj is ApplicationDependencyModel other && Equals(other);
    public override int GetHashCode() => StringComparer.Ordinal.GetHashCode(SourceAssembly + "|" + SourceType + "|" + RequestType);
}

internal sealed class ApplicationAssemblyModel(string assemblyName, string domainId, string? roleId, string? workloadOwner = null)
    : IEquatable<ApplicationAssemblyModel>
{
    public string AssemblyName { get; } = assemblyName;
    public string DomainId { get; } = domainId;
    public string? RoleId { get; } = roleId;
    public string? WorkloadOwner { get; } = workloadOwner;

    public bool Equals(ApplicationAssemblyModel? other) => other is not null &&
        AssemblyName == other.AssemblyName && DomainId == other.DomainId && RoleId == other.RoleId && WorkloadOwner == other.WorkloadOwner;

    public override bool Equals(object? obj) => obj is ApplicationAssemblyModel other && Equals(other);

    public override int GetHashCode()
    {
        unchecked
        {
            return ((StringComparer.Ordinal.GetHashCode(AssemblyName) * 397) ^
                    StringComparer.Ordinal.GetHashCode(DomainId)) * 397 ^ (RoleId?.GetHashCode() ?? 0) ^ (WorkloadOwner?.GetHashCode() ?? 0);
        }
    }
}

internal sealed class ApplicationOperationModel(
    string requestTypeName,
    string handlerTypeName,
    string handlerAssemblyName,
    string domainId,
    string roleId,
    string? requiredRole,
    EndpointModel? endpoint,
    bool published,
    ImmutableArray<string> tags) : IEquatable<ApplicationOperationModel>
{
    public string RequestTypeName { get; } = requestTypeName;
    public string HandlerTypeName { get; } = handlerTypeName;
    public string HandlerAssemblyName { get; } = handlerAssemblyName;
    public string DomainId { get; } = domainId;
    public string RoleId { get; } = roleId;
    public string? RequiredRole { get; } = requiredRole;
    public EndpointModel? Endpoint { get; } = endpoint;
    public bool Published { get; } = published;
    public ImmutableArray<string> Tags { get; } = tags;

    public string? InterfaceTypeName => Published && Endpoint is { } endpoint
        ? $"global::{endpoint.ContractNamespaceRoot}.Contracts.I{endpoint.Domain}{endpoint.RequestKind}Api"
        : null;

    public string? ImplementationTypeName => Published && Endpoint is { } endpoint
        ? $"global::{endpoint.ContractNamespaceRoot}.Implementations.Application.Application{endpoint.Domain}{endpoint.RequestKind}ApiClient"
        : null;

    public bool Equals(ApplicationOperationModel? other) => other is not null &&
        RequestTypeName == other.RequestTypeName && HandlerTypeName == other.HandlerTypeName &&
        HandlerAssemblyName == other.HandlerAssemblyName && DomainId == other.DomainId &&
        RoleId == other.RoleId && RequiredRole == other.RequiredRole &&
        Equals(Endpoint, other.Endpoint) && Published == other.Published &&
        Tags.SequenceEqual(other.Tags, StringComparer.Ordinal);

    public override bool Equals(object? obj) => obj is ApplicationOperationModel other && Equals(other);

    public override int GetHashCode()
    {
        unchecked
        {
            return ((StringComparer.Ordinal.GetHashCode(RequestTypeName) * 397) ^
                    StringComparer.Ordinal.GetHashCode(HandlerTypeName)) * 397 ^ (Endpoint?.GetHashCode() ?? 0);
        }
    }
}
