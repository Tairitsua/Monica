# Explicit result envelopes

Use `Result`, `Result<T>`, or `PagedResult<T>` at Facades and other public boundaries that intentionally expose Monica's envelope. Internal infrastructure services use direct returns and exceptions. All three result types implement `IResultEnvelope`; generic results do not inherit from `Result`. There are no implicit or explicit conversion operators between payloads, strings, exceptions, or envelopes.

## Constructing an outcome

Use the factories on the non-generic `Result` type. A success factory's argument is always data, including a string. Set a human-readable message separately.

```csharp
Result saved = Result.Success().WithMessage("Saved.");
Result<string> token = Result.Success("token-value");
Result<OrderDto> order = Result.Success(orderDto);
Result<OrderDto> created = Result.Created(orderDto);
Result accepted = Result.Created();
Result rejected = Result.Failure("The order is closed.", ResultStatus.Conflict);
Result<OrderDto> missing = Result.Failure<OrderDto>("Order not found.", ResultStatus.NotFound);
Result<OrderDto?> optional = Result.Success<OrderDto?>(null);
```

`Success` sets status 200; `Created` sets 201. The no-data factories initialize `Message` to the empty string, and the data factories initialize it to null. `Failure` defaults to `BadRequest` (400); it requires a defined failure status and throws `ArgumentOutOfRangeException` for `Unknown`, success statuses, or undefined values. `ResultStatus` is the supported HTTP outcome whitelist. Express detailed reasons with `ResultErrorCodes` instead of inventing statuses.

Parameterless constructors and mutable envelope properties support serialization. A newly constructed envelope starts with `Unknown`; it is not a completed outcome. Public producers should use factories. There are no convenience constructors taking data, messages, exceptions, or statuses; no format-string factories; and no null-to-failure or arbitrary-status factory. Do not return bare payloads or strings from result-returning methods.

## Reading and forwarding

`IsSuccess()` accepts 200 and 201. `IsFailure()` accepts the defined failure statuses. Both throw `InvalidOperationException` for `Unknown` or undefined statuses. There are no `out` extraction overloads. Classification does not promise a non-null payload: `Data` remains nullable, and optional data should be expressed with `Result<T?>`.

When an application request allows absent data, declare that in `IQuery<T?>` or `ICommand<T?>` and its handler's response type. The envelope itself must be non-null; published requests with nullable outer envelopes are rejected. Generated API interfaces and clients must preserve nullable annotations, including nested payload types, across the protocol, catalog, and host assembly boundaries. Those annotations do not change CLR type identity or the JSON envelope.

```csharp
var lookup = await orders.FindAsync(id, cancellationToken);
if (lookup.IsFailure())
    return Result.FromFailure<OrderSummary>(lookup);

var order = lookup.Data
    ?? throw new InvalidOperationException("A successful order lookup must include an order.");
return Result.Success(new OrderSummary(order.Id, order.Name));
```

Handle an allowed absence explicitly instead of throwing. A producer promising required data must supply it; callers must not turn an absent required payload into invented success data. An unexpected contract defect belongs to the host exception policy.

Return an existing envelope directly when its type matches. Use `Result.FromFailure(source)` or `Result.FromFailure<T>(source)` when forwarding failure to a different result shape. These preserve status, the exact message, and all metadata, including error origin, but do not copy data. They reject a null source with `ArgumentNullException` and a success or invalid source status with `InvalidOperationException`; they never invent a failure from a successful outcome. Preserve a meaningful failure report deliberately:

```csharp
Result<ValidationReport> rejected = Result
    .Failure("Review the rejected changes.", ResultStatus.Conflict)
    .WithData(report);

Result withoutReport = rejected.WithoutData();
Result<OrderSummary> forwarded = Result.FromFailure<OrderSummary>(rejected);
```

