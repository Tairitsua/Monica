# Query model fields and data

`Monica.ModelQuery` describes the queryable fields of an existing CLR model and uses those descriptions for filters, fuzzy matching and dynamic field selection. It does not generate application models or map objects. Register it independently with `AddModelQuery()` or use the Web API bundle, which includes it through AutoControllers.

## Register and declare fields

```csharp
using Monica.Core.Modularity.Extensions;
using Monica.ModelQuery.Annotations;
using Monica.Modules;

var builder = WebApplication.CreateBuilder(args);
builder.AddMonica(monica =>
    monica.AddModelQuery(options => options.RequireFieldAnnotation = true));

var app = builder.Build();
app.UseMonica();
app.MapMonica();
app.Run();

[QueryModel(Name = "Orders", RequireFieldAnnotation = true)]
public sealed class Order
{
    [QueryField(QueryNames = new[] { "number", "orderNumber" })]
    public string Number { get; init; } = string.Empty;

    [QueryField]
    public decimal Total { get; init; }
}
```

`RequireFieldAnnotation` defaults to `false`: eligible public properties participate without annotations. When it is `true`, every participating property needs `[QueryField]`, including navigation properties and their nested leaves. A model-level value overrides the module value; omitting it inherits the module value. The attribute uses an ordinary `bool` named argument and tracks omission internally, so explicit `false` can override a global `true`.

`[QueryField(Exclude = true)]` always excludes a property. Otherwise an explicit `[QueryField]` opts the property in even when it also has `[JsonIgnore]` or `[NotMapped]`. Without that annotation, those properties are excluded unless the corresponding module inclusion option is enabled. Dictionaries remain unsupported. This metadata policy is not an authorization boundary.

## Choose the source shape

Inject the service matching the caller's existing data source:

| Service | Operations |
| --- | --- |
| `IQueryableModelQuery<T>` | Composes `IQueryable<T>` filters and untyped `IQueryable` field projections; the source provider executes them when enumerated. |
| `IEnumerableModelQuery<T>` | Filters or projects an in-memory `IEnumerable<T>`; `CompileFilter` returns a reusable `Func<T, bool>`. |
| `IModelQuery<T>` | Common field resolution and filter normalization implemented by both services; choose a concrete source-shape interface for DI. |
| `IModelQuerySnapshot<T>` | Looks up the host's cached field metadata and accepted query names. |
| `IModelQuerySnapshotFactory` | Preloads a model with `PreloadSnapshot<T>()` or lists snapshots already activated in this host. |

For an injected `IQueryableModelQuery<Order> query` and an existing `IQueryable<Order> orders`:

```csharp
var filtered = query.ApplyFilter(orders, "total >= \"100\"");
var matchingNumber = query.ApplyFuzzy(filtered, "SO-", "number");
var selected = query.SelectFields(matchingNumber, "number,total");
var withoutNumber = query.SelectFieldsExcept(filtered, "number");
```

ModelQuery composes the query; the caller owns enumeration, asynchronous database execution and cancellation. Query-provider translation still limits what a database can execute. The enumerable service deliberately uses in-memory expression semantics. Snapshots are cached per model per host, created lazily unless preloaded; query operators and normalizers are transient. Register options before host composition and do not mutate cached metadata returned by a snapshot.

`NormalizeFilter` returns a `NormalizedFilter`, which either service can accept in its `ApplyFilter` overload when the normalization semantics match the source. Invalid names, values or expressions raise the relevant `ModelQueryException` subtype. Internal query operations do not return `Result` envelopes; the application boundary owns any error projection.

## Query names and navigation

`QueryNames` is the complete set of explicit expression names, not an additive alias list. A nonempty array replaces the default reflected property name or navigation path. An omitted or empty array uses that default. Names resolve with ordinal case-insensitive comparison. Conflicts and names that cannot be used by the filter or selection grammar fail snapshot construction, including blank names, whitespace, parentheses and the configured selection separator. `GetAllQueryNames()` lists accepted names; `GetField(name)` returns null for an unknown name. A bare `GetFields(names)` snapshot lookup omits unknown names, while query-operator selection validates names and reports errors. Property-selector filter overloads resolve the selected CLR property path to its accepted query name, including when custom names replace the fallback.

By default a nested property is qualified, such as `Customer.Name`. `OmitNavigationPrefix` can shorten it to `Name`; precedence is field attribute, model attribute, then module. Omitting an attribute option inherits its parent value, while explicit `true` or `false` overrides it. Nested `Id` properties remain qualified. `AutoQualifyConflictingNames` can restore a navigation prefix when a short default name conflicts with a name already discovered; it does not silently rename explicit `QueryNames`. Any unresolved collision still fails.

