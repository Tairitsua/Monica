using Monica.Core;
using Monica.Core.Clock;
using Monica.Core.Modularity;
using Monica.Core.Modularity.Abstractions;

// ReSharper disable once CheckNamespace
namespace Monica.Modules;

public static class ModuleClockBuilderExtensions
{
    extension(IMonicaBuilder builder)
    {
        /// <summary>
        /// Configures the Clock module, which owns the deployment's wall-clock timezone policy.
        /// </summary>
        /// <param name="action">An optional configuration delegate for <see cref="ModuleClockOption" />.</param>
        public ModuleRegistration<ModuleClock, ModuleClockOption> AddClock(Action<ModuleClockOption>? action = null)
        {
            return builder.AddModule<ModuleClock, ModuleClockOption>(action);
        }
    }
}

/// <summary>
/// The deployment clock policy. It owns the single wall-clock timezone that local timestamps are
/// rendered in, independent of any host's operating-system timezone. It registers no services.
/// </summary>
public class ModuleClock : MonicaModule<ModuleClockOption>
{
}

/// <summary>
/// Options for <see cref="ModuleClock" />.
/// </summary>
public class ModuleClockOption : ModuleOptions<ModuleClock>
{
    /// <summary>
    /// Gets or sets the deployment's wall-clock timezone used to stamp business audit timestamps
    /// (creation, modification, deletion) and display Monica UI timestamps.
    /// The default <see langword="null" /> keeps UTC; the UI never falls back to the server timezone.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Configure the identical value on every host writing the same database; mixed values make stored
    /// timestamps incomparable. The value is converted from UTC on every stamp, so it never depends on a
    /// host's operating-system timezone. Choose a timezone without daylight-saving transitions when the
    /// deployment stores timezone-free timestamps, because a DST gap or overlap would make converted
    /// wall-clock values ambiguous or repeated.
    /// </para>
    /// <para>
    /// Infrastructure bookkeeping — outbox capture, inbox receipts, delivery leases, and retry
    /// schedules — always stays UTC and is not affected by this option.
    /// </para>
    /// </remarks>
    public CommonTimeZone? LocalTimeZone { get; set; }
}
