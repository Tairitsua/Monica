using System.Collections.ObjectModel;
using System.Reflection;

namespace Monica.Core.TypeDiscovery.Models;

/// <summary>
/// Provides a host-scoped, lazily cached structural view of one discovered business type.
/// </summary>
/// <remarks>
/// A discovery compilation creates at most one shape for each scanned type. Reflection facts are loaded on demand and
/// retained by the shape, so query evaluation and commit code share the same interface, hierarchy, attribute, and
/// constructor data. The object is safe for concurrent reads after it has been published in a match result.
/// </remarks>
public sealed class BusinessTypeShape
{
    private readonly object _factGate = new();
    private Dictionary<Type, bool>? _assignability;
    private IReadOnlyList<Type>? _baseTypes;
    private IReadOnlyList<ConstructorInfo>? _constructors;
    private IReadOnlyList<CustomAttributeData>? _customAttributes;
    private IReadOnlyList<CustomAttributeData>? _inheritedCustomAttributes;
    private IReadOnlyList<Type>? _interfaces;
    private Dictionary<(Type AttributeType, bool Inherit), IReadOnlyList<Attribute>>? _materializedAttributes;
    private IReadOnlyDictionary<Type, IReadOnlyList<OpenGenericInterfaceMatch>>? _openGenericInterfaces;
    private Dictionary<Type, BusinessTypeShape>? _constructedGenericShapes;

    internal BusinessTypeShape(Type type)
    {
        ArgumentNullException.ThrowIfNull(type);
        Type = type;
    }

    /// <summary>
    /// Gets the reflected runtime type represented by this shape.
    /// </summary>
    public Type Type { get; }

    /// <summary>
    /// Gets whether the represented type is a class.
    /// </summary>
    public bool IsClass => Type.IsClass;

    /// <summary>
    /// Gets whether the represented type is an interface.
    /// </summary>
    public bool IsInterface => Type.IsInterface;

    /// <summary>
    /// Gets whether the represented type is a value type.
    /// </summary>
    public bool IsValueType => Type.IsValueType;

    /// <summary>
    /// Gets whether the represented type is abstract.
    /// </summary>
    public bool IsAbstract => Type.IsAbstract;

    /// <summary>
    /// Gets whether the represented type is a non-abstract class.
    /// </summary>
    /// <remarks>Open generic class definitions can be concrete; use <see cref="IsClosedType"/> when they must be excluded.</remarks>
    public bool IsConcreteClass => Type.IsClass && !Type.IsAbstract;

    /// <summary>
    /// Gets whether the represented type has no unbound generic parameters.
    /// </summary>
    public bool IsClosedType => !Type.ContainsGenericParameters;

    /// <summary>
    /// Gets a cached structural shape for a closed construction of this generic type definition.
    /// </summary>
    /// <param name="typeArguments">The concrete arguments satisfying the generic definition's constraints.</param>
    /// <returns>The shape owned by this definition for the constructed type.</returns>
    /// <exception cref="InvalidOperationException">This shape does not represent a generic type definition.</exception>
    /// <exception cref="ArgumentException">The arguments are invalid or leave unbound generic parameters.</exception>
    /// <remarks>
    /// Consumers may use this when a discovered contract definition is closed by another discovered type, such as
    /// an event handler. This does not add the constructed type to other modules' discovery matches.
    /// </remarks>
    public BusinessTypeShape MakeGenericType(params Type[] typeArguments)
    {
        var constructedType = Type.MakeGenericType(typeArguments);
        if (constructedType.ContainsGenericParameters)
        {
            throw new ArgumentException("A constructed business-type shape must be closed.", nameof(typeArguments));
        }

        lock (_factGate)
        {
            _constructedGenericShapes ??= [];
            if (!_constructedGenericShapes.TryGetValue(constructedType, out var shape))
            {
                shape = new BusinessTypeShape(constructedType);
                _constructedGenericShapes.Add(constructedType, shape);
            }

            return shape;
        }
    }

    /// <summary>
    /// Gets every interface implemented by the represented type, including inherited interfaces.
    /// </summary>
    public IReadOnlyList<Type> Interfaces => GetOrCreate(ref _interfaces, CreateInterfaces);

    /// <summary>
    /// Gets the represented type's base-class chain, nearest base first and including <see cref="object"/> when present.
    /// </summary>
    public IReadOnlyList<Type> BaseTypes => GetOrCreate(ref _baseTypes, CreateBaseTypes);

    /// <summary>
    /// Gets the represented type's public instance constructors.
    /// </summary>
    public IReadOnlyList<ConstructorInfo> Constructors => GetOrCreate(ref _constructors, CreateConstructors);

    /// <summary>
    /// Gets metadata for attributes declared directly on the represented type without constructing attribute instances.
    /// </summary>
    public IReadOnlyList<CustomAttributeData> CustomAttributes =>
        GetOrCreate(ref _customAttributes, CreateCustomAttributes);

