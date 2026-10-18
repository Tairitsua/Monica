using System.Collections.Frozen;

namespace Monica.Rpc.Services;

internal sealed class ApplicationRpcClientCatalog(
    IReadOnlyDictionary<(string AppId, string? ProviderKey), string> clients)
{
    private readonly FrozenDictionary<(string AppId, string? ProviderKey), string> _clients = clients.ToFrozenDictionary();

    public string GetClientName(string appId, string? providerKey) => _clients.TryGetValue((appId, providerKey), out var client)
        ? client : throw new InvalidOperationException($"Application RPC target '{appId}' with provider '{providerKey ?? "default"}' was not configured at startup.");
}
