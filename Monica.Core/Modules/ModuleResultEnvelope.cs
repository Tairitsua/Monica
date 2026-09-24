using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using System.Text.Encodings.Web;
using Monica.Core;
using Monica.Core.Modularity;
using Monica.Core.Modularity.Abstractions;
using Monica.Core.Results;
using Monica.Core.Results.Abstractions;
using Monica.Core.Results.Services;

// ReSharper disable once CheckNamespace
namespace Monica.Modules;

public static class ModuleResultEnvelopeBuilderExtensions
{
    extension(IMonicaBuilder builder)
    {
        /// <summary>
        /// Configures the ResultEnvelope module.
        /// </summary>
        public ModuleRegistration<ModuleResultEnvelope, ModuleResultEnvelopeOption> AddResultEnvelope(
            Action<ModuleResultEnvelopeOption>? action = null)
        {
            return builder.AddModule<ModuleResultEnvelope, ModuleResultEnvelopeOption>(action);
        }
    }

    extension(ModuleRegistration<ModuleResultEnvelope, ModuleResultEnvelopeOption> registration)
    {
        /// <summary>
        /// Configures top-level JSON field names for Monica result envelopes.
        /// </summary>
        public ModuleRegistration<ModuleResultEnvelope, ModuleResultEnvelopeOption> UseResultFieldNames(
            Action<ResultEnvelopeFieldNames> configure)
        {
            ArgumentNullException.ThrowIfNull(configure);
            return registration.Configure(option => configure(option.FieldNames));
        }

    }
}

public class ModuleResultEnvelope : MonicaModule<ModuleResultEnvelopeOption>
{
    public override void Describe(ModuleDescriptor module)
    {
        module.Require<ModuleJsonSerialization, ModuleJsonSerializationOption>();
    }

    /// <inheritdoc />
    public override void DeclareContracts(ModuleContractDescriptor<ModuleResultEnvelopeOption> contracts)
    {
        contracts.Modules.Get<ModuleJsonSerialization, ModuleJsonSerializationOption>()
            .WireContract
            .ConfigureResultEnvelope(contracts.Options.FieldNames);
        if (contracts.Options.ExposeDiagnosticDetails)
        {
            contracts.Modules.Get<ModuleJsonSerialization, ModuleJsonSerializationOption>()
                .WireContract.Configure(options => options.Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping);
        }
    }

    public override void ConfigureServices(ModuleContext<ModuleResultEnvelopeOption> context)
    {
        var services = context.Services;
        services.TryAddSingleton<IResultErrorMessageProvider, DefaultResultErrorMessageProvider>();
    }
}

public class ModuleResultEnvelopeOption : ModuleOptions<ModuleResultEnvelope>
{
    /// <summary>
    /// Gets the top-level JSON field names used for Monica result envelopes.
    /// </summary>
    public ResultEnvelopeFieldNames FieldNames { get; set; } = new();

    /// <summary>
    /// Gets or sets the single diagnostic switch for this host. The default is <see langword="false"/>.
    /// When enabled, unhandled exceptions carry a bounded <c>metadata.diagnostics</c> exception catalog,
    /// chain nodes refer to catalog entries through <c>exceptionId</c>, and reserved diagnostic
    /// members (<c>diagnostics</c>, legacy <c>exception</c>, <c>detail</c>, <c>chain</c>, <c>chain_error</c>) are retained in HTTP responses,
    /// the remote-call boundary forwards a downstream's reserved details instead of stripping them, and the
    /// chain-tracing filter attaches the call chain (including recorded SQL commands) to result envelopes.
    /// Diagnostic hosts use readable UTF-8 JSON escaping, including Unicode and CLR stack punctuation;
    /// consume these JSON responses as JSON rather than embedding their raw text in HTML or script.
    /// Operator logs always contain full exception objects regardless of this switch; enable it only on hosts
    /// whose consumers may see SQL text, parameter values, and stack traces.
    /// </summary>
    public bool ExposeDiagnosticDetails { get; set; }

}
