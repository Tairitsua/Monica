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
using Monica.Invocation.Abstractions;
using Monica.Invocation.Annotations;
using Monica.Invocation.Models;
using Monica.Invocation.Facades;
using Monica.Invocation.Services;

// ReSharper disable once CheckNamespace
namespace Monica.Modules;

/// <summary>Configures explicitly selected API clients and shared HTTP delivery services.</summary>
public static class ModuleApiClientBuilderExtensions
{
    extension(IMonicaBuilder builder)
    {
        /// <summary>
        /// Registers and configures the API client module.
        /// </summary>
        /// <remarks>Standalone clients default to HTTP and require a domain-info provider and an HTTP provider. Application-model hosts use AddApplicationInvocation for per-operation routing.</remarks>
        public ModuleRegistration<ModuleApiClient, ModuleApiClientOption> AddApiClient(
            Action<ModuleApiClientOption>? action = null)
        {
            return builder.AddModule<ModuleApiClient, ModuleApiClientOption>(action);
        }
    }

    extension(ModuleRegistration<ModuleApiClient, ModuleApiClientOption> registration)
    {
        /// <summary>
        /// Selects HTTP transport implementations for API contracts.
        /// </summary>
        public ModuleRegistration<ModuleApiClient, ModuleApiClientOption> UseHttpTransport()
        {
            return registration.Configure(options => options.Transport = ApiClientTransportKind.Http);
        }

        /// <summary>
        /// Selects in-process transport implementations for API contracts.
        /// </summary>
        public ModuleRegistration<ModuleApiClient, ModuleApiClientOption> UseLocalTransport()
        {
            return registration.Configure(options => options.Transport = ApiClientTransportKind.Local);
        }

        /// <summary>
        /// Selects the reserved gRPC transport, which currently fails validation at composition time.
        /// </summary>
        public ModuleRegistration<ModuleApiClient, ModuleApiClientOption> UseGrpcTransport()
        {
            return registration.Configure(options => options.Transport = ApiClientTransportKind.Grpc);
        }

        /// <summary>
        /// Selects the provider responsible for named HTTP-client registration.
        /// </summary>
        public ModuleRegistration<ModuleApiClient, ModuleApiClientOption> ConfigHttpClientRegisterProvider<TProvider>()
            where TProvider : class, IApiHttpClientRegisterProvider
        {
            return registration.Configure(options => options.UseHttpClientProvider<TProvider>());
        }

        /// <summary>
        /// Configures the dependency-domain resolver used to select generated clients.
        /// </summary>
        public ModuleRegistration<ModuleApiClient, ModuleApiClientOption> ConfigDomainInfoProvider(
            IApiClientDomainInfoProvider domainInfoProvider)
        {
            ArgumentNullException.ThrowIfNull(domainInfoProvider);
            return registration
                .Configure(options => options.DomainInfoProvider = domainInfoProvider);
        }

        /// <summary>
        /// Applies additional HTTP-client customization to every HTTP API registration.
        /// </summary>
        public ModuleRegistration<ModuleApiClient, ModuleApiClientOption> ConfigHttpClientBuilder(
            Action<IHttpClientBuilder> configure)
        {
            ArgumentNullException.ThrowIfNull(configure);
            return registration.Configure(options => options.CustomHttpClientBuilder = configure);
        }

        /// <summary>Binds provider-error classification to an explicitly selected transport.</summary>
        public ModuleRegistration<ModuleApiClient, ModuleApiClientOption> ConfigResponseClassifier<TClassifier>(string transport)
            where TClassifier : class, IRemoteResponseClassifier
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(transport);
            return registration.Configure(options => options.UseResponseClassifier<TClassifier>(transport));
        }
    }
}

