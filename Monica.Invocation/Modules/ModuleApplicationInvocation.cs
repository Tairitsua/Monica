using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Monica.ApplicationModel.Abstractions;
using Monica.Core;
using Monica.Core.Modularity;
using Monica.Core.Modularity.Abstractions;
using Monica.Invocation.Abstractions;
using Monica.Invocation.Models;
using Monica.Invocation.Services;

namespace Monica.Modules;

/// <summary>Registers application-model routing without a legacy domain-to-AppId provider.</summary>
public static class ModuleApplicationInvocationBuilderExtensions
{
    extension(IMonicaBuilder builder)
    {
        /// <summary>Composes per-operation Local/HTTP routing against validated ownership and locally available implementations.</summary>
        /// <remarks>Configure AddApplicationModel and the existing API HTTP provider for every required remote destination.</remarks>
        public ModuleRegistration<ModuleApplicationInvocation, ModuleApplicationInvocationOption> AddApplicationInvocation(
            Action<ModuleApplicationInvocationOption>? configure = null) => builder.AddModule<ModuleApplicationInvocation, ModuleApplicationInvocationOption>(configure);
    }

    extension(ModuleRegistration<ModuleApplicationInvocation, ModuleApplicationInvocationOption> registration)
    {
        /// <summary>Selects the single scoped product authority policy evaluated before normal Local/role routing.</summary>
        public ModuleRegistration<ModuleApplicationInvocation, ModuleApplicationInvocationOption> UseAuthorityPolicy<TPolicy>()
            where TPolicy : class, IApplicationInvocationAuthorityPolicy => registration.Configure(options => options.AuthorityPolicyType = typeof(TPolicy));

        /// <summary>Registers an explicit authority HTTP scope using the existing actual-AppId provider contract.</summary>
        /// <remarks>The provider can resolve another node/namespace and rewrite its delivery address. The logical role owner stays unchanged.</remarks>
        public ModuleRegistration<ModuleApplicationInvocation, ModuleApplicationInvocationOption> ConfigAuthorityHttpClientProvider<TProvider>(string providerKey)
            where TProvider : class, IApiHttpClientRegisterProvider
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(providerKey);
            if (providerKey != providerKey.Trim()) throw new ArgumentException("Provider keys cannot have surrounding whitespace.", nameof(providerKey));
            return registration.Configure(options => options.AuthorityHttpClientProviders.Add(providerKey, typeof(TProvider)));
        }
    }
}

/// <summary>Owns unified application invocation and startup registration of explicitly configured remote destinations.</summary>
public sealed class ModuleApplicationInvocation : MonicaModule<ModuleApplicationInvocationOption>
{
    /// <inheritdoc />
    public override void Describe(ModuleDescriptor module)
    {
        module.Require<ModuleApplicationModel, ModuleApplicationModelOption>();
        module.Require<ModuleApiClient, ModuleApiClientOption>(options => options.ApplicationModelRouting = true);
    }

    /// <inheritdoc />
    public override void ConfigureServices(ModuleContext<ModuleApplicationInvocationOption> context)
    {
        context.Services.AddHttpClient();
        context.Services.TryAddScoped<IApplicationApiInvoker, ApplicationApiInvoker>();
        if (Option.AuthorityPolicyType is { } authority)
            context.Services.AddScoped(typeof(IApplicationInvocationAuthorityPolicy), authority);
    }

    /// <inheritdoc />
    public override void PostConfigureServices(ModuleContext<ModuleApplicationInvocationOption> context)
    {
        var model = Application.TypeActivation as IApplicationModel
            ?? throw new InvalidOperationException("Application API requires a validated application model before service registration.");
        var apiClients = context.Modules.Get<ModuleApiClient, ModuleApiClientOption>();
        if (apiClients.Transport == ApiClientTransportKind.Grpc)
            throw new InvalidOperationException("Application API has no gRPC adapter.");
        var requiresRemote = model.Operations.Values.Any(operation => !model.LocalHandlers.ContainsKey(operation.RequestType) ||
            operation.RequiredRole is { } role && !model.IsRoleActive(role));
        if (requiresRemote && apiClients.HttpClientRegisterProviderType is null)
            throw new InvalidOperationException("Application operations require remote execution, but no API HTTP provider is configured.");
        if (Option.AuthorityPolicyType is not null && apiClients.HttpClientRegisterProviderType is null && Option.AuthorityHttpClientProviders.Count == 0)
            throw new InvalidOperationException("The application authority policy needs an explicitly configured API HTTP provider.");
        if (context.Services.Count(descriptor => descriptor.ServiceType == typeof(IApplicationInvocationAuthorityPolicy)) > 1)
            throw new InvalidOperationException("Configure one application API authority policy.");

        var clients = new Dictionary<(string AppId, string? ProviderKey), string>();
        var destinations = model.RoleApplications.Values.Append(model.AppId).Distinct(StringComparer.Ordinal);
        foreach (var appId in destinations)
        {
            if (apiClients.HttpClientRegisterProviderType is { } provider) Register(appId, null, provider);
            foreach (var (key, authorityProvider) in Option.AuthorityHttpClientProviders)
                Register(appId, key, authorityProvider);
        }
        context.Services.AddSingleton(new ApplicationApiClientCatalog(clients));

        void Register(string appId, string? key, Type provider)
        {
            ApiHttpClientRegistration.Register(context.Services, appId, provider, apiClients.CustomHttpClientBuilder, key);
            clients.Add((appId, key), ApiHttpClientRegistration.GetClientName(appId, key));
        }
    }
}

/// <summary>Explicit authority integration selected once during startup.</summary>
public sealed class ModuleApplicationInvocationOption : ModuleOptions<ModuleApplicationInvocation>
{
    internal Type? AuthorityPolicyType { get; set; }
    internal Dictionary<string, Type> AuthorityHttpClientProviders { get; } = new(StringComparer.Ordinal);
}
