using System.Text;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.IdentityModel.Tokens;
using Monica.Authority.Authentication.Abstractions;
using Monica.Authority.Authentication.Services;
using Monica.Authority.Identity.Abstractions;
using Monica.Authority.Identity.Models;
using Monica.Authority.Identity.Services;
using Monica.Authority.Localization;
using Monica.Core;
using Monica.Core.Modularity;
using Monica.Core.Modularity.Abstractions;
using Monica.Core.Modularity.Models;

// ReSharper disable once CheckNamespace
namespace Monica.Modules;

public static class ModuleAuthenticationBuilderExtensions
{
    extension(IMonicaBuilder builder)
    {
        /// <summary>
        /// Configure the Authentication module
        /// </summary>
        public ModuleRegistration<ModuleAuthentication, ModuleAuthenticationOption> AddAuthentication(
            Action<ModuleAuthenticationOption>? action = null)
        {
            return builder.AddModule<ModuleAuthentication, ModuleAuthenticationOption>(action);
        }
    }

    extension(ModuleRegistration<ModuleAuthentication, ModuleAuthenticationOption> registration)
    {
        public ModuleRegistration<ModuleAuthentication, ModuleAuthenticationOption> ConfigSystemUser<T>(
            T curSystemEnum,
            Action<SystemUserOptions>? configure = null)
            where T : struct, Enum
        {
            return registration.ConfigureServices(context =>
                context.Services.Configure((SystemUserOptions options) =>
                {
                    options.SetCurSystemUser(curSystemEnum);
                    configure?.Invoke(options);
                }));
        }

        public ModuleRegistration<ModuleAuthentication, ModuleAuthenticationOption> ConfigDefaultSystemUser(
            Action<SystemUserOptions>? configure = null)
        {
            return registration.ConfigSystemUser(EDefaultSystemUser.System, configure);
        }

        /// <summary>
        /// Allows bearer tokens in the query string for narrowly scoped transport paths.
        /// </summary>
        public ModuleRegistration<ModuleAuthentication, ModuleAuthenticationOption> AllowQueryStringAccessTokens(
            params string[] pathPrefixes)
        {
            return registration.Configure(options => options.AllowQueryStringAccessTokens(pathPrefixes));
        }
    }
}

public class ModuleAuthentication : MonicaModule<ModuleAuthenticationOption>, IWebHostRequiredModule
{
    public override void Describe(ModuleDescriptor module)
    {
        module.AfterIfPresent<ModuleCors, ModuleCorsOption>();
        module.Require<ModuleLocalization, ModuleLocalizationOption>(
            static localization => localization.AddResource<AuthorityResource>());
    }

    public override void ConfigureServices(ModuleContext<ModuleAuthenticationOption> context)
    {
        var services = context.Services;
        services.TryAddSingleton<AuthorityMessageLocalizer>();

        // Relies on AsyncLocal so the async static singleton keeps a separate HttpContext per request thread
        services.AddHttpContextAccessor();
        services.AddScoped<ICurrentUser, CurrentUser>();
        services.AddSingleton<IJwtAuthManager, JwtAuthManager>();
        services.AddSingleton<IAccessTokenIssuer, JwtAuthManager>();
        services.AddSingleton<ICurrentPrincipalAccessor, CurrentPrincipalAccessor>(); // Singleton is sufficient here
        services.AddSingleton<CurrentPrincipalContext>();

        services.AddSingleton<IPasswordCrypto, PasswordCrypto>();
        services.Configure<SystemUserOptions>(options => options.SetCurSystemUser(EDefaultSystemUser.System));
        services.AddSingleton<ISystemUserManager, SystemUserManager>();
        services.AddAuthentication(x =>
        {
            x.DefaultAuthenticateScheme = JwtBearerDefaults.AuthenticationScheme;
            x.DefaultChallengeScheme = JwtBearerDefaults.AuthenticationScheme;
        }).AddJwtBearer(x =>
        {
            x.RequireHttpsMetadata = true;
            x.SaveToken = true;
            x.TokenValidationParameters = new TokenValidationParameters
            {
                ValidateIssuer = true,
                ValidIssuer = Option.Issuer,
                ValidateIssuerSigningKey = true,
                IssuerSigningKey = Option.SecurityKey,
                ValidAudience = Option.Audience,
                ValidateAudience = true,
                ValidateLifetime = true,
                ClockSkew = TimeSpan.FromMinutes(1)
            };
            x.Events = new JwtBearerEvents
            {
                OnMessageReceived = context =>
                {
                    var accessToken = context.Request.Query["access_token"].ToString();
                    var requestPath = context.Request.Path;
                    var isAllowedPath = Option.QueryStringAccessTokenPathPrefixes.Any(prefix =>
                        requestPath.StartsWithSegments(
                            new PathString(prefix),
                            StringComparison.OrdinalIgnoreCase));

                    if (isAllowedPath && !string.IsNullOrEmpty(accessToken))
                    {
                        context.Token = accessToken;
                    }

                    return Task.CompletedTask;
                }
            };
        });

        #region Controller handling
        // Could also implement this through the Options.Event in AddJwtBearer?
        //services.AddControllers(o =>
        //{
        //    o.Filters.Add(new CustomAuthorizeFilter());
        //});

        #endregion
    }

