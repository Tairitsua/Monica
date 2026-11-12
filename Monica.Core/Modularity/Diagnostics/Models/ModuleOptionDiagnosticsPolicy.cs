using System.Collections.Immutable;
using System.Linq.Expressions;
using System.Reflection;

namespace Monica.Core.Modularity.Diagnostics.Models;

/// <summary>
/// Adds host-owned sensitivity rules to the automatic diagnostics projection for one option type.
/// </summary>
/// <typeparam name="TOptions">The finalized module option type.</typeparam>
public sealed class ModuleOptionDiagnosticsPolicy<TOptions> where TOptions : class
{
    private readonly HashSet<string> _sensitivePaths = new(StringComparer.Ordinal);

    /// <summary>
    /// Marks a property path as sensitive. Redacted diagnostics disclose only presence, while the explicit
    /// sensitive-value reveal policy may reveal a bounded scalar representation.
    /// </summary>
    /// <typeparam name="TValue">The selected property type.</typeparam>
    /// <param name="property">A direct or nested property path rooted at the option object.</param>
    /// <returns>This policy.</returns>
    public ModuleOptionDiagnosticsPolicy<TOptions> MarkSensitive<TValue>(
        Expression<Func<TOptions, TValue>> property)
    {
        _sensitivePaths.Add(ReadPropertyPath(property));
        return this;
    }

    internal ModuleOptionDiagnosticsPolicyDefinition Build() => new(
        _sensitivePaths.ToImmutableHashSet(StringComparer.Ordinal));

    private static string ReadPropertyPath<TValue>(Expression<Func<TOptions, TValue>> property)
    {
        ArgumentNullException.ThrowIfNull(property);
        Expression current = property.Body;
        if (current is UnaryExpression { NodeType: ExpressionType.Convert or ExpressionType.ConvertChecked } unary)
        {
            current = unary.Operand;
        }

        var members = new Stack<string>();
        while (current is MemberExpression member)
        {
            if (member.Member is not PropertyInfo)
            {
                throw new ArgumentException("Module option diagnostics rules must select properties only.", nameof(property));
            }

            members.Push(member.Member.Name);
            current = member.Expression
                ?? throw new ArgumentException("Static properties cannot be selected.", nameof(property));
        }

        if (current != property.Parameters[0] || members.Count == 0)
        {
            throw new ArgumentException(
                "Module option diagnostics rules must be a direct or nested property path rooted at the option object.",
                nameof(property));
        }

        return string.Join('.', members);
    }
}

internal sealed record ModuleOptionDiagnosticsPolicyDefinition(
    ImmutableHashSet<string> SensitivePaths)
{
    internal static ModuleOptionDiagnosticsPolicyDefinition Empty { get; } = new(
        ImmutableHashSet<string>.Empty.WithComparer(StringComparer.Ordinal));
}
