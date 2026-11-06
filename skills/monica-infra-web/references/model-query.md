# Query model fields and data

`Monica.ModelQuery` describes the queryable fields of an existing CLR model and uses those descriptions for filters, wildcard searches and dynamic field selection. Register it independently with `AddModelQuery()` or use the Web API bundle, which includes it through AutoControllers.

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

## Compose queries

Inject `IModelQuery<T>` for either source shape. `T` must be a reference type; the public generic constraint matches the registered provider's model contract:

| Service | Operations |
| --- | --- |
| `IModelQuery<T>` | Overloads compose `IQueryable<T>` or `IEnumerable<T>` filters, searches and field projections. `CompileFilter` returns a reusable in-memory predicate. |
| `IModelQuerySnapshot<T>` | Looks up the host's cached field metadata and accepted query names. |
| `IModelQuerySnapshotFactory` | Preloads a model with `PreloadSnapshot<T>()` or lists snapshots already activated in this host. |

For an injected `IModelQuery<Order> query` and an existing `IQueryable<Order> orders`:

```csharp
using Monica.ModelQuery.Models;

var filtered = query.ApplyFilter(orders, "total >= \"100\"");
var matchingNumber = query.ApplySearch(filtered, "SO-%", ["number"]);
var selected = query.SelectFields(matchingNumber, FieldSelection.Include("number", "total"));
var withoutNumber = query.SelectFields(filtered, FieldSelection.Exclude("number"));
```

Composition remains deferred; the caller owns enumeration, asynchronous database execution and cancellation. The `IQueryable<T>` overload keeps the original provider and never falls back to client evaluation. Provider translation still limits which expressions can execute. The `IEnumerable<T>` overload deliberately uses in-memory semantics. Choose the source's static type intentionally: calling an enumerable overload on a database query asks for client-side filtering during enumeration. Snapshots are immutable, cached per model per host and created lazily unless preloaded; the query service is transient. Register options before host composition.

`ParseFilter` returns an immutable `ParsedFilter<T>` with `ReferencedFields`. It does not expose generated expressions, parameter lists or tokenizer state. Reuse it with `ApplyFilter` or `CompileFilter` within its creating host, including through another query-service instance; another host rejects it because its field configuration can differ. The service generates expressions for the selected source kind. Internal query operations return ordinary .NET values and throw exceptions; the application boundary owns error projection.

```csharp
var filter = query.ParseFilter("total >= \"100\"");
var databaseQuery = query.ApplyFilter(orders, filter);
var predicate = query.CompileFilter(filter);
```

## Query names and navigation

`QueryNames` is the complete set of explicit expression names, not an additive alias list. A nonempty array replaces the default reflected property name or navigation path. An omitted or empty array uses that default. Names resolve with ordinal case-insensitive comparison. Conflicts and names that cannot be used by the filter grammar fail snapshot construction, including blank names, whitespace and parentheses. `GetAllQueryNames()` lists accepted names; `GetField(name)` returns null for an unknown name. Property-selector filter overloads resolve the selected CLR property path to its accepted query name, including when custom names replace the fallback.

By default a nested property is qualified, such as `Customer.Name`. `OmitNavigationPrefix` can shorten it to `Name`; precedence is field attribute, model attribute, then module. Omitting an attribute option inherits its parent value, while explicit `true` or `false` overrides it. Nested `Id` properties remain qualified. `AutoQualifyConflictingNames` can restore a navigation prefix when a short default name conflicts with a name already discovered; it does not silently rename explicit `QueryNames`. Any unresolved collision still fails.

The model's display name defaults to its CLR short name when `[QueryModel]` omits `Name`. `ModelQuerySnapshot.Model` carries model metadata; its `Fields` contain immutable field descriptions, accepted `QueryNames`, type information and search capabilities. Nested metadata and collections are read-only. Model names and field titles describe metadata; they do not rename CLR properties or dynamic projection output members.

## Filters and wildcard search

Use the existing field-condition grammar: `field operator "value"`, combined with `and`, `or` and parentheses. Existing boolean aliases `&&`, `||`, `&`, `|`, `AndAlso` and `OrElse` remain accepted between conditions. Values are quoted even for numbers, for example `total >= "100"`. Conditions and conversion are defined by `EFieldConditions` and the model's field types; common forms include `=`, `>=`, `in`, `like` and `explike`. The former bracketed raw Dynamic LINQ escape hatch (`[ ... ]`) is removed. Filter input must use the ModelQuery grammar and resolve against model fields.

`ApplySearch` searches eligible fields or an explicit array of accepted query names, combining field matches with OR. `ExcludeFromDefaultSearch = true` removes a field from the default search; an explicit field list can still include it when its type supports searching. Collections and unsupported fields are excluded by default. Unknown names and explicitly selected unsupported fields fail clearly.