    /// <summary>
    /// Gets closed open-generic interface implementations grouped by their generic type definition.
    /// </summary>
    /// <remarks>
    /// The index is built in one pass over <see cref="Interfaces"/>. Interfaces that still contain unbound generic
    /// parameters are not closed implementations and are therefore omitted.
    /// </remarks>
    public IReadOnlyDictionary<Type, IReadOnlyList<OpenGenericInterfaceMatch>> OpenGenericInterfaces =>
        GetOrCreate(ref _openGenericInterfaces, CreateOpenGenericInterfaces);

    /// <summary>
    /// Determines whether this type can be assigned to <paramref name="targetType"/>.
    /// </summary>
    /// <param name="targetType">The destination runtime type.</param>
    /// <returns><see langword="true"/> when assignment is valid; otherwise, <see langword="false"/>.</returns>
    public bool IsAssignableTo(Type targetType)
    {
        ArgumentNullException.ThrowIfNull(targetType);

        lock (_factGate)
        {
            _assignability ??= [];
            if (_assignability.TryGetValue(targetType, out var assignable))
            {
                return assignable;
            }

            assignable = targetType.IsAssignableFrom(Type);
            _assignability.Add(targetType, assignable);
            return assignable;
        }
    }

    /// <summary>
    /// Determines whether this type derives from <paramref name="baseType"/>.
    /// </summary>
    /// <param name="baseType">The class to locate in the base-class chain.</param>
    /// <returns>
    /// <see langword="true"/> when the class occurs in the chain; otherwise, <see langword="false"/>. A type is not a
    /// subclass of itself.
    /// </returns>
    public bool IsSubclassOf(Type baseType)
    {
        ArgumentNullException.ThrowIfNull(baseType);
        return BaseTypes.Contains(baseType);
    }

    /// <summary>
    /// Determines whether the represented type has a compatible attribute.
    /// </summary>
    /// <param name="attributeType">The attribute base or concrete type to locate.</param>
    /// <param name="inherit">
    /// <see langword="true"/> to examine inheritable declarations on base classes; otherwise, only declarations on the
    /// represented type are considered.
    /// </param>
    /// <returns><see langword="true"/> when a compatible declaration exists; otherwise, <see langword="false"/>.</returns>
    public bool HasAttribute(Type attributeType, bool inherit = false)
    {
        ValidateAttributeType(attributeType);
        var attributes = inherit
            ? GetOrCreate(ref _inheritedCustomAttributes, CreateInheritedCustomAttributes)
            : CustomAttributes;

        return attributes.Any(attribute => attributeType.IsAssignableFrom(attribute.AttributeType));
    }

    /// <summary>
    /// Materializes compatible attribute instances when commit code needs attribute values rather than presence alone.
    /// </summary>
    /// <param name="attributeType">The attribute base or concrete type to materialize.</param>
    /// <param name="inherit">Whether inheritable declarations on base classes should be included.</param>
    /// <returns>A cached, read-only list of materialized attributes.</returns>
    /// <remarks>
    /// Structural queries use <see cref="CustomAttributeData"/> and do not construct attributes. This method is the
    /// explicit slower path, and each attribute-type/inheritance pair is materialized at most once per shape.
    /// </remarks>
    public IReadOnlyList<Attribute> GetAttributes(Type attributeType, bool inherit = false)
    {
        ValidateAttributeType(attributeType);
        var key = (attributeType, inherit);

        lock (_factGate)
        {
            _materializedAttributes ??= [];
            if (_materializedAttributes.TryGetValue(key, out var attributes))
            {
                return attributes;
            }

            attributes = Array.AsReadOnly(Type.GetCustomAttributes(attributeType, inherit).Cast<Attribute>().ToArray());
            _materializedAttributes.Add(key, attributes);
            return attributes;
        }
    }

    /// <summary>
    /// Gets the first materialized attribute compatible with <typeparamref name="TAttribute"/>.
    /// </summary>
    /// <typeparam name="TAttribute">The attribute base or concrete type to materialize.</typeparam>
    /// <param name="inherit">Whether inheritable declarations on base classes should be included.</param>
    /// <returns>The first compatible attribute in reflection order, or <see langword="null"/> when none exists.</returns>
    public TAttribute? GetAttribute<TAttribute>(bool inherit = false)
        where TAttribute : Attribute
    {
        return GetAttributes(typeof(TAttribute), inherit).OfType<TAttribute>().FirstOrDefault();
    }

