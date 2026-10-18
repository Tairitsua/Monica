using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Monica.ApplicationModel.Abstractions;
using Monica.Core;
using Monica.Core.Modularity;
using Monica.Core.Modularity.Abstractions;
using Monica.Rpc.Abstractions;
using Monica.Rpc.Models;
using Monica.Rpc.Services;

namespace Monica.Modules;

/// <summary>Registers application-model routing without a legacy domain-to-AppId provider.</summary>
public static class ModuleApplicationRpcBuilderExtensions
{
    extension(IMonicaBuilder builder)
    {
        /// <summary>Composes per-operation Local/HTTP routing against validated ownership and locally available implementations.</summary>
        /// <remarks>Configure AddApplicationModel and the existing RPC HTTP provider for every required remote destination.</remarks>
        public ModuleRegistration<ModuleApplicationRpc, ModuleApplicationRpcOption> AddApplicationRpc(
            Action<ModuleApplicationRpcOption>? configure = null) => builder.AddModule<ModuleApplicationRpc, ModuleApplicationRpcOption>(configure);
    }

    extension(ModuleRegistration<ModuleApplicationRpc, ModuleApplicationRpcOption> registration)
    {
        /// <summary>Selects the single scoped product authority policy evaluated before normal Local/role routing.</summary>
        public ModuleRegistration<ModuleApplicationRpc, ModuleApplicationRpcOption> UseAuthorityPolicy<TPolicy>()
            where TPolicy : class, IApplicationRpcAuthorityPolicy => registration.Configure(options => options.AuthorityPolicyType = typeof(TPolicy));

        /// <summary>Registers an explicit authority HTTP scope using the existing actual-AppId provider contract.</summary>
        /// <remarks>The provider can resolve another node/namespace and rewrite its delivery address. The logical role owner stays unchanged.</remarks>
        public ModuleRegistration<ModuleApplicationRpc, ModuleApplicationRpcOption> ConfigAuthorityHttpClientProvider<TProvider>(string providerKey)
            where TProvider : class, IRpcHttpClientRegisterProvider
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(providerKey);
            if (providerKey != providerKey.Trim()) throw new ArgumentException("Provider keys cannot have surrounding whitespace.", nameof(providerKey));
            return registration.Configure(options => options.AuthorityHttpClientProviders.Add(providerKey, typeof(TProvider)));
        }
    }
}

/// <summary>Owns unified application invocation and startup registration of explicitly configured remote destinations.</summary>
public sealed class ModuleApplicationRpc : MonicaModule<ModuleApplicationRpcOption>
{
    /// <inheritdoc />
    public override void Describe(ModuleDescriptor module)
    {
        module.Require<ModuleApplicationModel, ModuleApplicationModelOption>();
        module.Require<ModuleRpcClient, ModuleRpcClientOption>(options => options.ApplicationModelRouting = true);
    }

    /// <inheritdoc />
    public override void ConfigureServices(ModuleContext<ModuleApplicationRpcOption> context)
    {
        context.Services.AddHttpClient();
        context.Services.TryAddScoped<IApplicationApiInvoker, ApplicationApiInvoker>();
        if (Option.AuthorityPolicyType is { } authority)
            context.Services.AddScoped(typeof(IApplicationRpcAuthorityPolicy), authority);
    }

    /// <inheritdoc />
    public override void PostConfigureServices(ModuleContext<ModuleApplicationRpcOption> context)
    {
        var model = Application.TypeActivation as IApplicationModel
            ?? throw new InvalidOperationException("Application RPC requires a validated application model before service registration.");
        var rpc = context.Modules.Get<ModuleRpcClient, ModuleRpcClientOption>();
        if (rpc.Transport == RpcTransportKind.Grpc)
            throw new InvalidOperationException("Application RPC has no gRPC adapter.");
        var requiresRemote = model.Operations.Values.Any(operation => !model.LocalHandlers.ContainsKey(operation.RequestType) ||
            operation.RequiredRole is { } role && !model.IsRoleActive(role));
        if (requiresRemote && rpc.HttpClientRegisterProviderType is null)
            throw new InvalidOperationException("Application operations require remote execution, but no RPC HTTP provider is configured.");
        if (Option.AuthorityPolicyType is not null && rpc.HttpClientRegisterProviderType is null && Option.AuthorityHttpClientProviders.Count == 0)
            throw new InvalidOperationException("The application authority policy needs an explicitly configured RPC HTTP provider.");
        if (context.Services.Count(descriptor => descriptor.ServiceType == typeof(IApplicationRpcAuthorityPolicy)) > 1)
            throw new InvalidOperationException("Configure one application RPC authority policy.");

        var clients = new Dictionary<(string AppId, string? ProviderKey), string>();
        var destinations = model.RoleApplications.Values.Append(model.AppId).Distinct(StringComparer.Ordinal);
        foreach (var appId in destinations)
        {
            if (rpc.HttpClientRegisterProviderType is { } provider) Register(appId, null, provider);
            foreach (var (key, authorityProvider) in Option.AuthorityHttpClientProviders)
                Register(appId, key, authorityProvider);
        }
        context.Services.AddSingleton(new ApplicationRpcClientCatalog(clients));

        void Register(string appId, string? key, Type provider)
        {
            RpcHttpClientRegistration.Register(context.Services, appId, provider, rpc.CustomHttpClientBuilder, key);
            clients.Add((appId, key), RpcHttpClientRegistration.GetClientName(appId, key));
        }
    }
}

/// <summary>Explicit authority integration selected once during startup.</summary>
public sealed class ModuleApplicationRpcOption : ModuleOptions<ModuleApplicationRpc>
{
    internal Type? AuthorityPolicyType { get; set; }
    internal Dictionary<string, Type> AuthorityHttpClientProviders { get; } = new(StringComparer.Ordinal);
}
