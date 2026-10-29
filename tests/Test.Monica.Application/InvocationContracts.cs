using System.ComponentModel.DataAnnotations;
using Monica.Application.Abstractions;
using Monica.WebApi.Annotations;
using Monica.Application.Annotations;

[assembly: Monica.Application.Annotations.ApplicationApiGenerationConfig(Monica.Invocation.Models.ApiClientTransportKind.Local)]
[assembly: WebApiGenerationConfig("test", DomainName = "Testing")]

namespace Test.Monica.Application.PublishedLanguages.DomainTesting.Requests;

/// <summary>Stores one entry through the same local or HTTP application contract.</summary>
[ApiEndpoint(ApiHttpMethod.Post, "commands/Store")]
public sealed class CommandStore : ICommand<OperationReceipt>
{
    public CommandStore() { }
    public CommandStore(string name, bool Fail = false) { Name = name; this.Fail = Fail; }
    [Required]
    public string Name { get; set; } = "";
    public bool Fail { get; set; }
    public bool CallNested { get; set; }
}

/// <summary>Reads the current caller and scope with transport-independent query semantics.</summary>
[ApiEndpoint(ApiHttpMethod.Post, "queries/Identity")]
public sealed record QueryIdentity(bool WaitForPeer = false) : IQuery<OperationReceipt>;

public sealed record OperationReceipt(Guid ScopeId, string? User, Guid NestedScopeId = default);

/// <summary>Coordinates a child call with an explicit transaction relationship.</summary>
[ApiEndpoint(ApiHttpMethod.Post, "transactions/Coordinate")]
public sealed record CommandCoordinate(bool Independent = false, bool FailParent = false, bool FailChild = false,
    bool CallOriginalAfterView = false) : ICommand<TransactionReceipt>;

/// <summary>Writes in the selected participant and calls a nested query.</summary>
[ApiEndpoint(ApiHttpMethod.Post, "transactions/Child")]
public sealed record CommandTransactionChild(bool Fail = false) : ICommand<TransactionReceipt>;

/// <summary>Reads the receiving operation's scope identity.</summary>
[ApiEndpoint(ApiHttpMethod.Post, "transactions/Scope")]
public sealed record QueryTransactionScope(bool Cancel = false) : IQuery<Guid>;

/// <summary>Reads transactional state or returns a deliberately selected query outcome.</summary>
[ApiEndpoint(ApiHttpMethod.Post, "transactions/Read")]
public sealed record QueryReadTransaction(global::Monica.Core.Results.ResStatus? Rejection = null,
    QueryFault Fault = QueryFault.None) : IQuery<QueryTransactionReceipt>;

/// <summary>Reads through an explicitly selected consistency transaction.</summary>
[ApiEndpoint(ApiHttpMethod.Post, "transactions/ConsistentRead")]
public sealed record QueryConsistentRead : IQuery<QueryTransactionReceipt>;

public enum QueryFault { None, Exception, Cancellation, CaughtSql }
public sealed record QueryTransactionReceipt(bool HasTransaction, int Rows);

public sealed record TransactionReceipt(Guid ScopeId, Guid ChildScopeId, Guid GrandchildScopeId, string? User);
