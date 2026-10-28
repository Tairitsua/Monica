using System.ComponentModel.DataAnnotations;
using Monica.Application.Abstractions;
using Monica.WebApi.Annotations;
using Monica.Application.Annotations;

[assembly: Monica.Application.Annotations.ApplicationApiGenerationConfig(Monica.Invocation.Models.ApiClientTransportKind.Local)]
[assembly: WebApiGenerationConfig("test", DomainName = "Testing")]

namespace Test.Monica.Application.PublishedLanguages.DomainTesting.Requests;

/// <summary>Stores one entry through the same local or HTTP application contract.</summary>
[ApiEndpoint(ApiHttpMethod.Post, "commands/Store")]
public sealed class CommandStore : IResultRequest<OperationReceipt>
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
public sealed record QueryIdentity(bool WaitForPeer = false) : IResultRequest<OperationReceipt>;

public sealed record OperationReceipt(Guid ScopeId, string? User, Guid NestedScopeId = default);

/// <summary>Coordinates a child call with an explicit transaction relationship.</summary>
[ApiEndpoint(ApiHttpMethod.Post, "transactions/Coordinate")]
public sealed record CommandCoordinate(bool Independent = false, bool FailParent = false, bool FailChild = false,
    bool CallOriginalAfterView = false) : IResultRequest<TransactionReceipt>;

/// <summary>Writes in the selected participant and calls a nested query.</summary>
[ApiEndpoint(ApiHttpMethod.Post, "transactions/Child")]
public sealed record CommandTransactionChild(bool Fail = false) : IResultRequest<TransactionReceipt>;

/// <summary>Reads the receiving operation's scope identity.</summary>
[ApiEndpoint(ApiHttpMethod.Post, "transactions/Scope")]
public sealed record QueryTransactionScope(bool Cancel = false) : IResultRequest<Guid>;

public sealed record TransactionReceipt(Guid ScopeId, Guid ChildScopeId, Guid GrandchildScopeId, string? User);
