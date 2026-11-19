using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Monica.Core;
using Monica.Core.Modularity;
using Monica.Core.Modularity.Abstractions;
using Monica.Core.Modularity.Models;
using Monica.DevOps.Git.Abstractions;
using Monica.DevOps.Git.Facades;
using Monica.DevOps.Git.Models;
using Monica.DevOps.Git.Providers;
using Monica.DevOps.Git.Services;

// ReSharper disable once CheckNamespace
namespace Monica.Modules;

/// <summary>
/// Builder extensions for the Git synchronization module.
/// </summary>
public static class ModuleGitBuilderExtensions
{
    extension(IMonicaBuilder builder)
    {
        /// <summary>
        /// Configures the Git synchronization module.
        /// </summary>
        public ModuleRegistration<ModuleGit, ModuleGitOption> AddGit(Action<ModuleGitOption>? action = null)
        {
            return builder.AddModule<ModuleGit, ModuleGitOption>(action);
        }
    }
}

/// <summary>
/// Git synchronization module.
/// </summary>
public class ModuleGit : MonicaModule<ModuleGitOption>, IWebModule
{
    /// <inheritdoc />
    public override void Describe(ModuleDescriptor module)
    {
        module.Require<ModuleEventBus>();
        module.Require<ModuleHostedService>();
    }

    /// <inheritdoc />
    public override void ConfigureServices(ModuleContext<ModuleGitOption> context)
    {
        var services = context.Services;
        services.TryAddSingleton<GitCredentialManager>();
        services.TryAddSingleton<IGitRepositoryService, GitRepositoryService>();
        services.TryAddSingleton<GitWebhookEndpointService>();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IGitCredentialResolver, GitOptionsCredentialResolver>());
        services.AddScoped<GitFacade>();

        if (Option.IsSyncTriggerEnabled(GitSyncTrigger.Startup))
        {
            services.AddHostedService<GitStartupSyncHostedService>();
        }
    }

    /// <inheritdoc />
    public override void ConfigureEndpoints(WebModuleContext<ModuleGitOption> context)
    {
        var app = context.ApplicationBuilder;
        UseEndpoints(context, endpoints =>
        {
            var tagName = Option.GetApiGroupName();

            endpoints.MapPost("/git/webhooks/{provider}", async (
                    [FromRoute] string provider,
                    HttpRequest request,
                    [FromServices] GitWebhookEndpointService service,
                    CancellationToken cancellationToken) =>
                {
                    return await service.HandleAsync(provider, request, cancellationToken);
                })
                .WithName("HandleGitWebhook")
                .WithTags(tagName)
                .WithSummary("Handle inbound Git webhooks")
                .WithDescription("Accepts normalized Git update events from configured webhook providers.");
        });
    }
}

/// <summary>
/// Registration extensions for the Git synchronization module.
/// </summary>
public static class ModuleGitRegistrationExtensions
{
    /// <summary>
    /// Sets which synchronization triggers are enabled.
    /// </summary>
    public static ModuleRegistration<ModuleGit, ModuleGitOption> UseSyncTriggers(this ModuleRegistration<ModuleGit, ModuleGitOption> module, params GitSyncTrigger[] triggers)
    {
        module.Configure(options => options.SetEnabledSyncTriggers(triggers));
        return module;
    }

    /// <summary>
    /// Adds a Git repository.
    /// </summary>
    public static ModuleRegistration<ModuleGit, ModuleGitOption> AddRepository(this ModuleRegistration<ModuleGit, ModuleGitOption> module,
        string id,
        string remoteUrl,
        string localPath,
        string? credentialId = null,
        string? provider = null,
        string? branch = null)
    {
        module.Configure(options =>
        {
            options.RepositoryRegistrations.Add(new GitRepositoryRegistration
            {
                Id = id,
                RemoteUrl = remoteUrl,
                LocalPath = localPath,
                CredentialId = credentialId,
                Provider = provider,
                Branch = branch
            });
        });

        return module;
    }

    /// <summary>
    /// Adds a token credential.
    /// </summary>
    public static ModuleRegistration<ModuleGit, ModuleGitOption> AddTokenCredential(this ModuleRegistration<ModuleGit, ModuleGitOption> module, string id, string token, string? userName = null)
    {
        module.Configure(options =>
        {
            options.CredentialRegistrations.Add(new GitCredentialRegistration
            {
                Id = id,
                AuthenticationType = GitAuthenticationType.Token,
                Token = token,
                UserName = userName
            });
        });

        return module;
    }