The model's display name defaults to its CLR short name when `[QueryModel]` omits `Name`. `ModelQuerySnapshot.Model` carries `QueryModel` metadata; its `Fields` are `QueryField` metadata with accepted `QueryNames`, `TypeInfo` and `FuzzySearch` information. Model names and field titles describe metadata; they do not rename CLR properties or dynamic projection output members.

## Expressions, fuzzy matching and selection

Use the existing field-condition grammar: `field operator "value"`, combined with `and`, `or` and parentheses. Values are quoted even for numbers, for example `total >= "100"`. Conditions and conversion are defined by `EFieldConditions` and the model's field types; common forms include `=`, `>=`, `in`, `like` and `explike`. The naming migration does not introduce another expression language. `ModelQueryExpressionOptions` retains the existing separators and expression tokens.

`ApplyFuzzy` searches eligible fields or an explicit field list separated by `ModelQueryExpressionOptions.SelectSeparator` (a comma by default). `ExcludeFromDefaultFuzzySearch = true` removes a field from the default all-field search; an explicit field list can still include it when its type supports fuzzy matching. Collections and unsupported fields are excluded from default fuzzy matching, and an explicitly selected unsupported field fails normalization. Custom query names are used consistently for field lookup and fuzzy matching.

`SelectFields` projects the named fields; `SelectFieldsExcept` projects their complement. Query names select CLR fields, so the output uses the existing projection member names. Direct projection of collection-navigation fields is unsupported; excluding those fields allows the remaining fields to be projected. `GetFieldsExcept` and reverse literal field resolution return metadata for the complement as well. Unknown selection names are errors, not silent partial projections.

`ApplyFilter(IQueryable<T>, string)` retains its existing bracketed raw Dynamic LINQ expression form (`[ ... ]`). That form does not use ModelQuery field-name resolution; requiring field annotations does not make this API an expression sandbox. The application owns which expression forms it accepts from callers.

Existing CRUD requests retain `IHasRequestFilter` and `IHasRequestSelect`, including `Filter`, `Fuzzy`, `FuzzyColumns`, `SelectColumns` and `SelectExceptColumns`. `SelectColumns` and `SelectExceptColumns` are alternative projections, not new wire names introduced by this migration.

## Module options and diagnostics

All of these `ModuleModelQueryOption` switches default to `false`:

| Option | Effect when enabled |
| --- | --- |
| `RequireFieldAnnotation` | Requires `[QueryField]` on participating properties. |
| `OmitNavigationPrefix` | Uses short default field names where allowed. |
| `AutoQualifyConflictingNames` | Restores a navigation prefix for detected short-name conflicts. |
| `IncludeJsonIgnoredProperties` | Includes otherwise eligible unannotated `[JsonIgnore]` properties. |
| `IncludeNotMappedProperties` | Includes otherwise eligible unannotated `[NotMapped]` properties. |
| `ThrowOnUnsupportedFieldType` | Fails on unsupported field types instead of skipping them. |

When common module minimal APIs are enabled, `GET /model-query/status` lists this host's activated snapshots. The optional `modelName` query parameter filters by model name, case-insensitively. The response includes `count`, `models`, and `snapshots` containing model and field metadata under the host's normal JSON naming policy. This endpoint inspects cached metadata; it does not scan all assemblies or run business queries. Configure exposure using the shared [API visibility contract](api-documentation-and-visibility.md).

Expression logging follows the frozen [application debug policy](../../monica-infra-hosting/references/debug-mode.md) and the host's logger filters. Debug logs can contain original expressions and values; ModelQuery has no separate debug switch.

## Migrating earlier names

Use package and namespace `Monica.ModelQuery`, `AddModelQuery`, `ModuleModelQueryOption`, `[QueryModel]` and `[QueryField]` in place of their AutoModel/AutoTable/AutoField names. Replace active-mode switches with `RequireFieldAnnotation` and activation-name members with `QueryNames`/`GetAllQueryNames`. The former database and memory operators become `IQueryableModelQuery<T>` and `IEnumerableModelQuery<T>`; field projection uses `SelectFields`/`SelectFieldsExcept`, and in-memory predicate compilation uses `CompileFilter`.

No source compatibility aliases remain. The status route, `GetModelQueryStatus` endpoint name, default Swagger group and module diagnostic identity change with the new module. Update saved diagnostic links and explicit type references. The unimplemented title-as-name, required-filter and client-evaluation switches were removed rather than carried into the new contract. Business filter syntax and CRUD request member names remain unchanged.

Source owners: `Monica.ModelQuery/Modules/ModuleModelQuery.cs`, public `Abstractions/`, `Annotations/` and `Models/`; behavior checks live in `tests/Test.Monica.ModelQuery/`. AutoControllers consumes these services through `Monica.WebApi/AutoControllers/Services/AbstractKeyCrudApplicationService.cs`.
