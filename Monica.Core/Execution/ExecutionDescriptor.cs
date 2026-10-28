using System.Collections.Concurrent;
using System.Data;
using System.Reflection;
using System.Runtime.CompilerServices;
using Monica.Tool.Extensions;

namespace Monica.Core.Execution;

/// <summary>
/// Describes one reusable execution boundary independently of a concrete invocation.
/// </summary>
/// <remarks>
/// Descriptors are immutable and interned from stable boundary metadata. They are reusable for the lifetime of the
/// current host, but executed component types may be retained by downstream plan and diagnostics caches. Collectible
/// plugin unloading is therefore not supported for component types that have entered the execution pipeline. Do not
/// place request-specific state in a descriptor; use <see cref="ExecutionFeatureCollection"/> on the execution
/// context instead.
/// </remarks>
public sealed class ExecutionDescriptor
{
    private static readonly ConditionalWeakTable<Type, DescriptorCache> DESCRIPTOR_CACHES = new();

    /// <summary>
    /// Initializes an execution descriptor.
    /// </summary>
    /// <param name="point">The subsystem execution point.</param>
    /// <param name="operationKey">The stable aggregate identity of the operation.</param>
    /// <param name="displayName">The human-readable operation name.</param>
    /// <param name="componentType">The concrete or contractual component type being invoked.</param>
    /// <param name="contractType">The adapter contract used to identify the entry method, when applicable.</param>
    /// <param name="entryMethod">The concrete entry method when one is available.</param>
    /// <param name="inputType">The pipeline input type.</param>
    /// <param name="resultType">The pipeline result type.</param>
    /// <param name="isBusinessOperation">Whether the execution represents application business work.</param>
    /// <param name="transactionMode">The adapter's default transaction policy, overridden by the entry method's declaration.</param>
    private ExecutionDescriptor(
        ExecutionPoint point,
        string operationKey,
        string displayName,
        Type componentType,
        Type? contractType,
        MethodInfo? entryMethod,
        Type inputType,
        Type resultType,
        bool isBusinessOperation,
        ExecutionTransactionMode transactionMode)
    {
        ArgumentNullException.ThrowIfNull(point);
        ArgumentException.ThrowIfNullOrWhiteSpace(operationKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(displayName);
        ArgumentNullException.ThrowIfNull(componentType);
        ArgumentNullException.ThrowIfNull(inputType);
        ArgumentNullException.ThrowIfNull(resultType);

        if (!string.Equals(operationKey, operationKey.Trim(), StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "Execution operation keys must not contain leading or trailing whitespace.",
                nameof(operationKey));
        }

        if (!string.Equals(displayName, displayName.Trim(), StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "Execution display names must not contain leading or trailing whitespace.",
                nameof(displayName));
        }

        Point = point;
        OperationKey = operationKey;
        DisplayName = displayName;
        ComponentType = componentType;
        ContractType = contractType;
        EntryMethod = entryMethod;
        InputType = inputType;
        ResultType = resultType;
        IsBusinessOperation = isBusinessOperation;
        var transaction = entryMethod?.GetCustomAttribute<ExecutionTransactionAttribute>(inherit: true);
        TransactionMode = transaction?.Mode ?? transactionMode;
        if (!Enum.IsDefined(TransactionMode))
        {
            throw new ArgumentOutOfRangeException(nameof(transactionMode), TransactionMode, "The transaction mode must be defined.");
        }

        if (transaction is not null)
        {
            if (!Enum.IsDefined(transaction.IsolationLevel))
                throw new ArgumentOutOfRangeException(nameof(transaction.IsolationLevel));
            if (transaction.IsolationLevel != IsolationLevel.Unspecified)
            {
                if (TransactionMode != ExecutionTransactionMode.Automatic)
                    throw new InvalidOperationException("An isolation requirement requires an automatic transaction.");
                TransactionIsolationLevel = transaction.IsolationLevel;
            }
            ArgumentNullException.ThrowIfNull(transaction.DbContextTypes);
            if (transaction.DbContextTypes.Length > 0)
            {
                if (TransactionMode != ExecutionTransactionMode.Automatic)
                {
                    throw new InvalidOperationException("Database context selection requires an automatic transaction.");
                }

                if (transaction.DbContextTypes.Any(type => type is null)
                    || transaction.DbContextTypes.Distinct().Count() != transaction.DbContextTypes.Length)
                {
                    throw new ArgumentException("Transaction context types must be non-null and distinct.", nameof(transaction.DbContextTypes));
                }

                TransactionDbContextTypes = Array.AsReadOnly(transaction.DbContextTypes.ToArray());
            }
        }
    }

    /// <summary>
    /// Gets a memoized descriptor for a concrete entry method.
    /// </summary>
    /// <typeparam name="TInput">The pipeline input type.</typeparam>
    /// <typeparam name="TResult">The pipeline result type.</typeparam>
    /// <param name="point">The subsystem execution point.</param>
    /// <param name="componentType">The concrete or contractual component type being invoked.</param>
    /// <param name="entryMethod">The concrete entry method when one is available.</param>
    /// <param name="isBusinessOperation">Whether the execution represents application business work.</param>
    /// <param name="transactionMode">The adapter's default transaction policy, overridden by the entry method's declaration.</param>
    /// <returns>The stable descriptor shared by equivalent method boundaries.</returns>
    public static ExecutionDescriptor ForMethod<TInput, TResult>(
        ExecutionPoint point,
        Type componentType,
        MethodInfo? entryMethod,
        bool isBusinessOperation,
        ExecutionTransactionMode transactionMode)
    {
        ArgumentNullException.ThrowIfNull(point);
        ArgumentNullException.ThrowIfNull(componentType);

        var key = new DescriptorKey(
            point,
            entryMethod,
            null,
            typeof(TInput),
            typeof(TResult),
            isBusinessOperation,
            transactionMode);
        return GetOrAdd(componentType, key);
    }

    /// <summary>
    /// Gets a memoized descriptor for a component interface that exposes exactly one entry method.
    /// </summary>
    /// <typeparam name="TInput">The pipeline input type.</typeparam>
    /// <typeparam name="TResult">The pipeline result type.</typeparam>
    /// <param name="point">The subsystem execution point.</param>
    /// <param name="componentType">The concrete component type being invoked.</param>
    /// <param name="contractType">The single-method interface implemented by the component.</param>
    /// <param name="isBusinessOperation">Whether the execution represents application business work.</param>
    /// <param name="transactionMode">The adapter's default transaction policy, overridden by the mapped entry method's declaration.</param>
    /// <returns>The stable descriptor shared by equivalent interface boundaries.</returns>
    /// <exception cref="ArgumentException">
    /// Thrown when the contract is not an interface or does not expose exactly one mapped method.
    /// </exception>
    public static ExecutionDescriptor ForInterface<TInput, TResult>(
        ExecutionPoint point,
        Type componentType,
        Type contractType,
        bool isBusinessOperation,
        ExecutionTransactionMode transactionMode)
    {
        ArgumentNullException.ThrowIfNull(point);
        ArgumentNullException.ThrowIfNull(componentType);
        ArgumentNullException.ThrowIfNull(contractType);

        if (!contractType.IsInterface)
        {
            throw new ArgumentException($"Execution contract '{contractType.FullName}' must be an interface.", nameof(contractType));
        }

        var key = new DescriptorKey(
            point,
            null,
            contractType,
            typeof(TInput),
            typeof(TResult),
            isBusinessOperation,
            transactionMode);
        return GetOrAdd(componentType, key);
    }

    /// <summary>
    /// Gets the subsystem execution point.
    /// </summary>
    public ExecutionPoint Point { get; }

    /// <summary>
    /// Gets the stable identity used to aggregate diagnostics for equivalent executions.
    /// </summary>
    /// <remarks>
    /// The key includes the execution point, assembly-scoped component and contract identities, concrete method
    /// signature, and pipeline input/result types. It is intended for machine identity, not user-facing display.
    /// </remarks>
    public string OperationKey { get; }

    /// <summary>
    /// Gets the human-readable operation name used by diagnostic views and logs.
    /// </summary>
    public string DisplayName { get; }

    /// <summary>
    /// Gets the concrete or contractual component type being invoked.
    /// </summary>
    public Type ComponentType { get; }

    /// <summary>
    /// Gets the adapter contract used to identify the entry method, when one was supplied.
    /// </summary>
    public Type? ContractType { get; }

    /// <summary>
    /// Gets the concrete entry method when the adapter can identify one.
    /// </summary>
    public MethodInfo? EntryMethod { get; }

    /// <summary>
    /// Gets the pipeline input type.
    /// </summary>
    public Type InputType { get; }

    /// <summary>
    /// Gets the pipeline result type.
    /// </summary>
    public Type ResultType { get; }

    /// <summary>
    /// Gets whether the execution represents application business work.
    /// </summary>
    public bool IsBusinessOperation { get; }

    /// <summary>
    /// Gets the effective automatic transaction policy after applying the entry method's declaration to the adapter default.
    /// </summary>
    public ExecutionTransactionMode TransactionMode { get; }

    /// <summary>
    /// Gets the immutable ordered context selection declared on the entry method, or null to use the registered
    /// default selection. The persistence module validates these types and owns their transaction participation.
    /// </summary>
    public IReadOnlyList<Type>? TransactionDbContextTypes { get; }

    /// <summary>Gets the method's required isolation, or null when it uses the provider default.</summary>
    public IsolationLevel? TransactionIsolationLevel { get; }

    private static ExecutionDescriptor GetOrAdd(Type componentType, DescriptorKey key)
    {
        var cache = DESCRIPTOR_CACHES.GetValue(componentType, static _ => new DescriptorCache());
        return cache.Descriptors.GetOrAdd(key, descriptorKey =>
        {
            var descriptor = descriptorKey.CreateDescriptor(componentType);
            // Different adapter defaults can resolve to the same method declaration and must share one plan.
            return descriptor.TransactionMode == descriptorKey.TransactionMode
                ? descriptor
                : cache.Descriptors.GetOrAdd(descriptorKey with { TransactionMode = descriptor.TransactionMode }, descriptor);
        });
    }

    private readonly record struct DescriptorKey(
        ExecutionPoint Point,
        MethodInfo? EntryMethod,
        Type? ContractType,
        Type InputType,
        Type ResultType,
        bool IsBusinessOperation,
        ExecutionTransactionMode TransactionMode)
    {
        public ExecutionDescriptor CreateDescriptor(Type componentType)
        {
            var entryMethod = EntryMethod;
            if (ContractType is not null)
            {
                InterfaceMapping interfaceMapping;
                try
                {
                    interfaceMapping = componentType.GetInterfaceMap(ContractType);
                }
                catch (ArgumentException exception)
                {
                    throw new ArgumentException(
                        $"Component '{componentType.FullName}' does not implement execution contract " +
                        $"'{ContractType.FullName}'.",
                        "contractType",
                        exception);
                }

                if (interfaceMapping.TargetMethods.Length != 1)
                {
                    throw new ArgumentException(
                        $"Execution contract '{ContractType.FullName}' must map exactly one method on " +
                        $"'{componentType.FullName}', but mapped {interfaceMapping.TargetMethods.Length}.",
                        "contractType");
                }

                entryMethod = interfaceMapping.TargetMethods[0];
            }

            var displayName = $"{componentType.GetCleanFullName()}.{entryMethod?.Name ?? Point.Value}";
            var operationKey = ExecutionOperationKey.Create(
                Point,
                componentType,
                ContractType,
                entryMethod,
                InputType,
                ResultType);

            return new ExecutionDescriptor(
                Point,
                operationKey,
                displayName,
                componentType,
                ContractType,
                entryMethod,
                InputType,
                ResultType,
                IsBusinessOperation,
                TransactionMode);
        }
    }

    private sealed class DescriptorCache
    {
        public ConcurrentDictionary<DescriptorKey, ExecutionDescriptor> Descriptors { get; } = new();
    }
}