    /// <summary>
    /// Adds a user name and password credential.
    /// </summary>
    public static ModuleRegistration<ModuleGit, ModuleGitOption> AddUserPasswordCredential(this ModuleRegistration<ModuleGit, ModuleGitOption> module, string id, string userName, string password)
    {
        module.Configure(options =>
        {
            options.CredentialRegistrations.Add(new GitCredentialRegistration
            {
                Id = id,
                AuthenticationType = GitAuthenticationType.UserPassword,
                UserName = userName,
                Password = password
            });
        });

        return module;
    }

    /// <summary>
    /// Adds a custom credential resolver.
    /// </summary>
    public static ModuleRegistration<ModuleGit, ModuleGitOption> AddCredentialResolver<TResolver>(this ModuleRegistration<ModuleGit, ModuleGitOption> module) where TResolver : class, IGitCredentialResolver
    {
        module.ConfigureServices(context =>
        {
            context.Services.TryAddEnumerable(ServiceDescriptor.Singleton<IGitCredentialResolver, TResolver>());
        });

        return module;
    }

    /// <summary>
    /// Enables the built-in GitHub webhook provider.
    /// </summary>
    public static ModuleRegistration<ModuleGit, ModuleGitOption> UseGitHubWebhookProvider(this ModuleRegistration<ModuleGit, ModuleGitOption> module, Action<GitHubWebhookOption>? configure = null)
    {
        module.Configure(options =>
        {
            configure?.Invoke(options.GitHubWebhook);
        });

        module.ConfigureServices(context =>
        {
            context.Services.TryAddEnumerable(ServiceDescriptor.Singleton<IGitWebhookProvider, GitHubWebhookProvider>());
        });

        return module;
    }

    /// <summary>
    /// Enables the built-in GitLab webhook provider.
    /// </summary>
    public static ModuleRegistration<ModuleGit, ModuleGitOption> UseGitLabWebhookProvider(this ModuleRegistration<ModuleGit, ModuleGitOption> module, Action<GitLabWebhookOption>? configure = null)
    {
        module.Configure(options =>
        {
            configure?.Invoke(options.GitLabWebhook);
        });

        module.ConfigureServices(context =>
        {
            context.Services.TryAddEnumerable(ServiceDescriptor.Singleton<IGitWebhookProvider, GitLabWebhookProvider>());
        });

        return module;
    }

    /// <summary>
    /// Registers a custom webhook provider.
    /// </summary>
    public static ModuleRegistration<ModuleGit, ModuleGitOption> AddWebhookProvider<TProvider>(this ModuleRegistration<ModuleGit, ModuleGitOption> module, string? registrationKey = null)
        where TProvider : class, IGitWebhookProvider
    {
        module.ConfigureServices(context =>
        {
            context.Services.TryAddEnumerable(ServiceDescriptor.Singleton<IGitWebhookProvider, TProvider>());
        });

        return module;
    }

}

/// <summary>
/// Options for the Git synchronization module.
/// </summary>
public class ModuleGitOption : MinimalApiModuleOptions<ModuleGit>
{
    /// <summary>
    /// Gets the synchronization triggers that are allowed to execute.
    /// </summary>
    public HashSet<GitSyncTrigger> EnabledSyncTriggers { get; set; } =
    [
        GitSyncTrigger.Startup,
        GitSyncTrigger.Webhook,
        GitSyncTrigger.Manual
    ];

    /// <summary>
    /// Gets the configured repository registrations.
    /// </summary>
    public List<GitRepositoryRegistration> RepositoryRegistrations { get; set; } = [];

    /// <summary>
    /// Gets the configured credential registrations.
    /// </summary>
    public List<GitCredentialRegistration> CredentialRegistrations { get; set; } = [];

    /// <summary>
    /// Gets or sets the GitHub webhook provider configuration.
    /// </summary>
    public GitHubWebhookOption GitHubWebhook { get; set; } = new();

    /// <summary>
    /// Gets or sets the GitLab webhook provider configuration.
    /// </summary>
    public GitLabWebhookOption GitLabWebhook { get; set; } = new();

    /// <summary>
    /// Replaces the enabled synchronization triggers.
    /// </summary>
    public void SetEnabledSyncTriggers(params GitSyncTrigger[] triggers)
    {
        EnabledSyncTriggers = triggers?
            .Distinct()
            .ToHashSet()
            ?? [];
    }

    /// <summary>
    /// Determines whether the specified synchronization trigger is enabled.
    /// </summary>
    public bool IsSyncTriggerEnabled(GitSyncTrigger trigger)
    {
        return EnabledSyncTriggers.Contains(trigger);
    }
}
