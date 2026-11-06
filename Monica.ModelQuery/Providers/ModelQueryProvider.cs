using System.Collections;
using System.Linq.Dynamic.Core;
using System.Linq.Dynamic.Core.CustomTypeProviders;
using System.Linq.Expressions;
using System.Reflection;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Monica.Core;
using Monica.ModelQuery.Abstractions;
using Monica.ModelQuery.Exceptions;
using Monica.ModelQuery.Models;
using Monica.ModelQuery.Utils;
using Monica.Tool.Extensions;
using Monica.Tool.Text;

namespace Monica.ModelQuery.Providers;

internal sealed class ModelQueryProvider<TModel>(
    IModelQuerySnapshot<TModel> snapshot,
    IModelQueryExpressionTokenizer<TModel> tokenizer,
    IMonicaApplicationOptions applicationOptions,
    ILogger<ModelQueryProvider<TModel>> logger) : IModelQuery<TModel> where TModel : class
{
    private readonly ParsingConfig _queryConfig = CreateQueryConfig();
    private readonly ParsingConfig _objectConfig = CreateObjectConfig();
    // The generator owns temporary per-token state, so each generation must complete before another starts.
    private readonly object _generationLock = new();

    public IReadOnlyList<QueryField> GetFields() => snapshot.GetFields();

    public FieldMatchResult MatchFields(IEnumerable<string> names)
    {
        ArgumentNullException.ThrowIfNull(names);
        var fields = new List<QueryField>();
        var unmatched = new List<string>();
        foreach (var name in names)
        {
            var field = string.IsNullOrWhiteSpace(name) ? null : snapshot.GetField(name.Trim());
            if (field is null) unmatched.Add(name);
            else fields.Add(field);
        }
        return new FieldMatchResult(fields, unmatched);
    }

    public IReadOnlyList<QueryField> ResolveFields(FieldSelection selection)
    {
        ArgumentNullException.ThrowIfNull(selection);
        var matches = MatchFields(selection.Names);
        if (matches.UnmatchedNames.Count > 0)
            throw new ModelQueryNormalizationException($"Unknown model query fields: {string.Join(", ", matches.UnmatchedNames)}.",
                $"Unrecognized fields: {string.Join(", ", matches.UnmatchedNames)}.");
        var fields = selection.IsExclusion
            ? snapshot.GetFields().Except(matches.MatchedFields).ToArray()
            : matches.MatchedFields.Distinct().ToArray();
        if (fields.Length == 0)
            throw new ModelQueryNormalizationException("A field selection must contain at least one field.",
                "The selected model fields were empty.");
        return Array.AsReadOnly(fields);
    }

    public ParsedFilter<TModel> ParseFilter(string filter)
    {
        ArgumentNullException.ThrowIfNull(filter);
        lock (_generationLock)
        {
            var context = tokenizer.Tokenize(filter);
            ValidateFilterSyntax(context);
            return new ParsedFilter<TModel>(snapshot, filter, context.Tokens.Select(CaptureToken).ToArray());
        }
    }

    public Func<TModel, bool> CompileFilter(string filter) => CompileFilter(ParseFilter(filter));

    public Func<TModel, bool> CompileFilter(ParsedFilter<TModel> filter)
    {
        var result = GenerateFilter(filter, inMemory: true);
        return (Func<TModel, bool>)DynamicExpressionParser.ParseLambda(_objectConfig, typeof(TModel), typeof(bool),
            result.FinalExpression, [.. result.Params]).Compile();
    }

    public IQueryable<TModel> ApplyFilter(IQueryable<TModel> source, string filter) => ApplyFilter(source, ParseFilter(filter));

    public IQueryable<TModel> ApplyFilter(IQueryable<TModel> source, ParsedFilter<TModel> filter)
    {
        ArgumentNullException.ThrowIfNull(source);
        var result = GenerateFilter(filter, inMemory: false);
        try
        {
            return source.Where(_queryConfig, result.FinalExpression, [.. result.Params]);
        }
        catch (Exception exception)
        {
            throw new ModelQueryExecutionException("The model query could not be composed.",
                $"Expression: {result.FinalExpression}; error: {exception.Message}");
        }
    }

    public IQueryable<TModel> ApplyFilter(IQueryable<TModel> source, Expression<Func<TModel, object>> selector,
        EFieldConditions condition, string value) => ApplyFilter(source, CreateCondition(GetField(selector), condition, value));

    public IEnumerable<TModel> ApplyFilter(IEnumerable<TModel> source, string filter) => ApplyFilter(source, ParseFilter(filter));

    public IEnumerable<TModel> ApplyFilter(IEnumerable<TModel> source, ParsedFilter<TModel> filter)
    {
        ArgumentNullException.ThrowIfNull(source);
        return source.Where(CompileFilter(filter));
    }

    public IEnumerable<TModel> ApplyFilter(IEnumerable<TModel> source, Expression<Func<TModel, object>> selector,
        EFieldConditions condition, string value) => ApplyFilter(source, CreateCondition(GetField(selector), condition, value));

    public IQueryable<TModel> ApplySearch(IQueryable<TModel> source, string searchText, IReadOnlyList<string>? searchFields = null) =>
        ApplyFilter(source, CreateSearch(searchText, searchFields));

    public IEnumerable<TModel> ApplySearch(IEnumerable<TModel> source, string searchText, IReadOnlyList<string>? searchFields = null) =>
        ApplyFilter(source, CreateSearch(searchText, searchFields));

    public IQueryable SelectFields(IQueryable<TModel> source, FieldSelection selection)
    {
        ArgumentNullException.ThrowIfNull(source);
        return source.Select(GetSelectExpression(ResolveFields(selection)));
    }

    public IEnumerable<dynamic> SelectFields(IEnumerable<TModel> source, FieldSelection selection)
    {
        ArgumentNullException.ThrowIfNull(source);
        var selector = GetSelectExpression(ResolveFields(selection));
        dynamic projection = DynamicExpressionParser.ParseLambda(_objectConfig, true, typeof(TModel), null, selector, null).Compile();
        return Enumerable.Select(source, projection);
    }

    private QueryField GetField(Expression<Func<TModel, object>> selector)
    {
        ArgumentNullException.ThrowIfNull(selector);
        Expression? expression = selector.Body;
        var path = new Stack<string>();
        while (expression is UnaryExpression { NodeType: ExpressionType.Convert or ExpressionType.ConvertChecked } conversion)
            expression = conversion.Operand;
        while (expression is MemberExpression { Member: PropertyInfo property } member)
        {
            path.Push(property.Name);
            expression = member.Expression;
        }
        if (expression != selector.Parameters[0] || path.Count == 0)
            throw new ArgumentException("The selector must identify a model property path.", nameof(selector));
        return snapshot.GetFields().FirstOrDefault(field =>
            (field.NavigationProperties?.Select(segment => segment.PropertyName) ?? [])
            .Append(field.ReflectionName).SequenceEqual(path, StringComparer.Ordinal))
            ?? throw new ModelQueryNormalizationException("The selected property is not available for model queries.",
                $"Model: {typeof(TModel).FullName}; property path: {string.Join('.', path)}.");
    }

    private ParsedFilter<TModel> CreateCondition(QueryField field, EFieldConditions condition, string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        var conditionName = condition.GetEnumAlias();
        if (string.IsNullOrEmpty(conditionName))
            throw new ArgumentException("The field condition is not supported.", nameof(condition));
        return new ParsedFilter<TModel>(snapshot, "x", [CreateToken(field, conditionName, value, 0)]);
    }

    private ParsedFilter<TModel> CreateSearch(string searchText, IReadOnlyList<string>? searchFields)
    {
        ArgumentNullException.ThrowIfNull(searchText);
        if (searchFields?.Any(string.IsNullOrWhiteSpace) == true)
            throw new ModelQueryNormalizationException("Search field names must not be blank.");
        var fields = searchFields is { Count: > 0 }
            ? ResolveFields(FieldSelection.Include(searchFields.ToArray()))
            : snapshot.GetFields().Where(field => !field.SearchInfo.IsIgnored && !field.SearchInfo.IsNotSupported).ToArray();
        var unsupported = fields.Where(field => field.SearchInfo.IsNotSupported).ToArray();
        if (unsupported.Length > 0)
            throw new ModelQueryNormalizationException($"Search is not supported for fields: {string.Join(", ", unsupported.Select(field => field.ReflectionName))}.",
                $"Unsupported fields: {string.Join(", ", unsupported.Select(field => field.ReflectionName))}.");
        if (fields.Count == 0)
            throw new ModelQueryNormalizationException("No searchable fields are available.", "Search has no eligible fields.");

        var expression = new StringBuilder();
        var tokens = new List<ParsedFilterToken>();
        foreach (var field in fields)
        {
            if (expression.Length > 0) expression.Append(" or ");
            var index = expression.Length;
            expression.Append('x');
            tokens.Add(CreateToken(field, "like", searchText, index));
        }
        return new ParsedFilter<TModel>(snapshot, expression.ToString(), tokens);
    }

    private NormalizedFilter GenerateFilter(ParsedFilter<TModel> filter, bool inMemory)
    {
        ArgumentNullException.ThrowIfNull(filter);
        if (!ReferenceEquals(filter.Snapshot, snapshot))
            throw new ModelQueryNormalizationException("The parsed filter belongs to another host. Parse it again in this host.");
        lock (_generationLock)
        {
            var context = new TokenizerContext(filter.Expression);
            foreach (var parsed in filter.Tokens)
            {
                var token = new FieldToken(parsed.FieldName, parsed.Condition, parsed.Value, parsed.Start, parsed.End)
                {
                    FieldInfo = parsed.Field,
                    Conditions = parsed.Conditions,
                    Features = parsed.Features,
                    ConvertedValue = CopyConvertedValue(parsed.ConvertedValue)
                };
                if (inMemory) token.Features |= EFieldConditionFeatures.UseClientSideEvaluations;
                context.Tokens.Add(token);
            }
            var result = tokenizer.GenFinalExpression(context);
            if (applicationOptions.DebugMode)
            {
                logger.LogInformation("Model query filter: {filter}", filter.Expression);
                logger.LogInformation("Model query expression: {expression}", result.ToString());
            }
            return result;
        }
    }

    private ParsedFilterToken CreateToken(QueryField field, string condition, string value, int index)
    {
        lock (_generationLock)
        {
            var token = new FieldToken(field.QueryNames[0], condition, value, index, index) { FieldInfo = field };
            if (!tokenizer.NormalizeCondition(token))
                throw new ModelQueryNormalizationException("The field condition is not supported.", $"Condition: {condition}.");
            tokenizer.NormalizeValue(token);
            return CaptureToken(token);
        }
    }

    private static ParsedFilterToken CaptureToken(FieldToken token) =>
        new(token.FieldStr, token.ConditionStr, token.ValueStr, token.Start, token.End, token.FieldInfo!,
            token.Conditions, token.Features, CopyConvertedValue(token.ConvertedValue));

    // In conditions can contain a mutable typed list. Each query gets its own copy so its expression
    // tree cannot expose a list that changes a parsed filter used by another query.
    private static object? CopyConvertedValue(object? value)
    {
        if (value is Array array) return array.Clone();
        if (value is not IList list) return value;
        var copy = (IList)Activator.CreateInstance(value.GetType())!;
        foreach (var item in list) copy.Add(item);
        return copy;
    }

    private string GetSelectExpression(IReadOnlyList<QueryField> fields)
    {
        var duplicates = fields.GroupBy(field => field.ReflectionName, StringComparer.Ordinal)
            .Where(group => group.Count() > 1)
            .Select(group => string.Join(", ", group.Select(GetFieldPath)))
            .ToArray();
        if (duplicates.Length > 0)
            throw new ModelQueryNormalizationException(
                $"Selected fields produce duplicate projection members: {string.Join("; ", duplicates)}.");
        var expressions = fields.Select(field =>
        {
            if (field.NavigationProperties?.Any(segment => segment.IsCollection) == true)
                throw new ModelQueryNormalizationException("Collection navigation fields cannot be projected.",
                    $"Field: {field.ReflectionName}.");
            return GetFieldPath(field);
        });
        var expression = $"new {{ {string.Join(',', expressions)} }}";
        if (applicationOptions.DebugMode)
            logger.LogInformation("Model query projection: {expression}", expression);
        return expression;
    }

    private static string GetFieldPath(QueryField field) =>
        string.Join('.', (field.NavigationProperties?.Select(segment => segment.PropertyName) ?? [])
            .Append(field.ReflectionName));

    private static ParsingConfig CreateQueryConfig()
    {
        var config = new ParsingConfig { AllowEqualsAndToStringMethodsOnObject = true };
        config.CustomTypeProvider = new QueryCustomTypeProvider(config);
        return config;
    }

    private static ParsingConfig CreateObjectConfig()
    {
        var config = new ParsingConfig { AllowEqualsAndToStringMethodsOnObject = true };
        config.CustomTypeProvider = new ObjectCustomTypeProvider(config);
        return config;
    }

    private static void ValidateFilterSyntax(TokenizerContext context)
    {
        var text = context.OriginExpression;
        var position = 0;
        var tokenIndex = 0;
        var expectOperand = true;
        var depth = 0;
        while (position < text.Length)
        {
            if (char.IsWhiteSpace(text[position])) { position++; continue; }
            if (tokenIndex < context.Tokens.Count && position == context.Tokens[tokenIndex].Start)
            {
                if (!expectOperand) break;
                position = context.Tokens[tokenIndex++].End + 1;
                expectOperand = false;
                continue;
            }
            if (text[position] == '(' && expectOperand) { depth++; position++; continue; }
            if (text[position] == ')' && !expectOperand && depth > 0) { depth--; position++; continue; }
            if (text[position] == '!' && expectOperand) { position++; continue; }
            if (expectOperand && ConsumeWord(text, ref position, "not")) continue;
            if (!expectOperand && (Consume(text, ref position, "&&") || Consume(text, ref position, "||") ||
                                   Consume(text, ref position, "&") || Consume(text, ref position, "|") ||
                                   ConsumeWord(text, ref position, "andalso") || ConsumeWord(text, ref position, "orelse") ||
                                   ConsumeWord(text, ref position, "and") || ConsumeWord(text, ref position, "or")))
            {
                expectOperand = true;
                continue;
            }
            break;
        }
        if (position != text.Length || expectOperand || depth != 0 || tokenIndex != context.Tokens.Count)
            throw new ModelQueryNormalizationException("The model query filter syntax is invalid.",
                $"Expected field conditions joined by logical operators near position {position}.");
    }

    private static bool Consume(string text, ref int position, string value)
    {
        if (!text.AsSpan(position).StartsWith(value, StringComparison.OrdinalIgnoreCase)) return false;
        position += value.Length;
        return true;
    }

    private static bool ConsumeWord(string text, ref int position, string value)
    {
        if (!text.AsSpan(position).StartsWith(value, StringComparison.OrdinalIgnoreCase)) return false;
        var end = position + value.Length;
        if (end < text.Length && (char.IsLetterOrDigit(text[end]) || text[end] == '_')) return false;
        position = end;
        return true;
    }
}

file sealed class QueryCustomTypeProvider(ParsingConfig config) : DefaultDynamicLinqCustomTypeProvider(config, [], true)
{
    public override HashSet<Type> GetCustomTypes()
    {
        var result = base.GetCustomTypes();
        result.Add(typeof(EF));
        result.Add(typeof(DbFunctionsExtensions));
        return result;
    }
}

file sealed class ObjectCustomTypeProvider(ParsingConfig config) : DefaultDynamicLinqCustomTypeProvider(config, [], true)
{
    public override HashSet<Type> GetCustomTypes()
    {
        var result = base.GetCustomTypes();
        result.Add(typeof(LinqToObjectFunctions));
        return result;
    }
}
