namespace Monica.Rpc.Models;

/// <summary>Describes a call without retaining its payload, headers, or resolved address.</summary>
/// <param name="Service">The target descriptor.</param>
/// <param name="Operation">Stable operation name.</param>
/// <param name="RouteTemplate">Route template containing no actual request values.</param>
/// <param name="Transport">Host-selected transport key; defaults to ordinary HTTP.</param>
public sealed record RemoteCallContext(RpcServiceDescriptor Service, string Operation,
    string RouteTemplate, string Transport = "http");
