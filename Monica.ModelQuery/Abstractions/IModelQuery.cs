using System.Linq.Expressions;
using Monica.ModelQuery.Models;

namespace Monica.ModelQuery.Abstractions;

/// <summary>
/// Resolves model fields and composes filters, searches and projections for queryable and in-memory sources.
/// Results are deferred; queryable operations remain subject to the source provider's translation capabilities.
/// </summary>
/// <typeparam name="TModel">The reference-type model described by this host's query metadata.</typeparam>
public interface IModelQuery<TModel> where TModel : class
{
    /// <summary>Gets all fields in the host-owned immutable model description.</summary>
    IReadOnlyList<QueryField> GetFields();
    /// <summary>Resolves an inclusion or exclusion; unknown names and an empty result are rejected.</summary>
    IReadOnlyList<QueryField> ResolveFields(FieldSelection selection);
    /// <summary>Matches names independently, retaining unmatched names for the caller to handle.</summary>
    FieldMatchResult MatchFields(IEnumerable<string> names);
    /// <summary>Parses a filter and records its referenced fields for reuse with either source kind in this host.</summary>
    /// <remarks>A parsed filter belongs to the host that created it and must be parsed again in another host.</remarks>
    ParsedFilter<TModel> ParseFilter(string filter);
    /// <summary>Compiles a filter for in-memory evaluation.</summary>
    Func<TModel, bool> CompileFilter(string filter);
    /// <summary>Compiles a parsed filter for in-memory evaluation.</summary>
    Func<TModel, bool> CompileFilter(ParsedFilter<TModel> filter);
    /// <summary>Composes a filter in the source query.</summary>
    IQueryable<TModel> ApplyFilter(IQueryable<TModel> source, string filter);
    /// <summary>Composes a parsed filter in the source query.</summary>
    IQueryable<TModel> ApplyFilter(IQueryable<TModel> source, ParsedFilter<TModel> filter);
    /// <summary>Composes a condition for a selected property in the source query.</summary>
    IQueryable<TModel> ApplyFilter(IQueryable<TModel> source, Expression<Func<TModel, object>> selector, EFieldConditions condition, string value);
    /// <summary>Filters a sequence using in-memory semantics.</summary>
    IEnumerable<TModel> ApplyFilter(IEnumerable<TModel> source, string filter);
    /// <summary>Filters a sequence using a parsed filter and in-memory semantics.</summary>
    IEnumerable<TModel> ApplyFilter(IEnumerable<TModel> source, ParsedFilter<TModel> filter);
    /// <summary>Filters a sequence by a selected property using in-memory semantics.</summary>
    IEnumerable<TModel> ApplyFilter(IEnumerable<TModel> source, Expression<Func<TModel, object>> selector, EFieldConditions condition, string value);
    /// <summary>Searches eligible fields, or the explicitly named fields, in the source query.</summary>
    /// <remarks>String values without % are wrapped in %; supplied % and _ characters retain LIKE wildcard semantics.</remarks>
    IQueryable<TModel> ApplySearch(IQueryable<TModel> source, string searchText, IReadOnlyList<string>? searchFields = null);
    /// <summary>Searches eligible fields, or the explicitly named fields, in a sequence.</summary>
    /// <remarks>String values without % are wrapped in %; supplied % and _ characters retain LIKE wildcard semantics.</remarks>
    IEnumerable<TModel> ApplySearch(IEnumerable<TModel> source, string searchText, IReadOnlyList<string>? searchFields = null);
    /// <summary>Projects selected fields in the source query.</summary>
    IQueryable SelectFields(IQueryable<TModel> source, FieldSelection selection);
    /// <summary>Projects selected fields in a sequence.</summary>
    IEnumerable<dynamic> SelectFields(IEnumerable<TModel> source, FieldSelection selection);
}
