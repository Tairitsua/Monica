using System.Collections.Frozen;
using System.Collections.Immutable;
using System.Reflection;
using Monica.Core.TypeDiscovery.Abstractions;
using Monica.Framework.Seeder.Abstractions;
using Monica.Framework.Seeder.Annotations;
using Monica.Tool.Extensions;

namespace Monica.Framework.Seeder.Models.Internal;

internal sealed class SeederGraph
{
    private static readonly IComparer<Type> TYPE_NAME_COMPARER = Comparer<Type>.Create(
        static (left, right) => StringComparer.Ordinal.Compare(GetTypeName(left), GetTypeName(right)));

    private SeederGraph(ImmutableArray<SeederDescriptor> nodes)
    {
        Nodes = nodes;
        NodesByType = nodes.ToFrozenDictionary(static node => node.SeederType);
    }

    public static SeederGraph Empty { get; } = new([]);

    public ImmutableArray<SeederDescriptor> Nodes { get; }

    public FrozenDictionary<Type, SeederDescriptor> NodesByType { get; }

    public string? GetAutomaticBlockReason(Type seederType)
    {
        foreach (var dependency in NodesByType[seederType].Dependencies)
        {
            var prerequisite = NodesByType[dependency];
            if (prerequisite.Activation != ActivationMode.Automatic)
                return $"Prerequisite '{prerequisite.SeederTypeName}' requires {prerequisite.Activation} admission.";
            if (GetAutomaticBlockReason(dependency) is { } blocked)
                return $"Prerequisite '{prerequisite.SeederTypeName}': {blocked}";
        }
        return null;
    }

    public static SeederGraph Create(IEnumerable<Type> seederTypes, Monica.Modules.ModuleSeederOption options,
        Func<Type, ActivationMode>? getActivation = null)
    {
        ArgumentNullException.ThrowIfNull(seederTypes);
        ArgumentNullException.ThrowIfNull(options);
        ValidateOptions(options);

        var discoveredTypes = seederTypes
            .OrderBy(GetTypeName, StringComparer.Ordinal)
            .ToArray();
        var duplicateType = discoveredTypes
            .GroupBy(static type => type)
            .FirstOrDefault(static group => group.Count() > 1);
        if (duplicateType is not null)
        {
            throw new InvalidOperationException($"Seeder type '{GetTypeName(duplicateType.Key)}' was discovered more than once.");
        }

        var duplicateTypeName = discoveredTypes
            .GroupBy(GetTypeName, StringComparer.Ordinal)
            .FirstOrDefault(static group => group.Count() > 1);
        if (duplicateTypeName is not null)
        {
            throw new InvalidOperationException(
                $"Seeder full type name '{duplicateTypeName.Key}' is not unique in the discovery scope.");
        }

        var discoveredSet = discoveredTypes.ToFrozenSet();
        var nodes = ImmutableArray.CreateBuilder<SeederDescriptor>(discoveredTypes.Length);
        foreach (var seederType in discoveredTypes)
        {
            ValidateSeederType(seederType);
            var policy = seederType.GetCustomAttribute<SeederPolicyAttribute>(inherit: true);
            var declaredExecutionMode = policy?.ExecutionMode ?? SeederExecutionMode.Inherit;
            var declaredCriticality = policy?.Criticality ?? SeederCriticality.Inherit;
            var declaredFailureBehavior = policy?.FailureBehavior ?? SeederFailureBehavior.Inherit;
            var executionMode = ResolveExecutionMode(declaredExecutionMode, options);
            var criticality = ResolveCriticality(declaredCriticality, options);
            var failureBehavior = ResolveFailureBehavior(declaredFailureBehavior, options);
            var maxAttempts = policy is { MaxAttempts: > 0 } ? policy.MaxAttempts : options.DefaultMaxAttempts;
            if (policy is { MaxAttempts: < 0 })
            {
                throw new InvalidOperationException(
                    $"Seeder '{GetTypeName(seederType)}' has invalid MaxAttempts={policy.MaxAttempts}. Use zero to inherit or a positive value.");
            }

            var dependencies = GetDependencies(seederType);
            ValidateDependencies(seederType, dependencies, discoveredSet);
            nodes.Add(new SeederDescriptor(
                seederType,
                seederType.GetCleanName(),
                GetTypeName(seederType),
                executionMode,
                GetSource(declaredExecutionMode == SeederExecutionMode.Inherit),
                criticality,
                GetSource(declaredCriticality == SeederCriticality.Inherit),
                failureBehavior,
                GetSource(declaredFailureBehavior == SeederFailureBehavior.Inherit),
                maxAttempts,
                GetSource(policy is not { MaxAttempts: > 0 }),
                dependencies,
                getActivation?.Invoke(seederType) ?? ActivationMode.Automatic));
        }

        var graph = new SeederGraph(nodes.MoveToImmutable());
        graph.ValidateCriticality();
        graph.ValidateAcyclic();
        return graph;
    }

