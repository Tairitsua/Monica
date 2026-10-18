using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Monica.Core;
using Monica.Core.Modularity;
using Monica.Core.Modularity.Abstractions;
using Monica.Core.TypeDiscovery.Models;
using Monica.Tool.Extensions;
using Monica.Rpc.Abstractions;
using Monica.Rpc.Annotations;
using Monica.Rpc.Models;
using Monica.Rpc.Facades;
using Monica.Rpc.Services;

// ReSharper disable once CheckNamespace
namespace Monica.Modules;

public static class ModuleRpcClientBuilderExtensions
{
    extension(IMonicaBuilder builder)
    {
        /// <summary>
        /// Registers and configures the RPC client module.
        /// </summary>
        public ModuleRegistration<ModuleRpcClient, ModuleRpcClientOption> AddRpcClient(
            Action<ModuleRpcClientOption>? action = null)
        {
            return builder.AddModule<ModuleRpcClient, ModuleRpcClientOption>(action);
        }
    }

    extension(ModuleRegistration<ModuleRpcClient, ModuleRpcClientOption> registration)
    {
        /// <summary>
        /// Selects HTTP transport implementations for RPC contracts.
        /// </summary>
        public ModuleRegistration<ModuleRpcClient, ModuleRpcClientOption> UseHttpTransport()
        {
            return registration.Configure(options => options.Transport = RpcTransportKind.Http);
        }

        /// <summary>
        /// Selects in-process transport implementations for RPC contracts.
        /// </summary>
        public ModuleRegistration<ModuleRpcClient, ModuleRpcClientOption> UseLocalTransport()
        {
            return registration.Configure(options => options.Transport = RpcTransportKind.Local);
        }

        /// <summary>
        /// Selects the reserved gRPC transport, which currently fails validation at composition time.
        /// </summary>
        public ModuleRegistration<ModuleRpcClient, ModuleRpcClientOption> UseGrpcTransport()
        {
            return registration.Configure(options => options.Transport = RpcTransportKind.Grpc);
        }

        /// <summary>
        /// Selects the provider responsible for named HTTP-client registration.
        /// </summary>
        public ModuleRegistration<ModuleRpcClient, ModuleRpcClientOption> ConfigHttpClientRegisterProvider<TProvider>()
            where TProvider : class, IRpcHttpClientRegisterProvider
        {
            return registration.Configure(options => options.UseHttpClientProvider<TProvider>());
        }

        /// <summary>
        /// Configures the dependency-domain resolver used to select generated clients.
        /// </summary>
        public ModuleRegistration<ModuleRpcClient, ModuleRpcClientOption> ConfigDomainInfoProvider(
            IRpcClientDomainInfoProvider domainInfoProvider)
        {
            ArgumentNullException.ThrowIfNull(domainInfoProvider);
            return registration
                .Configure(options => options.DomainInfoProvider = domainInfoProvider);
        }

        /// <summary>
        /// Applies additional HTTP-client customization to every HTTP RPC registration.
        /// </summary>
        public ModuleRegistration<ModuleRpcClient, ModuleRpcClientOption> ConfigHttpClientBuilder(
            Action<IHttpClientBuilder> configure)
        {
            ArgumentNullException.ThrowIfNull(configure);
            return registration.Configure(options => options.CustomHttpClientBuilder = configure);
        }

        /// <summary>Binds provider-error classification to an explicitly selected transport.</summary>
        public ModuleRegistration<ModuleRpcClient, ModuleRpcClientOption> ConfigResponseClassifier<TClassifier>(string transport)
            where TClassifier : class, IRemoteResponseClassifier
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(transport);
            return registration.Configure(options => options.UseResponseClassifier<TClassifier>(transport));
        }
    }
}

public class ModuleRpcClient : MonicaModule<ModuleRpcClientOption>
{
    private const string NONE_DOMAIN_NAME = "None";

    private readonly List<BusinessTypeMatch> _relatedTypes = [];

    public override void Describe(ModuleDescriptor module)
    {
        module.Require<ModuleAuthentication, ModuleAuthenticationOption>();
        module.Require<ModuleDependencyInjection, ModuleDependencyInjectionOption>();
        module.Require<ModuleMediator, ModuleMediatorOption>();
        module.Require<ModuleResultEnvelope, ModuleResultEnvelopeOption>();
    }

