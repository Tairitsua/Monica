using System.Collections;
using System.Collections.Concurrent;
using System.Globalization;
using System.Reflection;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Http.Extensions;
using Microsoft.AspNetCore.Routing.Patterns;
using Monica.Core.JsonSerialization.Models;
using Monica.Tool.Extensions;

namespace Monica.Rpc.Extensions;

/// <summary>
/// Provides request serialization helpers used by generated HTTP RPC clients.
/// </summary>
public static class HttpApiRequestExtensions
{
    private static readonly ConcurrentDictionary<Type, IReadOnlyDictionary<string, PropertyInfo>> ReadableProperties = new();
    private static readonly ConcurrentDictionary<string, IReadOnlyDictionary<string, RoutePatternParameterPart>> RouteParameters = new();

    private static readonly Regex RoutePlaceholderPattern = new(
        @"\{(?:\*{1,2})?(?<name>[A-Za-z_][A-Za-z0-9_]*)(?:[^}]*)\}",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>
    /// Builds an escaped request URI by substituting route placeholders from request properties and,
    /// when requested, appending all remaining readable properties as query parameters.
    /// </summary>
    /// <typeparam name="TRequest">The request model type.</typeparam>
    /// <param name="request">The request model to serialize.</param>
    /// <param name="routeTemplate">The complete route template.</param>
    /// <param name="includeQueryString">
    /// Whether properties that are not bound to route placeholders should be appended to the query string.
    /// </param>
    /// <param name="dateTimeFormat">The host-owned wire representation for <see cref="DateTime" /> values.</param>
    /// <returns>The escaped relative request URI.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="request"/> is <see langword="null"/>.</exception>
    /// <exception cref="InvalidOperationException">
    /// A route placeholder has no matching readable property, or a required route value is <see langword="null"/>.
    /// </exception>
    public static string BuildApiRequestUri<TRequest>(
        this TRequest request,
        string routeTemplate,
        bool includeQueryString,
        DateTimeWireFormat dateTimeFormat)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(dateTimeFormat);

        if (!includeQueryString && !RoutePlaceholderPattern.IsMatch(routeTemplate))
        {
            return routeTemplate;
        }

        var properties = GetReadableProperties(request.GetType());
        var routeParameters = GetRouteParameters(routeTemplate);
        var pathBoundPropertyNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var resolvedSegments = new List<string>();
        foreach (var segment in routeTemplate.Split('/'))
        {
            var omitSegment = false;
            var resolvedSegment = RoutePlaceholderPattern.Replace(segment, match =>
            {
                var placeholderName = match.Groups["name"].Value;
                if (!properties.TryGetValue(placeholderName, out var property))
                {
                    throw new InvalidOperationException(
                        $"Route placeholder '{{{placeholderName}}}' has no matching readable property on request type '{request.GetType().FullName}'.");
                }

                if (!routeParameters.TryGetValue(placeholderName, out var parameter))
                {
                    throw new InvalidOperationException(
                        $"Route placeholder '{{{placeholderName}}}' is not a valid route parameter.");
                }

                pathBoundPropertyNames.Add(property.Name);
                var value = property.GetValue(request) ?? parameter.Default;
                if (value is not null)
                {
                    return Uri.EscapeDataString(FormatValue(value, dateTimeFormat));
                }

                if (parameter.IsOptional)
                {
                    omitSegment = true;
                    return string.Empty;
                }

                throw new InvalidOperationException(
                    $"Route property '{property.Name}' on request type '{request.GetType().FullName}' cannot be null.");
            });

            if (!omitSegment)
            {
                resolvedSegments.Add(resolvedSegment);
            }
        }

        var route = string.Join("/", resolvedSegments);

        if (!includeQueryString)
        {
            return route;
        }

        var builder = new QueryBuilder();
        foreach (var property in properties.Values.Where(
                     property => !pathBoundPropertyNames.Contains(property.Name)))
        {
            var value = property.GetValue(request);
            if (value is null)
            {
                continue;
            }

            var queryName = property.Name.ToCamelCase();
            if (value is IEnumerable values and not string)
            {
                foreach (var item in values)
                {
                    if (item is not null)
                    {
                        builder.Add(queryName, FormatValue(item, dateTimeFormat));
                    }
                }

                continue;
            }

            builder.Add(queryName, FormatValue(value, dateTimeFormat));
        }

        return route + builder;
    }

    private static IReadOnlyDictionary<string, PropertyInfo> GetReadableProperties(Type requestType)
    {
        return ReadableProperties.GetOrAdd(requestType, static type => CreateReadableProperties(type));
    }

    private static IReadOnlyDictionary<string, RoutePatternParameterPart> GetRouteParameters(string routeTemplate)
    {
        return RouteParameters.GetOrAdd(routeTemplate, static template =>
            RoutePatternFactory.Parse(template).Parameters.ToDictionary(
                static parameter => parameter.Name,
                StringComparer.OrdinalIgnoreCase));
    }

    private static IReadOnlyDictionary<string, PropertyInfo> CreateReadableProperties(Type requestType)
    {
        var properties = new Dictionary<string, PropertyInfo>(StringComparer.OrdinalIgnoreCase);
        foreach (var property in requestType.GetProperties(BindingFlags.Instance | BindingFlags.Public)
                     .Where(static property => property.CanRead && property.GetIndexParameters().Length == 0))
        {
            properties.TryAdd(property.Name, property);
        }

        return properties;
    }

    private static string FormatValue(object value, DateTimeWireFormat dateTimeFormat)
    {
        return value switch
        {
            DateTime dateTime => dateTimeFormat.Format(dateTime),
            DateTimeOffset dateTimeOffset => dateTimeOffset.ToString("O", CultureInfo.InvariantCulture),
            IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
            _ => value.ToString() ?? string.Empty
        };
    }
}
