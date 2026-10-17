using Domains.Ordering.Application.HandlersCommand;
using Monica.Application.Annotations;
using Monica.Modules;
using Monica.Rpc.Models;
using Platform.Protocol.PublishedLanguages.DomainOrdering.Requests;

[assembly: ApplicationApiGenerationConfig(RpcTransportKind.Local, typeof(CommandCreateOrder),
    HandlerAssemblies = new[] { typeof(CommandHandlerCreateOrder) })]
#if DEBUG || MONICA_HTTP_ENDPOINTS
[assembly: Monica.WebApi.Annotations.WebApiGenerationConfig]
#endif

[Flags]
internal enum OrderingDomains { None = 0, Ordering = 1 }

internal sealed class OrderingRpcDomains : IRpcClientDomainInfoProvider
{
    public object GetDependencyDomains() => OrderingDomains.Ordering;
    public RpcServiceDescriptor GetDomain(Enum domain) => throw new NotSupportedException("This host uses Local RPC and has no remote destinations.");
}