    /// <summary>
    /// Gets all materialized attributes compatible with <typeparamref name="TAttribute"/>.
    /// </summary>
    /// <typeparam name="TAttribute">The attribute base or concrete type to materialize.</typeparam>
    /// <param name="inherit">Whether inheritable declarations on base classes should be included.</param>
    /// <returns>A read-only list in reflection order.</returns>
    public IReadOnlyList<TAttribute> GetAttributes<TAttribute>(bool inherit = false)
        where TAttribute : Attribute
    {
        return Array.AsReadOnly(
            GetAttributes(typeof(TAttribute), inherit)
                .OfType<TAttribute>()
                .ToArray());
    }

    /// <summary>
    /// Gets closed implementations of <paramref name="openGenericInterface"/> without repeating interface reflection.
    /// </summary>
    /// <param name="openGenericInterface">An interface generic type definition.</param>
    /// <returns>All closed implementations in runtime interface order, or an empty list when none exist.</returns>
    public IReadOnlyList<OpenGenericInterfaceMatch> GetOpenGenericInterfaceMatches(Type openGenericInterface)
    {
        ArgumentNullException.ThrowIfNull(openGenericInterface);
        if (!openGenericInterface.IsInterface || !openGenericInterface.IsGenericTypeDefinition)
        {
            throw new ArgumentException(
                "An open-generic interface lookup requires an interface generic type definition.",
                nameof(openGenericInterface));
        }

        return OpenGenericInterfaces.TryGetValue(openGenericInterface, out var matches) ? matches : [];
    }

    private TFact GetOrCreate<TFact>(ref TFact? field, Func<TFact> factory)
        where TFact : class
    {
        if (Volatile.Read(ref field) is { } value)
        {
            return value;
        }

        lock (_factGate)
        {
            return field ??= factory();
        }
    }

    private IReadOnlyList<Type> CreateInterfaces() => Array.AsReadOnly(Type.GetInterfaces());

    private IReadOnlyList<Type> CreateBaseTypes()
    {
        var baseTypes = new List<Type>();
        for (var baseType = Type.BaseType; baseType is not null; baseType = baseType.BaseType)
        {
            baseTypes.Add(baseType);
        }

        return baseTypes.AsReadOnly();
    }

    private IReadOnlyList<ConstructorInfo> CreateConstructors() => Array.AsReadOnly(Type.GetConstructors());

    private IReadOnlyList<CustomAttributeData> CreateCustomAttributes()
    {
        return Array.AsReadOnly(Type.GetCustomAttributesData().ToArray());
    }

    private IReadOnlyList<CustomAttributeData> CreateInheritedCustomAttributes()
    {
        var attributes = new List<CustomAttributeData>(CustomAttributes);
        foreach (var baseType in BaseTypes)
        {
            foreach (var attribute in baseType.GetCustomAttributesData())
            {
                if (IsAttributeInherited(attribute.AttributeType))
                {
                    attributes.Add(attribute);
                }
            }
        }

        return attributes.AsReadOnly();
    }

    private IReadOnlyDictionary<Type, IReadOnlyList<OpenGenericInterfaceMatch>> CreateOpenGenericInterfaces()
    {
        var matchesByDefinition = new Dictionary<Type, List<OpenGenericInterfaceMatch>>();
        foreach (var implementedInterface in Interfaces)
        {
            if (!implementedInterface.IsConstructedGenericType || implementedInterface.ContainsGenericParameters)
            {
                continue;
            }

            var genericDefinition = implementedInterface.GetGenericTypeDefinition();
            if (!matchesByDefinition.TryGetValue(genericDefinition, out var matches))
            {
                matches = [];
                matchesByDefinition.Add(genericDefinition, matches);
            }

            matches.Add(new OpenGenericInterfaceMatch(Type, implementedInterface));
        }

        var readOnlyMatches = matchesByDefinition.ToDictionary(
            static pair => pair.Key,
            static pair => (IReadOnlyList<OpenGenericInterfaceMatch>)pair.Value.AsReadOnly());

        return new ReadOnlyDictionary<Type, IReadOnlyList<OpenGenericInterfaceMatch>>(readOnlyMatches);
    }

    private static bool IsAttributeInherited(Type attributeType)
    {
        for (var candidate = attributeType; candidate is not null; candidate = candidate.BaseType)
        {
            var usage = candidate
                .GetCustomAttributesData()
                .FirstOrDefault(static data => data.AttributeType == typeof(AttributeUsageAttribute));
            if (usage is null)
            {
                continue;
            }

            var inherited = usage.NamedArguments.FirstOrDefault(
                static argument => argument.MemberName == nameof(AttributeUsageAttribute.Inherited));
            return inherited.TypedValue.Value is not bool value || value;
        }

        // AttributeUsageAttribute defaults Inherited to true when no declaration overrides it.
        return true;
    }

    private static void ValidateAttributeType(Type attributeType)
    {
        ArgumentNullException.ThrowIfNull(attributeType);
        if (!typeof(Attribute).IsAssignableFrom(attributeType))
        {
            throw new ArgumentException("An attribute lookup requires an Attribute-derived type.", nameof(attributeType));
        }
    }
}
