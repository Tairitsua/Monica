using Microsoft.Extensions.Http;

// ReSharper disable once CheckNamespace
namespace Monica.Modules;

/// <summary>
/// Configures named <see cref="HttpClientFactoryOptions"/> instances used by generated HTTP RPC clients.
/// </summary>
public interface IRpcHttpClientRegisterProvider
{
    void ConfigureHttpClientFactoryOptions(HttpClientFactoryOptions options, string appid);
}