For a different report schema, map the report explicitly and attach it with `WithData`. `Result.WithData<T>(data)`, `Result<T>.WithoutData()`, and `PagedResult<T>.WithoutData()` create a new envelope while preserving classification, message, and metadata. They copy the top-level metadata dictionary; nested metadata objects are shared. `WithMessage<TSelf>(message)` changes the same receiver and preserves its concrete type. There is no automatic payload erasure or cross-generic conversion.

## Pagination and aggregation

Create a page through the non-generic `PagedResult` factory:

```csharp
PagedResult<OrderDto> page = PagedResult.Success(
    totalCount: 42, items: orders, currentPage: 2, pageSize: 10);
PagedResult<OrderDto> rejected = PagedResult.Failure<OrderDto>("Invalid filter.");
PagedResult<OrderDto> forwarded = PagedResult.FromFailure<OrderDto>(lookup);
```

The optional `currentPage`, `pageSize`, and `cursor` arguments default to null. The nested `PageData` and its pagination calculations retain their wire contract. Ordinary paged failures initialize `sum` to zero and `items` to an empty collection. The `IRemoteResultEnvelope<TSelf>.CreateRemoteFailure` transport path retains null `sum` and `items`; do not normalize away that distinction. Use `ToBulkResult` for the existing aggregate-result behavior.

## Error and transport boundaries

The serialized envelope uses `message`, `status`, `metadata`, and, for generic results, `data` under the repository's camelCase policy. The CLR type rename can change OpenAPI schema names even though these fields and status values are retained. Review generated schemas and client contracts when migrating consumers.

`metadata.error` is reserved for one `ResultError` contract with a stable code, nonempty origin trace, optional logical service/operation, and bounded request field errors. Capture the trace once at the owning boundary. Keep exception text, stack traces, request/response dumps, and call chains in operator diagnostics; public failures get safe application messages. Unexpected defects should reach the host handler. A Facade that deliberately catches an exception logs it under host policy and returns a safe failure. Caller cancellation propagates; it is not a failed business result.

Use `GetResponse()` (or `GetResponse(controller)`) at HTTP boundaries. `Application:DebugMode` controls whether reserved diagnostic metadata may be exposed; ordinary hosts strip it. Configure top-level field names through `monica.AddResultEnvelope().UseResultFieldNames(...)` only when the host needs a different wire convention.

Generated API clients and custom forwarders use `IRemoteCallClient` for the complete exchange. The request factory transfers request ownership, the client is borrowed, caller cancellation propagates, and the call boundary does not retry. Configure `ModuleApiClientOption.CallTimeout` for generated API deadlines. A timeout or downstream 429 does not establish whether a command executed. Remote application failures are preserved only when the envelope and HTTP status agree; transport classification follows transport behavior. The remote boundary records target identity and correlation for the call chain on both success and failure. Result classification does not control transaction commit. Returned failures, including at the outermost operation, do not automatically roll back; developers must inspect child results and choose explicit abort when required. An exception escaping the transaction owner triggers rollback. See the [transaction contract](../../monica-infra-persistence/references/transactions.md#business-outcomes-and-explicit-abort).

## Migration and verification

Replace the old abbreviated result types and factory names, then review return expressions and callers by meaning. An old non-generic success-message call becomes `Result.Success().WithMessage(message)`, while a string payload becomes `Result.Success(value)`. Compilation cannot distinguish those intentions in an inferred variable or `IResultEnvelope` assignment. Remove success/failure extraction helpers and inspect payload absence explicitly. Use typed failures or `FromFailure<T>` instead of reconstructing a failure from its message and losing metadata.

Runtime and serialization contracts live in `tests/Test.Monica.Core/Results/ResultContractTests.cs`; forbidden conversions and supported syntax are exercised by `tests/Test.Monica.Generators.AutoController/ResultApiCompilationTests.cs`. Verify string payloads, nullable data, Created status, failure reports, metadata copying, HTTP/local forwarding, rollback, and the distinct pagination failure defaults when changing these contracts.
