using System.Reflection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Monica.Configuration.Exceptions;
using Monica.Configuration.Models;
using Monica.Configuration.Services.Support;

namespace Monica.Configuration.Binding;

/// <summary>
/// Shares production binding with complete validation and bootstrap. Scalar conversion uses the Microsoft binder;
/// configured collections replace CLR defaults while providers retain indexed-key overlay precedence.
/// </summary>
internal static class MonicaConfigurationBinder
{
    public static TOptions Get<TOptions>(IConfiguration configuration) where TOptions : class, new()
    {
        var options = new TOptions();
        Bind(configuration, options);
        return options;
    }

    public static TOptions Get<TOptions>(IConfiguration configuration, string sectionPath) where TOptions : class, new()
    {
        var options = new TOptions();
        Bind(configuration, sectionPath, options);
        return options;
    }

    public static OptionsBuilder<TOptions> BindOptions<TOptions>(OptionsBuilder<TOptions> optionsBuilder,
        IConfiguration configuration) where TOptions : class
    {
        optionsBuilder.Services.AddSingleton<IOptionsChangeTokenSource<TOptions>>(
            new ConfigurationChangeTokenSource<TOptions>(optionsBuilder.Name, configuration));
        optionsBuilder.Services.AddSingleton<IConfigureOptions<TOptions>>(
            new ConfigureNamedOptions<TOptions>(optionsBuilder.Name, options => Bind(configuration, options)));
        return optionsBuilder;
    }

    public static OptionsBuilder<TOptions> BindOptions<TOptions>(OptionsBuilder<TOptions> optionsBuilder,
        IConfiguration configuration, string sectionPath, string definitionKey) where TOptions : class
    {
        optionsBuilder.Services.AddSingleton<IOptionsChangeTokenSource<TOptions>>(
            new ConfigurationChangeTokenSource<TOptions>(optionsBuilder.Name, configuration));
        optionsBuilder.Services.AddSingleton<IConfigureOptions<TOptions>>(provider =>
            new ConfigureNamedOptions<TOptions>(optionsBuilder.Name, options =>
            {
                try { Bind(configuration, sectionPath, options); }
                catch (Exception exception)
                {
                    var fault = exception is ConfigurationValidationExecutionException typed
                        ? new ConfigurationValidationExecutionException(definitionKey, typed.LogicalPath, typed.Stage, typed.Kind)
                        : new ConfigurationValidationExecutionException(definitionKey, LogicalPath.Root, "options-binding",
                            innerException: exception);
                    provider.GetRequiredService<ConfigurationOptionsValidationDiagnostics>().RecordFault(definitionKey, fault);
                    throw fault;
                }
            }));
        return optionsBuilder;
    }

