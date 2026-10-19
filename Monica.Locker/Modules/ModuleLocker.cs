using Microsoft.Extensions.DependencyInjection;
using Monica.Core;
using Monica.Locker.Abstractions;
using Monica.Core.Modularity;
using Monica.Core.Modularity.Abstractions;
using Monica.Locker.Models;
using Monica.Locker.Providers.InProcess;
using Monica.Locker.Providers.Medallion;
using Monica.Locker.Services;
using MedallionDistributedLockProvider = Medallion.Threading.IDistributedLockProvider;

// ReSharper disable once CheckNamespace
namespace Monica.Modules;

public static class ModuleLockerBuilderExtensions
{
    extension(IMonicaBuilder builder)
    {
        /// <summary>
        /// Registers the Locker module and returns its host-bound provider configuration.
        /// </summary>
        /// <param name="action">Optional module option configuration.</param>
        /// <returns>The locker registration used to select a concrete lock provider.</returns>
        public ModuleRegistration<ModuleLocker, ModuleLockerOption> AddLocker(
            Action<ModuleLockerOption>? action = null)
        {
            return builder.AddModule<ModuleLocker, ModuleLockerOption>(action);
        }
    }

    extension(ModuleRegistration<ModuleLocker, ModuleLockerOption> registration)
    {
        /// <summary>Selects a singleton lock provider for the current host.</summary>
        /// <typeparam name="TProvider">The provider implementation and its singleton-compatible dependencies.</typeparam>
        /// <returns>The current Locker registration.</returns>
        public ModuleRegistration<ModuleLocker, ModuleLockerOption> UseProvider<TProvider>()
            where TProvider : class, ILockProvider
        {
            return registration
                .ConfigureServices(context => context.Services.AddSingleton<ILockProvider, TProvider>())
                .SatisfyFeature(ModuleLocker.PROVIDER_FEATURE);
        }

        /// <summary>Adapts a caller-owned native Medallion provider as a singleton.</summary>
        /// <param name="distributedLockProvider">The pre-created provider; its caller owns it and its backend connections.</param>
        /// <returns>The current Locker registration.</returns>
        /// <remarks>
        /// The container does not dispose an externally supplied instance. Keep it and its connections alive until
        /// all acquired handles have been released. Native backend options own lease expiry and renewal.
        /// </remarks>
        public ModuleRegistration<ModuleLocker, ModuleLockerOption> UseMedallionProvider(
            MedallionDistributedLockProvider distributedLockProvider)
        {
            ArgumentNullException.ThrowIfNull(distributedLockProvider);
            return registration
                .UseProvider<MedallionLockProvider>()
                .ConfigureServices(context => context.Services.AddSingleton(distributedLockProvider));
        }

        /// <summary>Creates and adapts one host-owned native Medallion provider through dependency injection.</summary>
        /// <param name="providerFactory">Creates the singleton provider from singleton-compatible host dependencies.</param>
        /// <returns>The current Locker registration.</returns>
        /// <remarks>
        /// The container invokes this factory on first resolution and owns disposal of a disposable result.
        /// Backend connections remain owned by the registrations that create them; register such connections with
        /// singleton factories when the host should dispose them. Do not capture a scoped repository or create
        /// another service provider. All acquired handles must be released before host disposal. Configure lease
        /// expiry and renewal on the native provider; per-acquisition LeaseDuration is not applied by the adapter.
        /// </remarks>
        public ModuleRegistration<ModuleLocker, ModuleLockerOption> UseMedallionProvider(
            Func<IServiceProvider, MedallionDistributedLockProvider> providerFactory)
        {
            ArgumentNullException.ThrowIfNull(providerFactory);
            return registration
                .UseProvider<MedallionLockProvider>()
                .ConfigureServices(context => context.Services.AddSingleton<MedallionDistributedLockProvider>(providerFactory));
        }

        /// <summary>Selects process-local keyed semaphores without coordination across host instances.</summary>
        /// <returns>The current Locker registration.</returns>
        public ModuleRegistration<ModuleLocker, ModuleLockerOption> UseInProcessProvider()
        {
            return registration.UseProvider<InProcessLockProvider>();
        }
    }
}

/// <summary>
/// Registers the shared locker services and exposes <see cref="IDistributedLock"/> to application code.
/// </summary>
public class ModuleLocker : MonicaModule<ModuleLockerOption>
{
    internal const string PROVIDER_FEATURE = "lock-provider";

    public override void Describe(ModuleDescriptor module)
    {
        module.RequireFeature(PROVIDER_FEATURE);
    }

    public override void ConfigureServices(ModuleContext<ModuleLockerOption> context)
    {
        context.Services.AddSingleton<LockKeyNormalizer>();
        context.Services.AddSingleton<IDistributedLock, DistributedLockService>();
    }
}

/// <summary>
/// Configures cross-provider locker defaults.
/// </summary>
public class ModuleLockerOption : ModuleOptions<ModuleLocker>
{
    /// <summary>
    /// Prefix applied to every logical lock name before it reaches the active provider.
    /// </summary>
    /// <remarks>
    /// Defaults to an empty string and is concatenated without an added separator. Participants protecting the same
    /// resource must use the same backend, prefix, and resource-name format. Add deployment isolation here when needed;
    /// the native Medallion adapter does not add an AppId or process identifier.
    /// </remarks>
    public string LockKeyPrefix { get; set; } = string.Empty;

    /// <summary>
    /// Default time to wait for a lock when a caller does not provide <see cref="LockAcquisitionOptions.WaitTimeout"/>.
    /// </summary>
    /// <remarks>Defaults to two minutes and does not limit the protected operation's duration.</remarks>
    public TimeSpan DefaultWaitTimeout { get; set; } = TimeSpan.FromMinutes(2);
}