    // Must be registered after the CORS middleware; otherwise CORS stops working
    public override void ConfigureApplicationBuilder(WebModuleContext<ModuleAuthenticationOption> context)
    {
        context.ApplicationBuilder.UseAuthentication();
    }

    protected override ModuleWebStage GetApplicationBuilderStage() => ModuleWebStage.AfterRouting;
}

/// <summary>
/// Configures JWT validation and token lifetimes for the current Monica host.
/// </summary>
public class ModuleAuthenticationOption : ModuleOptions<ModuleAuthentication>
{
    internal List<string> QueryStringAccessTokenPathPrefixes { get; } = [];

    /// <summary>
    /// Adds path prefixes where bearer tokens may also be read from the <c>access_token</c> query-string parameter.
    /// </summary>
    /// <remarks>
    /// Browser transports such as WebSocket cannot set the Authorization header, so narrowly scoped transport paths
    /// (for example SignalR hub routes) need query-string token support. Site-wide <c>/</c> prefixes are rejected.
    /// </remarks>
    public void AllowQueryStringAccessTokens(IEnumerable<string> pathPrefixes)
    {
        ArgumentNullException.ThrowIfNull(pathPrefixes);
        var normalizedPrefixes = pathPrefixes
            .Select(NormalizeQueryStringTokenPathPrefix)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (normalizedPrefixes.Length == 0)
        {
            throw new ArgumentException("At least one query-string access-token path prefix is required.", nameof(pathPrefixes));
        }

        foreach (var prefix in normalizedPrefixes)
        {
            if (!QueryStringAccessTokenPathPrefixes.Contains(prefix, StringComparer.OrdinalIgnoreCase))
            {
                QueryStringAccessTokenPathPrefixes.Add(prefix);
            }
        }
    }

    private static string NormalizeQueryStringTokenPathPrefix(string pathPrefix)
    {
        if (string.IsNullOrWhiteSpace(pathPrefix))
        {
            throw new ArgumentException("A query-string access-token path prefix cannot be empty.", nameof(pathPrefix));
        }

        var normalized = "/" + pathPrefix.Trim().Trim('/');
        if (normalized == "/")
        {
            throw new ArgumentException(
                "Query-string access tokens cannot be enabled for the entire application.",
                nameof(pathPrefix));
        }

        return normalized;
    }

    /// <summary>
    /// Gets the signing key derived from <see cref="Secret"/>.
    /// </summary>
    public SymmetricSecurityKey SecurityKey => new(Encoding.ASCII.GetBytes(Secret.PadRight(512 / 8, '\0')));

    /// <summary>
    /// Gets or sets the stable secret used to sign access and refresh tokens.
    /// </summary>
    /// <remarks>
    /// The built-in value is a development placeholder. Production hosts must supply protected key material
    /// with at least 128 bits of entropy and preserve it for the intended token-validation lifetime.
    /// </remarks>
    public string Secret { get; set; } = nameof(Secret) + nameof(Secret);

    /// <summary>
    /// Gets or sets the issuer that generated and validates Monica JWTs.
    /// </summary>
    public string Issuer { get; set; } = nameof(Issuer);

    /// <summary>
    /// Gets or sets the audience accepted by Monica JWT validation.
    /// </summary>
    public string Audience { get; set; } = nameof(Audience);

    /// <summary>
    /// Gets or sets the access-token lifetime in minutes. The default is 60 minutes.
    /// </summary>
    public int AccessTokenExpiration { get; set; } = 60;

    /// <summary>
    /// Gets or sets the refresh-token lifetime in minutes. The default is 120 minutes.
    /// </summary>
    public int RefreshTokenExpiration { get; set; } = 120;
}
