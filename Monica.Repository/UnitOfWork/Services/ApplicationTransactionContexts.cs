using System.Collections.Frozen;
using System.Reflection;
using Microsoft.EntityFrameworkCore;
using Monica.ApplicationModel.Abstractions;
using Monica.ApplicationModel.Annotations;
using Monica.Core.Execution;

namespace Monica.Repository.UnitOfWork.Services;

/// <summary>Freezes operation-owned default contexts without coupling persistence to process placement.</summary>
internal sealed class ApplicationTransactionContexts
{
    private readonly IApplicationModel? _model;
    private readonly FrozenDictionary<string, Type> _contexts;

    public ApplicationTransactionContexts(IApplicationModel? model, IReadOnlyDictionary<string, Type> contexts)
    {
        _model = model;
        _contexts = contexts.ToFrozenDictionary(StringComparer.Ordinal);
        foreach (var (domain, context) in _contexts)
        {
            if (model is null || !model.Domains.ContainsKey(domain))
                throw new InvalidOperationException($"Transaction default '{domain}' requires a declared domain in AddApplicationModel.");
            if (!typeof(DbContext).IsAssignableFrom(context) || context.IsAbstract || context.ContainsGenericParameters)
                throw new InvalidOperationException($"Transaction default for '{domain}' is not a concrete DbContext.");
            if (context.Assembly.GetCustomAttribute<ApplicationDomainAttribute>()?.DomainId != domain)
                throw new InvalidOperationException($"Transaction context '{context}' is not owned by domain '{domain}'.");
        }
    }

    public IReadOnlyList<Type>? Select(ExecutionDescriptor descriptor)
    {
        if (_model is null) return null;
        var domain = _model.Operations.TryGetValue(descriptor.InputType, out var operation) ? operation.DomainId :
            _model.FindRole(descriptor.ComponentType.Assembly)?.DomainId ??
            descriptor.ComponentType.Assembly.GetCustomAttribute<ApplicationDomainAttribute>()?.DomainId;
        return domain is not null && _contexts.TryGetValue(domain, out var context) ? [context] : [];
    }
}