    internal static void ValidateOptions(Monica.Modules.ModuleSeederOption options)
    {
        if (options.MaxConcurrency <= 0)
        {
            throw new InvalidOperationException("Seeder MaxConcurrency must be greater than zero.");
        }

        if (options.DefaultExecutionMode is SeederExecutionMode.Inherit ||
            !Enum.IsDefined(options.DefaultExecutionMode))
        {
            throw new InvalidOperationException("Seeder DefaultExecutionMode must be Concurrent or Exclusive.");
        }

        if (options.DefaultCriticality is SeederCriticality.Inherit ||
            !Enum.IsDefined(options.DefaultCriticality))
        {
            throw new InvalidOperationException("Seeder DefaultCriticality must be Required or Optional.");
        }

        if (options.DefaultFailureBehavior is SeederFailureBehavior.Inherit ||
            !Enum.IsDefined(options.DefaultFailureBehavior))
        {
            throw new InvalidOperationException(
                "Seeder DefaultFailureBehavior must be ContinueAndRecord or FailFast.");
        }

        if (options.DefaultMaxAttempts <= 0)
        {
            throw new InvalidOperationException("Seeder DefaultMaxAttempts must be greater than zero.");
        }

        if (options.RetryBaseDelay <= TimeSpan.Zero)
        {
            throw new InvalidOperationException("Seeder RetryBaseDelay must be greater than zero.");
        }

        if (options.RetryMaxDelay < options.RetryBaseDelay)
        {
            throw new InvalidOperationException("Seeder RetryMaxDelay must be greater than or equal to RetryBaseDelay.");
        }
    }

    private static void ValidateSeederType(Type seederType)
    {
        if (!seederType.IsClass || seederType.IsAbstract || !typeof(ISeeder).IsAssignableFrom(seederType))
        {
            throw new InvalidOperationException(
                $"Discovered seeder '{GetTypeName(seederType)}' must be a concrete class implementing {nameof(ISeeder)}.");
        }
    }

    private static SeederExecutionMode ResolveExecutionMode(
        SeederExecutionMode declared,
        Monica.Modules.ModuleSeederOption options)
    {
        if (!Enum.IsDefined(declared))
        {
            throw new InvalidOperationException($"Seeder policy contains unknown execution mode value '{declared}'.");
        }

        return declared == SeederExecutionMode.Inherit ? options.DefaultExecutionMode : declared;
    }

    private static SeederCriticality ResolveCriticality(
        SeederCriticality declared,
        Monica.Modules.ModuleSeederOption options)
    {
        if (!Enum.IsDefined(declared))
        {
            throw new InvalidOperationException($"Seeder policy contains unknown criticality value '{declared}'.");
        }

        return declared == SeederCriticality.Inherit ? options.DefaultCriticality : declared;
    }

