using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Http;
using Microsoft.Extensions.Options;
using Monica.Modules;

namespace Monica.Invocation.Services;

/// <summary>Shares named-client ownership, authentication, provider setup, and timeout policy across API clients.</summary>
internal static class ApiHttpClientRegistration
{
    public static string GetClientName(string appId, string? providerKey = null) => providerKey is null
        ? $"Monica.Invocation.{appId}" : $"Monica.ApplicationInvocation.{providerKey.Length}:{providerKey}.{appId}";

    public static void Register(IServiceCollection services, string appId, Type providerType,
        Action<IHttpClientBuilder>? configure, string? providerKey = null)
    {
        services.TryAddSingleton(providerType);
        var name = GetClientName(appId, providerKey);
        var builder = services.AddHttpClient(name).RemoveAllLoggers()
            .AddHttpMessageHandler<AuthenticationDelegatingHandler>();
        configure?.Invoke(builder);
        services.AddSingleton<IConfigureOptions<HttpClientFactoryOptions>>(provider =>
            new ConfigureNamedOptions<HttpClientFactoryOptions>(name, options =>
            {
                var transport = (IApiHttpClientRegisterProvider)provider.GetRequiredService(providerType);
                transport.ConfigureHttpClientFactoryOptions(options, appId);
                options.HttpClientActions.Add(client => client.Timeout = Timeout.InfiniteTimeSpan);
            }));
    }
}
