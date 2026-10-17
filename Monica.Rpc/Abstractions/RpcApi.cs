using Monica.DependencyInjection.Abstractions;

namespace Monica.Rpc.Abstractions;

public abstract class RpcApi(ICachedServiceProvider serviceProvider) : IRpcApi
{
    protected ICachedServiceProvider CachedServiceProvider { get; } = serviceProvider;
}