    private static SeederFailureBehavior ResolveFailureBehavior(
        SeederFailureBehavior declared,
        Monica.Modules.ModuleSeederOption options)
    {
        if (!Enum.IsDefined(declared))
        {
            throw new InvalidOperationException($"Seeder policy contains unknown failure behavior value '{declared}'.");
        }

        return declared == SeederFailureBehavior.Inherit ? options.DefaultFailureBehavior : declared;
    }

    private static SeederPolicySource GetSource(bool inherited)
    {
        return inherited ? SeederPolicySource.ModuleDefault : SeederPolicySource.SeederOverride;
    }

    private static ImmutableArray<Type> GetDependencies(Type seederType)
    {
        var dependencies = seederType
            .GetCustomAttributes(inherit: true)
            .Select(static attribute => attribute.GetType())
            .Where(static attributeType =>
                attributeType.IsGenericType &&
                attributeType.GetGenericTypeDefinition() == typeof(SeederDependsOnAttribute<>))
            .Select(static attributeType => attributeType.GetGenericArguments()[0])
            .OrderBy(GetTypeName, StringComparer.Ordinal)
            .ToImmutableArray();

        var duplicate = dependencies
            .GroupBy(static dependency => dependency)
            .FirstOrDefault(static group => group.Count() > 1);
        if (duplicate is not null)
        {
            throw new InvalidOperationException(
                $"Seeder '{GetTypeName(seederType)}' declares dependency '{GetTypeName(duplicate.Key)}' more than once.");
        }

        return dependencies;
    }

    private static void ValidateDependencies(
        Type seederType,
        ImmutableArray<Type> dependencies,
        FrozenSet<Type> discoveredTypes)
    {
        foreach (var dependency in dependencies)
        {
            if (dependency == seederType)
            {
                throw new InvalidOperationException($"Seeder '{GetTypeName(seederType)}' cannot depend on itself.");
            }

            if (!discoveredTypes.Contains(dependency))
            {
                throw new InvalidOperationException(
                    $"Seeder '{GetTypeName(seederType)}' depends on undiscovered seeder '{GetTypeName(dependency)}'.");
            }
        }
    }

    private void ValidateCriticality()
    {
        foreach (var node in Nodes.Where(static node => node.Criticality == SeederCriticality.Required))
        {
            var optionalDependency = node.Dependencies
                .Select(dependency => NodesByType[dependency])
                .FirstOrDefault(static dependency => dependency.Criticality == SeederCriticality.Optional);
            if (optionalDependency is not null)
            {
                throw new InvalidOperationException(
                    $"Required seeder '{node.SeederTypeName}' cannot depend on optional seeder '{optionalDependency.SeederTypeName}'.");
            }
        }
    }

    private void ValidateAcyclic()
    {
        var remainingDependencies = Nodes.ToDictionary(
            static node => node.SeederType,
            static node => node.Dependencies.Length);
        var dependents = Nodes.ToDictionary(
            static node => node.SeederType,
            static _ => new List<Type>());
        foreach (var node in Nodes)
        {
            foreach (var dependency in node.Dependencies)
            {
                dependents[dependency].Add(node.SeederType);
            }
        }

        var ready = new SortedSet<Type>(
            remainingDependencies.Where(static pair => pair.Value == 0).Select(static pair => pair.Key),
            TYPE_NAME_COMPARER);
        var visited = 0;
        while (ready.Count > 0)
        {
            var current = ready.Min!;
            ready.Remove(current);
            visited++;
            foreach (var dependent in dependents[current].OrderBy(GetTypeName, StringComparer.Ordinal))
            {
                remainingDependencies[dependent]--;
                if (remainingDependencies[dependent] == 0)
                {
                    ready.Add(dependent);
                }
            }
        }

        if (visited == Nodes.Length)
        {
            return;
        }

        var cycleMembers = remainingDependencies
            .Where(static pair => pair.Value > 0)
            .Select(static pair => GetTypeName(pair.Key));
        throw new InvalidOperationException(
            $"Seeder dependency graph contains a cycle involving: {string.Join(", ", cycleMembers)}.");
    }

    private static string GetTypeName(Type type) => type.GetCleanFullName();
}