    public static void Bind(IConfiguration configuration, object options)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(options);
        BindObject(configuration, options, new HashSet<object>(ReferenceEqualityComparer.Instance));
    }

    public static void Bind(IConfiguration configuration, string sectionPath, object options)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(options);
        var view = new ConfigurationShapeView(configuration);
        if (view.TryGetShape(sectionPath, out var shape)
            && shape is not (ConfigurationValueShape.Object or ConfigurationValueShape.Container))
            throw new ConfigurationValidationExecutionException(string.Empty, LogicalPath.Root, "root-configuration-shape",
                ConfigurationValidationIssueKind.Contract);
        BindObject(view.GetSection(sectionPath), options, new HashSet<object>(ReferenceEqualityComparer.Instance), view);
    }

    private static void BindObject(IConfiguration configuration, object instance, ISet<object> ancestors,
        ConfigurationShapeView? view = null)
    {
        if (!ancestors.Add(instance)) throw new InvalidOperationException("Configured object defaults must be acyclic.");
        try
        {
            foreach (var property in instance.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance)
                         .Where(property => property.GetMethod is { IsPublic: true } && property.GetIndexParameters().Length == 0))
            {
                var key = property.GetCustomAttribute<ConfigurationKeyNameAttribute>()?.Name ?? property.Name;
                var section = configuration.GetSection(key);
                var present = view?.TryGetShape(section.Path, out _) == true || section.Value is not null || section.GetChildren().Any();
                if (!present) continue;
                var writable = property.SetMethod is { IsPublic: true };
                var current = property.GetValue(instance);
                if (!writable && (IsScalar(Nullable.GetUnderlyingType(property.PropertyType) ?? property.PropertyType) || current is null)) continue;
                var value = BindValue(property.PropertyType, section, current, ancestors, view);
                if (writable) property.SetValue(instance, value);
                else if (!ReferenceEquals(value, current)) ReplaceReadOnlyCollection(current!, value);
            }
        }
        finally { ancestors.Remove(instance); }
    }

    private static object? BindValue(Type type, IConfigurationSection section, object? current,
        ISet<object> ancestors, ConfigurationShapeView? view)
    {
        var shape = default(ConfigurationValueShape);
        var hasShape = view?.TryGetShape(section.Path, out shape) == true;
        if (hasShape && shape == ConfigurationValueShape.AmbiguousNull && !IsScalar(Nullable.GetUnderlyingType(type) ?? type))
            throw new ConfigurationValidationExecutionException(string.Empty, LogicalPath.Root, "ambiguous-container-shape",
                ConfigurationValidationIssueKind.Contract);
        if (hasShape && shape is ConfigurationValueShape.Null or ConfigurationValueShape.AmbiguousNull)
        {
            if (type.IsValueType && Nullable.GetUnderlyingType(type) is null)
                throw new InvalidOperationException("Explicit null cannot bind a non-nullable value.");
            return null;
        }

        var actual = Nullable.GetUnderlyingType(type) ?? type;
        var dictionary = ConfigurationLocalDefinitionDescriptor.FindGeneric(actual, typeof(IDictionary<,>));
        if (dictionary is not null)
        {
            if (hasShape && shape is not (ConfigurationValueShape.Object or ConfigurationValueShape.Container or ConfigurationValueShape.Null))
                throw new InvalidOperationException("A dictionary requires an object configuration shape.");
            var arguments = dictionary.GetGenericArguments();
            var value = current ?? (actual.IsInterface || actual.IsAbstract
                ? Activator.CreateInstance(typeof(Dictionary<,>).MakeGenericType(arguments))!
                : Create(actual));
            ConfigurationLocalDefinitionDescriptor.FindGeneric(value.GetType(), typeof(ICollection<>))!
                .GetMethod(nameof(ICollection<object>.Clear))!.Invoke(value, null);
            var add = dictionary.GetMethod("Add", arguments)!;
            foreach (var child in section.GetChildren())
            {
                var keyConfiguration = new ConfigurationBuilder().AddInMemoryCollection(
                    new Dictionary<string, string?> { ["key"] = child.Key }).Build();
                try
                {
                    var key = keyConfiguration.GetSection("key").Get(arguments[0]);
                    add.Invoke(value, [key, BindValue(arguments[1], child, CreateDefault(arguments[1]), ancestors, view)]);
                }
                finally { (keyConfiguration as IDisposable)?.Dispose(); }
            }
            return value;
        }

        var collection = ConfigurationLocalDefinitionDescriptor.FindGeneric(actual, typeof(IEnumerable<>));
        if (actual != typeof(string) && (actual.IsArray || collection is not null))
        {
            if (hasShape && shape == ConfigurationValueShape.Object)
                throw new InvalidOperationException("A collection requires an array configuration shape.");
            if (hasShape && shape == ConfigurationValueShape.Scalar && section.Value != string.Empty)
                throw new InvalidOperationException("A collection requires indexed configuration keys.");
            var itemType = actual.IsArray ? actual.GetElementType()! : collection!.GetGenericArguments()[0];
            var items = section.GetChildren().Select(child => BindValue(itemType, child, CreateDefault(itemType), ancestors, view)).ToArray();
            if (actual.IsArray)
            {
                var array = Array.CreateInstance(itemType, items.Length);
                for (var i = 0; i < items.Length; i++) array.SetValue(items[i], i);
                return array;
            }
            var concrete = current?.GetType() ?? (actual.IsInterface || actual.IsAbstract
                ? ConfigurationLocalDefinitionDescriptor.FindGeneric(actual, typeof(ISet<>)) is not null
                    ? typeof(HashSet<>).MakeGenericType(itemType) : typeof(List<>).MakeGenericType(itemType)
                : actual);
            var value = current ?? Create(concrete);
            var mutable = ConfigurationLocalDefinitionDescriptor.FindGeneric(concrete, typeof(ICollection<>))
                ?? throw new InvalidOperationException("Configured collections must support collection binding.");
            var add = mutable.GetMethod(nameof(ICollection<object>.Add))!;
            mutable.GetMethod(nameof(ICollection<object>.Clear))!.Invoke(value, null);
            foreach (var item in items) add.Invoke(value, [item]);
            return value;
        }

        if (IsScalar(actual)) return section.Get(type);
        if (hasShape && shape is ConfigurationValueShape.Scalar or ConfigurationValueShape.List)
            throw new InvalidOperationException("An object requires an object configuration shape.");
        var instance = current ?? Create(actual);
        BindObject(section, instance, ancestors, view);
        return instance;
    }

    internal static object Create(Type type) => Activator.CreateInstance(type, nonPublic: true)
        ?? throw new InvalidOperationException("Managed options require a parameterless constructor.");

    private static void ReplaceReadOnlyCollection(object current, object? replacement)
    {
        var dictionary = ConfigurationLocalDefinitionDescriptor.FindGeneric(current.GetType(), typeof(IDictionary<,>));
        var collection = ConfigurationLocalDefinitionDescriptor.FindGeneric(current.GetType(), typeof(ICollection<>));
        if (replacement is null || collection is null)
            throw new InvalidOperationException("A configured property cannot replace its read-only value.");
        collection.GetMethod(nameof(ICollection<object>.Clear))!.Invoke(current, null);
        if (dictionary is not null)
        {
            var add = dictionary.GetMethod("Add", dictionary.GetGenericArguments())!;
            foreach (var entry in (System.Collections.IEnumerable)replacement)
                add.Invoke(current, [entry!.GetType().GetProperty("Key")!.GetValue(entry), entry.GetType().GetProperty("Value")!.GetValue(entry)]);
        }
        else
        {
            var add = collection.GetMethod(nameof(ICollection<object>.Add))!;
            foreach (var item in (System.Collections.IEnumerable)replacement) add.Invoke(current, [item]);
        }
    }

    private static object? CreateDefault(Type type) => IsScalar(Nullable.GetUnderlyingType(type) ?? type)
        || !type.IsValueType ? null : Activator.CreateInstance(type);

    internal static bool IsScalar(Type type) => type.IsPrimitive || type.IsEnum || type == typeof(string)
        || type == typeof(decimal) || type == typeof(DateTime) || type == typeof(DateTimeOffset)
        || type == typeof(TimeSpan) || type == typeof(Uri) || type == typeof(Guid);
}