/// <summary>Registers standalone API clients and the Local and HTTP services shared by application invocation.</summary>
public class ModuleApiClient : MonicaModule<ModuleApiClientOption>
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

    public override void ConfigureServices(ModuleContext<ModuleApiClientOption> context)
    {
        var services = context.Services;
        services.TryAddScoped<ILocalApiInvoker, MediatorLocalApiInvoker>();
        if (Option.CallTimeout <= TimeSpan.Zero || Option.CallTimeout.TotalMilliseconds > uint.MaxValue - 1 ||
            Option.MaxResponseBodyBytes <= 0)
            throw new InvalidOperationException("API timeout and response size must be positive and within supported limits.");
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

    public override void DeclareTypeDiscovery(TypeDiscoveryPlan<ModuleApiClientOption> discovery)
    {
        discovery.Match(
            TypeQuery.ClosedClass.SubclassOf<ApiClient>(),
            (_, matches) =>
            {
                _relatedTypes.Clear();
                _relatedTypes.AddRange(matches);
            });
    }

    public override void PostConfigureServices(ModuleContext<ModuleApiClientOption> context)
    {
        if (Option.ApplicationModelRouting) return;
        var services = context.Services;
        var infoProvider = GetRequiredDomainInfoProvider();
        var selectedTransport = Option.Transport;

        ValidateTransportConfiguration(selectedTransport);

        var dependentDomains = GetDependentDomains(infoProvider);
        var supportedDomains = GetSupportedDomains(dependentDomains.GetType());
        var apiClientsByDomain = BuildApiClientsByDomain(supportedDomains, dependentDomains.GetType());
        var registeredInterfaces = new HashSet<Type>();
        var registeredAppIds = new HashSet<string>();

        foreach (var domain in supportedDomains.Where(dependentDomains.HasFlag))
        {
            if (!apiClientsByDomain.TryGetValue(domain.ToString(), out var apiClients))
            {
                throw new InvalidOperationException($"No generated API clients were discovered for selected domain '{domain}'. Configure generation and type discovery for this host.");
            }

            foreach (var apiClient in SelectApiClientsForTransport(apiClients, selectedTransport))
            {
                RegisterApiClient(services, infoProvider, domain, apiClient, registeredInterfaces, registeredAppIds);
            }
        }
    }

    private IApiClientDomainInfoProvider GetRequiredDomainInfoProvider()
    {
        return Option.DomainInfoProvider
               ?? throw new InvalidOperationException("Standalone API clients require ConfigDomainInfoProvider. Complete application hosts use AddApplicationInvocation with a validated application model.");
    }

    private void ValidateTransportConfiguration(ApiClientTransportKind selectedTransport)
    {
        switch (selectedTransport)
        {
            case ApiClientTransportKind.Local when Option.HttpClientRegisterProviderType is not null:
                throw new InvalidOperationException("An HTTP API provider cannot be combined with Local transport. Select HTTP delivery or remove the provider.");
            case ApiClientTransportKind.Http when Option.HttpClientRegisterProviderType is null:
                throw new InvalidOperationException(
                    "Please configure an API HTTP client provider before using HTTP API transport.");
            case ApiClientTransportKind.Grpc:
                throw new NotImplementedException("Grpc is not supported now!");
        }
    }

    private static Enum GetDependentDomains(IApiClientDomainInfoProvider infoProvider)
    {
        return infoProvider.GetDependencyDomains() as Enum
               ?? throw new InvalidOperationException(
                   $"{nameof(IApiClientDomainInfoProvider)}.{nameof(IApiClientDomainInfoProvider.GetDependencyDomains)} must return an enum instance.");
    }

    private static List<Enum> GetSupportedDomains(Type domainEnumType)
    {
        return Enum.GetValues(domainEnumType)
            .OfType<Enum>()
            .Where(domain => !string.Equals(domain.ToString(), NONE_DOMAIN_NAME, StringComparison.Ordinal))
            .ToList();
    }

    private Dictionary<string, List<ApiClientRegistration>> BuildApiClientsByDomain(
        IEnumerable<Enum> supportedDomains,
        Type domainEnumType)
    {
        var validDomainNames = supportedDomains
            .Select(domain => domain.ToString())
            .ToHashSet(StringComparer.Ordinal);
        var apiClientsByDomain = new Dictionary<string, List<ApiClientRegistration>>(StringComparer.Ordinal);

        foreach (var match in _relatedTypes)
        {
            var registration = CreateApiClientRegistration(match, validDomainNames, domainEnumType);
            if (!apiClientsByDomain.TryGetValue(registration.DomainName, out var registrations))
            {
                registrations = [];
                apiClientsByDomain.Add(registration.DomainName, registrations);
            }

            registrations.Add(registration);
        }

        return apiClientsByDomain;
    }

    private static ApiClientRegistration CreateApiClientRegistration(
        BusinessTypeMatch match,
        HashSet<string> validDomainNames,
        Type domainEnumType)
    {
        var type = match.Type;
        var domainAttribute = match.Shape.GetAttribute<ApiClientDomainAttribute>(inherit: true)
                              ?? throw new InvalidOperationException(
                                  $"API client type {type.GetCleanFullName()} must declare [{nameof(ApiClientDomainAttribute)}] to participate in automatic registration.");
        if (!validDomainNames.Contains(domainAttribute.DomainName))
        {
            throw new InvalidOperationException(
                $"API client type {type.GetCleanFullName()} declares domain '{domainAttribute.DomainName}', but it is not defined by dependency enum {domainEnumType.GetCleanFullName()}.");
        }

        return new ApiClientRegistration(
            domainAttribute.DomainName,
            type,
            ResolveApiInterface(match.Shape),
            ResolveTransportKind(match.Shape));
    }

    private static Type ResolveApiInterface(BusinessTypeShape shape)
    {
        var interfaces = shape.Interfaces
            .Where(p => p != typeof(IApplicationApi) && p.IsImplementInterface<IApplicationApi>())
            .ToList();

        return interfaces.Count switch
        {
            1 => interfaces[0],
            0 => throw new InvalidOperationException(
                $"API client type {shape.Type.GetCleanFullName()} must implement exactly one interface extending {nameof(IApplicationApi)}."),
            _ => throw new InvalidOperationException(
                $"There are multiple interfaces ({interfaces.Select(p => p.GetCleanFullName()).StringJoin(",")}) extend {nameof(IApplicationApi)} for type {shape.Type.GetCleanFullName()}")
        };
    }

    private void RegisterApiClient(
        IServiceCollection services,
        IApiClientDomainInfoProvider infoProvider,
        Enum domain,
        ApiClientRegistration apiClient,
        HashSet<Type> registeredInterfaces,
        HashSet<string> registeredAppIds)
    {
        if (!registeredInterfaces.Add(apiClient.InterfaceType))
        {
            throw new InvalidOperationException(
                $"Interface {apiClient.InterfaceType.GetCleanFullName()} has been registered, the type {apiClient.ClientType.GetCleanFullName()} can not register again!");
        }

        if (apiClient.ClientType.IsSubclassOf(typeof(HttpApiClient)))
        {
            RegisterHttpApiClient(services, infoProvider, domain, apiClient, registeredAppIds);
            return;
        }

        services.TryAddTransient(apiClient.InterfaceType, apiClient.ClientType);
        Logger.LogInformation(
            "Register Domain ({domainName} - {domainDesc}) {transport} API {type} -> {interface}",
            domain.ToString(),
            domain.GetDescription(),
            apiClient.TransportKind?.ToString() ?? "Custom",
            apiClient.ClientType.Name,
            apiClient.InterfaceType.Name);
    }

    private void RegisterHttpApiClient(
        IServiceCollection services,
        IApiClientDomainInfoProvider infoProvider,
        Enum domain,
        ApiClientRegistration apiClient,
        HashSet<string> registeredAppIds)
    {
        if (Option.HttpClientRegisterProviderType is not { } httpClientRegisterProviderType)
        {
            throw new InvalidOperationException("Configure an API HTTP client provider before registering HTTP clients.");
        }

        var descriptor = infoProvider.GetDomain(domain);
        var appid = descriptor.AppId;
        var clientName = ApiHttpClientRegistration.GetClientName(appid);
        if (registeredAppIds.Add(appid))
        {
            ApiHttpClientRegistration.Register(services, appid, httpClientRegisterProviderType, Option.CustomHttpClientBuilder);
        }

        services.TryAddTransient(apiClient.InterfaceType, provider =>
        {
            var httpClientFactory = provider.GetRequiredService<IHttpClientFactory>();
            var client = httpClientFactory.CreateClient(clientName);
            return ActivatorUtilities.CreateInstance(provider, apiClient.ClientType, client, descriptor);
        });

        Logger.LogInformation(
            "Register Domain ({domainName} - {domainDesc}) HTTP API {type} -> {interface}",
            domain.ToString(),
            domain.GetDescription(),
            apiClient.ClientType.Name,
            apiClient.InterfaceType.Name);
    }

    private static ApiClientTransportKind? ResolveTransportKind(BusinessTypeShape shape)
    {
        if (shape.IsSubclassOf(typeof(HttpApiClient)))
        {
            return ApiClientTransportKind.Http;
        }

        if (shape.IsSubclassOf(typeof(LocalApiClient)))
        {
            return ApiClientTransportKind.Local;
        }

        return null;
    }

    private static IReadOnlyList<ApiClientRegistration> SelectApiClientsForTransport(
        IEnumerable<ApiClientRegistration> registrations,
        ApiClientTransportKind selectedTransport)
    {
        return registrations
            .GroupBy(registration => registration.InterfaceType)
            .Select(group => SelectApiClientForTransport(group.ToList(), selectedTransport))
            .Where(static registration => registration != null)
            .Cast<ApiClientRegistration>()
            .ToList();
    }

    private static ApiClientRegistration? SelectApiClientForTransport(
        IReadOnlyList<ApiClientRegistration> registrations,
        ApiClientTransportKind selectedTransport)
    {
        var transportMatches = registrations
            .Where(registration => registration.TransportKind == selectedTransport)
            .ToList();

        if (transportMatches.Count > 1)
        {
            throw new InvalidOperationException(
                $"Multiple API client implementations match transport '{selectedTransport}' for interface {registrations[0].InterfaceType.GetCleanFullName()}: {DescribeRegistrations(transportMatches)}.");
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
                $"Multiple fallback API client implementations were found for interface {registrations[0].InterfaceType.GetCleanFullName()}: {DescribeRegistrations(fallbackMatches)}.");
        }

        return fallbackMatches.Count == 1
            ? fallbackMatches[0]
            : throw new InvalidOperationException($"No API implementation matches selected transport '{selectedTransport}' for {registrations[0].InterfaceType.GetCleanFullName()}. Available: {DescribeRegistrations(registrations)}.");
    }

    private static string DescribeRegistrations(IEnumerable<ApiClientRegistration> registrations)
    {
        return registrations
            .Select(registration => $"{registration.ClientType.GetCleanFullName()} [{registration.TransportKind?.ToString() ?? "Custom"}]")
            .StringJoin(", ");
    }

    private sealed record ApiClientRegistration(
        string DomainName,
        Type ClientType,
        Type InterfaceType,
        ApiClientTransportKind? TransportKind);
}

/// <summary>Configures explicit API client selection and the shared remote call limits.</summary>
public class ModuleApiClientOption : ModuleOptions<ModuleApiClient>
{
    /// <summary>Chooses the provider that configures this module's dedicated HTTP clients.</summary>
    /// <remarks>Provider modules can call this from Describe so transitive and direct registration agree.</remarks>
    public void UseHttpClientProvider<TProvider>() where TProvider : class, IApiHttpClientRegisterProvider =>
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
    /// Selects which generated API transport implementation should be registered.
    /// Defaults to <see cref="ApiClientTransportKind.Http"/> for distributed service-to-service calls.
    /// </summary>
    public ApiClientTransportKind Transport { get; set; } = ApiClientTransportKind.Http;

    /// <summary>
    /// Applies additional customization to HTTP client registrations when <see cref="Transport"/> is <see cref="ApiClientTransportKind.Http"/>.
    /// </summary>
    public Action<IHttpClientBuilder>? CustomHttpClientBuilder { get; internal set; }

    internal Type? HttpClientRegisterProviderType { get; set; }
    internal IApiClientDomainInfoProvider? DomainInfoProvider { get; set; }
    internal bool ApplicationModelRouting { get; set; }
}