    public override void ConfigureServices(ModuleContext<ModuleRpcClientOption> context)
    {
        var services = context.Services;
        services.TryAddScoped<ILocalRpcInvoker, MediatorLocalRpcInvoker>();
        if (Option.CallTimeout <= TimeSpan.Zero || Option.CallTimeout.TotalMilliseconds > uint.MaxValue - 1 ||
            Option.MaxResponseBodyBytes <= 0)
            throw new InvalidOperationException("RPC timeout and response size must be positive and within supported limits.");
        services.TryAddSingleton(TimeProvider.System);
        services.AddSingleton<RemoteResponseDecoder>();
        services.AddSingleton<IRemoteCallClient, RemoteCallFacade>();
        if (Option.ResponseClassifierType is { } classifierType)
            services.TryAddEnumerable(ServiceDescriptor.Singleton(typeof(IRemoteResponseClassifier), classifierType));
        services.AddHttpContextAccessor();
        services.AddTransient<AuthenticationDelegatingHandler>();
        if (Option.HttpClientRegisterProviderType is { } providerType)
        {
            services.TryAddSingleton(providerType);
        }
    }

    public override void DeclareTypeDiscovery(TypeDiscoveryPlan<ModuleRpcClientOption> discovery)
    {
        discovery.Match(
            TypeQuery.ClosedClass.SubclassOf<RpcApi>(),
            (_, matches) =>
            {
                _relatedTypes.Clear();
                _relatedTypes.AddRange(matches);
            });
    }

    public override void PostConfigureServices(ModuleContext<ModuleRpcClientOption> context)
    {
        if (Option.ApplicationModelRouting) return;
        var services = context.Services;
        var infoProvider = GetRequiredDomainInfoProvider();
        var selectedTransport = Option.Transport;

        ValidateTransportConfiguration(selectedTransport);

        var dependentDomains = GetDependentDomains(infoProvider);
        var supportedDomains = GetSupportedDomains(dependentDomains.GetType());
        var rpcClientsByDomain = BuildRpcClientsByDomain(supportedDomains, dependentDomains.GetType());
        var registeredInterfaces = new HashSet<Type>();
        var registeredAppIds = new HashSet<string>();

        foreach (var domain in supportedDomains.Where(dependentDomains.HasFlag))
        {
            if (!rpcClientsByDomain.TryGetValue(domain.ToString(), out var rpcClients))
            {
                throw new InvalidOperationException($"No generated RPC clients were discovered for selected domain '{domain}'. Configure generation and type discovery for this host.");
            }

            foreach (var rpcClient in SelectRpcClientsForTransport(rpcClients, selectedTransport))
            {
                RegisterRpcClient(services, infoProvider, domain, rpcClient, registeredInterfaces, registeredAppIds);
            }
        }
    }

    private IRpcClientDomainInfoProvider GetRequiredDomainInfoProvider()
    {
        return Option.DomainInfoProvider
               ?? throw new InvalidOperationException("Standalone RPC clients require ConfigDomainInfoProvider. Complete application hosts use AddApplicationRpc with a validated application model.");
    }

    private void ValidateTransportConfiguration(RpcTransportKind selectedTransport)
    {
        switch (selectedTransport)
        {
            case RpcTransportKind.Local when Option.HttpClientRegisterProviderType is not null:
                throw new InvalidOperationException("An HTTP RPC provider cannot be combined with Local transport. Select HTTP delivery or remove the provider.");
            case RpcTransportKind.Http when Option.HttpClientRegisterProviderType is null:
                throw new InvalidOperationException(
                    "Please configure an RPC HTTP client provider before using HTTP RPC transport.");
            case RpcTransportKind.Grpc:
                throw new NotImplementedException("Grpc is not supported now!");
        }
    }

    private static Enum GetDependentDomains(IRpcClientDomainInfoProvider infoProvider)
    {
        return infoProvider.GetDependencyDomains() as Enum
               ?? throw new InvalidOperationException(
                   $"{nameof(IRpcClientDomainInfoProvider)}.{nameof(IRpcClientDomainInfoProvider.GetDependencyDomains)} must return an enum instance.");
    }

    private static List<Enum> GetSupportedDomains(Type domainEnumType)
    {
        return Enum.GetValues(domainEnumType)
            .OfType<Enum>()
            .Where(domain => !string.Equals(domain.ToString(), NONE_DOMAIN_NAME, StringComparison.Ordinal))
            .ToList();
    }

    private Dictionary<string, List<RpcClientRegistration>> BuildRpcClientsByDomain(
        IEnumerable<Enum> supportedDomains,
        Type domainEnumType)
    {
        var validDomainNames = supportedDomains
            .Select(domain => domain.ToString())
            .ToHashSet(StringComparer.Ordinal);
        var rpcClientsByDomain = new Dictionary<string, List<RpcClientRegistration>>(StringComparer.Ordinal);

        foreach (var match in _relatedTypes)
        {
            var registration = CreateRpcClientRegistration(match, validDomainNames, domainEnumType);
            if (!rpcClientsByDomain.TryGetValue(registration.DomainName, out var registrations))
            {
                registrations = [];
                rpcClientsByDomain.Add(registration.DomainName, registrations);
            }

            registrations.Add(registration);
        }

        return rpcClientsByDomain;
    }