For string fields, a search value without `%` becomes `%value%`; a value containing `%` is used as supplied. `%` matches any number of characters and `_` matches a single character. This preserves the existing search-box behavior:

| Search text | Effective pattern | Meaning |
| --- | --- | --- |
| `SO-` | `%SO-%` | Contains `SO-`. |
| `SO-%` | `SO-%` | Starts with `SO-`. |
| `%001` | `%001` | Ends with `001`. |
| `SO-%001` | `SO-%001` | Starts with `SO-` and ends with `001`. |

Search values are parameters, not filter-language fragments; quotation marks in ordinary search text remain data. Numeric and enum fields retain their type-specific search conversions. Database LIKE behavior follows its provider and collation; memory matching is case-insensitive. These APIs do not promise identical collation behavior across providers.

## Select and resolve fields

`SelectFields(source, FieldSelection.Include(...))` projects named fields. `FieldSelection.Exclude(...)` selects their complement. Field names are separate array items; a comma-separated string is not a field list. Selection is immutable and preserves the caller's selected-field order. Query names select CLR fields, so output retains the projection member names.

`GetFields()` returns all field descriptions. `ResolveFields(selection)` strictly resolves a selection; unknown or blank names and an empty final selection fail. Direct projection through collection navigation is unsupported; excluding those fields permits projection of the remaining fields.

Use `MatchFields(names)` only when the caller deliberately owns another field vocabulary, such as Excel export DTO properties. Its `MatchedFields` and `UnmatchedNames` make that split explicit. The caller must validate unmatched names against that other contract; this is not permission to silently discard invalid fields.

## HTTP request contract

| Interface | Property | Type and meaning |
| --- | --- | --- |
| `IModelFilterRequest` | `Filter` | Optional filter expression. |
| `IModelFilterRequest` | `SearchText` | Optional search text, preserving `%` patterns. |
| `IModelFilterRequest` | `SearchFields` | Optional `string[]` of search field names; omitted or empty selects default eligible fields. |
| `IFieldSelectionRequest` | `IncludeFields` | Optional `string[]` of fields to project. |
| `IFieldSelectionRequest` | `ExcludeFields` | Optional `string[]` of fields to omit. |

`GetFieldSelection()` validates the selection request and returns an optional `FieldSelection`. Empty arrays mean no projection request. Both include and exclude arrays being nonempty is an error; a nonempty array cannot contain blank names. Pagination, sorting and CRUD features remain outside ModelQuery.

JSON bodies use real arrays:

```json
{
  "filter": "total >= \"100\"",
  "searchText": "SO-%",
  "searchFields": ["number"],
  "includeFields": ["number", "total"]
}
```

GET requests repeat a key for each item, for example `?searchText=SO-%25&searchFields=number&includeFields=number&includeFields=total`. The percent sign is URL-encoded once by the client. Frontend callers must migrate names and serialization together with the backend; old property aliases are not retained.

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

Use package and namespace `Monica.ModelQuery`, `AddModelQuery`, `ModuleModelQueryOption`, `[QueryModel]` and `[QueryField]` in place of their AutoModel/AutoTable/AutoField names. Replace active-mode switches with `RequireFieldAnnotation` and activation-name members with `QueryNames`/`GetAllQueryNames`.

Replace source-specific operator interfaces with `IModelQuery<T>`, `ApplyFuzzy` with `ApplySearch`, and `NormalizeFilter` with `ParseFilter`. Replace `NormalizedFilter` access to tokens with `ParsedFilter<T>.ReferencedFields`. Use `FieldSelection` instead of selection strings, exclusion methods or reverse flags. `MatchFields` replaces tolerant normalization when a real second field vocabulary exists. Search annotations use `ExcludeFromDefaultSearch`.

HTTP consumers migrate `Fuzzy` to `SearchText`, `FuzzyColumns` to `SearchFields`, `SelectColumns` to `IncludeFields`, and `SelectExceptColumns` to `ExcludeFields`; the three field lists become arrays. Ordinary filter grammar and search wildcards retain their meaning. Tokenizer/provider classes and mutable execution details are no longer consumer extension contracts.

No source compatibility aliases remain. The status route, `GetModelQueryStatus` endpoint name, default Swagger group and module diagnostic identity follow the new module name. Update saved diagnostic links and explicit type references. The unimplemented title-as-name, required-filter and client-evaluation switches were removed rather than carried into the new contract. Preserve saved ordinary filter expressions, and migrate request names and field-list encoding with their consumers.

Source owners: `Monica.ModelQuery/Modules/ModuleModelQuery.cs`, public `Abstractions/`, `Annotations/` and `Models/`; behavior checks live in `tests/Test.Monica.ModelQuery/`. AutoControllers consumes these services through `Monica.WebApi/AutoControllers/Services/AbstractKeyCrudApplicationService.cs`.
