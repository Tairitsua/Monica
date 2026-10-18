using Microsoft.Extensions.Http;

// ReSharper disable once CheckNamespace
namespace Monica.Modules;

/// <summary>
/// Configures named <see cref="HttpClientFactoryOptions"/> instances used by generated HTTP API clients.
/// </summary>
/// <remarks>Providers are singletons and must not capture caller-scoped services. Registration supplies the logical destination AppId.</remarks>
public interface IApiHttpClientRegisterProvider
{
    /// <summary>Configures HTTP delivery for the given destination, including its resolved base address.</summary>
    /// <remarks>The invocation module applies its owned-client timeout policy after this configuration.</remarks>
    void ConfigureHttpClientFactoryOptions(HttpClientFactoryOptions options, string appid);
}