    private static RpcClientRegistration CreateRpcClientRegistration(
        BusinessTypeMatch match,
        HashSet<string> validDomainNames,
        Type domainEnumType)
    {
        var type = match.Type;
        var domainAttribute = match.Shape.GetAttribute<RpcClientDomainAttribute>(inherit: true)
                              ?? throw new InvalidOperationException(
                                  $"RPC client type {type.GetCleanFullName()} must declare [{nameof(RpcClientDomainAttribute)}] to participate in automatic registration.");
        if (!validDomainNames.Contains(domainAttribute.DomainName))
        {
            throw new InvalidOperationException(
                $"RPC client type {type.GetCleanFullName()} declares domain '{domainAttribute.DomainName}', but it is not defined by dependency enum {domainEnumType.GetCleanFullName()}.");
        }

        return new RpcClientRegistration(
            domainAttribute.DomainName,
            type,
            ResolveRpcInterface(match.Shape),
            ResolveTransportKind(match.Shape));
    }

    private static Type ResolveRpcInterface(BusinessTypeShape shape)
    {
        var interfaces = shape.Interfaces
            .Where(p => p != typeof(IRpcApi) && p.IsImplementInterface<IRpcApi>())
            .ToList();

        return interfaces.Count switch
        {
            1 => interfaces[0],
            0 => throw new InvalidOperationException(
                $"RPC client type {shape.Type.GetCleanFullName()} must implement exactly one interface extending {nameof(IRpcApi)}."),
            _ => throw new InvalidOperationException(
                $"There are multiple interfaces ({interfaces.Select(p => p.GetCleanFullName()).StringJoin(",")}) extend {nameof(IRpcApi)} for type {shape.Type.GetCleanFullName()}")
        };
    }

    private void RegisterRpcClient(
        IServiceCollection services,
        IRpcClientDomainInfoProvider infoProvider,
        Enum domain,
        RpcClientRegistration rpcClient,
        HashSet<Type> registeredInterfaces,
        HashSet<string> registeredAppIds)
    {
        if (!registeredInterfaces.Add(rpcClient.InterfaceType))
        {
            throw new InvalidOperationException(
                $"Interface {rpcClient.InterfaceType.GetCleanFullName()} has been registered, the type {rpcClient.ClientType.GetCleanFullName()} can not register again!");
        }

        if (rpcClient.ClientType.IsSubclassOf(typeof(HttpRpcApi)))
        {
            RegisterHttpRpcClient(services, infoProvider, domain, rpcClient, registeredAppIds);
            return;
        }

        services.TryAddTransient(rpcClient.InterfaceType, rpcClient.ClientType);
        Logger.LogInformation(
            "Register Domain ({domainName} - {domainDesc}) {transport} RPC {type} -> {interface}",
            domain.ToString(),
            domain.GetDescription(),
            rpcClient.TransportKind?.ToString() ?? "Custom",
            rpcClient.ClientType.Name,
            rpcClient.InterfaceType.Name);
    }

    private void RegisterHttpRpcClient(
        IServiceCollection services,
        IRpcClientDomainInfoProvider infoProvider,
        Enum domain,
        RpcClientRegistration rpcClient,
        HashSet<string> registeredAppIds)
    {
        if (Option.HttpClientRegisterProviderType is not { } httpClientRegisterProviderType)
        {
            throw new InvalidOperationException("Please config MoRPC http client provider to use rpc client!");
        }

        var descriptor = infoProvider.GetDomain(domain);
        var appid = descriptor.AppId;
        var clientName = RpcHttpClientRegistration.GetClientName(appid);
        if (registeredAppIds.Add(appid))
        {
            RpcHttpClientRegistration.Register(services, appid, httpClientRegisterProviderType, Option.CustomHttpClientBuilder);
        }

        services.TryAddTransient(rpcClient.InterfaceType, provider =>
        {
            var httpClientFactory = provider.GetRequiredService<IHttpClientFactory>();
            var client = httpClientFactory.CreateClient(clientName);
            return ActivatorUtilities.CreateInstance(provider, rpcClient.ClientType, client, descriptor);
        });

        Logger.LogInformation(
            "Register Domain ({domainName} - {domainDesc}) HTTP RPC {type} -> {interface}",
            domain.ToString(),
            domain.GetDescription(),
            rpcClient.ClientType.Name,
            rpcClient.InterfaceType.Name);
    }

    private static RpcTransportKind? ResolveTransportKind(BusinessTypeShape shape)
    {
        if (shape.IsSubclassOf(typeof(HttpRpcApi)))
        {
            return RpcTransportKind.Http;
        }

        if (shape.IsSubclassOf(typeof(LocalRpcApi)))
        {
            return RpcTransportKind.Local;
        }

        return null;
    }

