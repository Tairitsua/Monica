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
