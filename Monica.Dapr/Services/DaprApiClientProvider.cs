using Dapr.Client;
using Microsoft.Extensions.Http;
using Microsoft.Extensions.Options;
using Monica.Modules;

namespace Monica.Dapr.Services;

/// <summary>Configures application API HTTP clients for Dapr sidecar invocation using logical destination AppIds.</summary>
/// <remarks>Registered as a singleton by ModuleDaprApiClient. Each named client receives its own invocation handler.</remarks>
public class DaprApiClientProvider : IApiHttpClientRegisterProvider
{
    /// <inheritdoc />
    public void ConfigureHttpClientFactoryOptions(HttpClientFactoryOptions options, string appid)
    {
        options.HttpClientActions.Add(client =>
        {
            try
            {
                client.BaseAddress = new Uri($"http://{appid}");
            }
            catch (UriFormatException inner)
            {
                throw new ArgumentException("The appId must be a valid hostname.", nameof(appid), inner);
            }

        });

        options.HttpMessageHandlerBuilderActions.Add(builder =>
        {
            builder.AdditionalHandlers.Add(new InvocationHandler
            {
                DefaultAppId = appid
            });
        });
    }
}