    private static IReadOnlyList<RpcClientRegistration> SelectRpcClientsForTransport(
        IEnumerable<RpcClientRegistration> registrations,
        RpcTransportKind selectedTransport)
    {
        return registrations
            .GroupBy(registration => registration.InterfaceType)
            .Select(group => SelectRpcClientForTransport(group.ToList(), selectedTransport))
            .Where(static registration => registration != null)
            .Cast<RpcClientRegistration>()
            .ToList();
    }

    private static RpcClientRegistration? SelectRpcClientForTransport(
        IReadOnlyList<RpcClientRegistration> registrations,
        RpcTransportKind selectedTransport)
    {
        var transportMatches = registrations
            .Where(registration => registration.TransportKind == selectedTransport)
            .ToList();

        if (transportMatches.Count > 1)
        {
            throw new InvalidOperationException(
                $"Multiple RPC client implementations match transport '{selectedTransport}' for interface {registrations[0].InterfaceType.GetCleanFullName()}: {DescribeRegistrations(transportMatches)}.");
        }

        if (transportMatches.Count == 1)
        {
            return transportMatches[0];
        }

        var fallbackMatches = registrations
            .Where(registration => registration.TransportKind == null)
            .ToList();

        if (fallbackMatches.Count > 1)
        {
            throw new InvalidOperationException(
                $"Multiple fallback RPC client implementations were found for interface {registrations[0].InterfaceType.GetCleanFullName()}: {DescribeRegistrations(fallbackMatches)}.");
        }

        return fallbackMatches.Count == 1
            ? fallbackMatches[0]
            : throw new InvalidOperationException($"No RPC implementation matches selected transport '{selectedTransport}' for {registrations[0].InterfaceType.GetCleanFullName()}. Available: {DescribeRegistrations(registrations)}.");
    }

    private static string DescribeRegistrations(IEnumerable<RpcClientRegistration> registrations)
    {
        return registrations
            .Select(registration => $"{registration.ClientType.GetCleanFullName()} [{registration.TransportKind?.ToString() ?? "Custom"}]")
            .StringJoin(", ");
    }

    private sealed record RpcClientRegistration(
        string DomainName,
        Type ClientType,
        Type InterfaceType,
        RpcTransportKind? TransportKind);
}

public class ModuleRpcClientOption : ModuleOptions<ModuleRpcClient>
{
    /// <summary>Chooses the provider that configures this module's dedicated HTTP clients.</summary>
    /// <remarks>Provider modules can call this from Describe so transitive and direct registration agree.</remarks>
    public void UseHttpClientProvider<TProvider>() where TProvider : class, IRpcHttpClientRegisterProvider =>
        HttpClientRegisterProviderType = typeof(TProvider);

    /// <summary>Binds response classification to an explicit transport. The classifier is registered as a singleton.</summary>
    /// <remarks>The transport must equal the classifier's Transport value; direct HTTP has no classifier by default.</remarks>
    public void UseResponseClassifier<TClassifier>(string transport) where TClassifier : class, IRemoteResponseClassifier
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(transport);
        ProviderTransport = transport;
        ResponseClassifierType = typeof(TClassifier);
    }

    /// <summary>Gets or sets the complete call deadline, including response-body reads. Defaults to 60 seconds.</summary>
    /// <remarks>Borrowed clients retain their own timeout. A shorter client send timeout still takes effect.</remarks>
    public TimeSpan CallTimeout { get; set; } = TimeSpan.FromSeconds(60);

    /// <summary>Gets or sets the decoded response limit. Defaults to 16 MiB; increase for known large response contracts.</summary>
    public long MaxResponseBodyBytes { get; set; } = 16 * 1024 * 1024;

    internal string ProviderTransport { get; set; } = "http";
    internal Type? ResponseClassifierType { get; set; }
    /// <summary>
    /// Selects which generated RPC transport implementation should be registered.
    /// Defaults to <see cref="RpcTransportKind.Http"/> for distributed service-to-service calls.
    /// </summary>
    public RpcTransportKind Transport { get; set; } = RpcTransportKind.Http;

    /// <summary>
    /// Applies additional customization to HTTP client registrations when <see cref="Transport"/> is <see cref="RpcTransportKind.Http"/>.
    /// </summary>
    public Action<IHttpClientBuilder>? CustomHttpClientBuilder { get; internal set; }

    internal Type? HttpClientRegisterProviderType { get; set; }
    internal IRpcClientDomainInfoProvider? DomainInfoProvider { get; set; }
    internal bool ApplicationModelRouting { get; set; }
}
