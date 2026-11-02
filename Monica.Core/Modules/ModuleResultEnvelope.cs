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
        if (Application.Application.DebugMode)
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

}
