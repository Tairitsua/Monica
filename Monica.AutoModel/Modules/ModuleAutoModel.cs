using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Monica.AutoModel.Abstractions;
using Monica.AutoModel.Providers;
using Monica.AutoModel.Services;
using Monica.Core;
using Monica.Core.Modularity;
using Monica.Core.Modularity.Abstractions;
using Monica.Tool.Extensions;
using TokenExpressionGenDynamicLinqProvider = Monica.AutoModel.Providers.TokenExpressionGenDynamicLinqProvider;

// ReSharper disable once CheckNamespace
namespace Monica.Modules;

public static class ModuleAutoModelBuilderExtensions
{
    extension(IMonicaBuilder builder)
    {
        /// <summary>
        /// Adds and configures the AutoModel module.
        /// </summary>
        public ModuleRegistration<ModuleAutoModel, ModuleAutoModelOption> AddAutoModel(
            Action<ModuleAutoModelOption>? action = null)
        {
            return builder.AddModule<ModuleAutoModel, ModuleAutoModelOption>(action);
        }
    }
}

public class ModuleAutoModel : MonicaModule<ModuleAutoModelOption>, IWebModule
{
    public override void ConfigureServices(ModuleContext<ModuleAutoModelOption> context)
    {
        var services = context.Services;
        services.AddSingleton<SnapshotFactoryMemoryProvider>();
        services.AddSingleton<IAutoModelSnapshotFactory>(provider =>
            provider.GetRequiredService<SnapshotFactoryMemoryProvider>());
        services.AddSingleton(typeof(IAutoModelSnapshot<>), typeof(SnapshotMemoryProvider<>));
        services.AddTransient(typeof(IAutoModelExpressionNormalizer<>), typeof(ExpressionNormalizerDynamicLinqProvider<>));
        services.AddTransient(typeof(IAutoModelDbOperator<>), typeof(DbOperatorDynamicLinqProvider<>));
        services.AddTransient(typeof(IAutoModelMemoryOperator<>), typeof(MemoryOperatorDynamicLinqProvider<>));
        services.AddTransient(typeof(IAutoModelExpressionTokenizer<>),
            typeof(AutoModel.Services.ExpressionTokenizer<>));
        services.AddTransient<IAutoModelTokenExpressionGen, TokenExpressionGenDynamicLinqProvider>();
        services.AddTransient<IAutoModelTypeConverter, TypeConverter>();
    }

    public override void ConfigureEndpoints(WebModuleContext<ModuleAutoModelOption> context)
    {
        UseEndpoints(context, endpoints =>
        {
            var tagName = Option.GetApiGroupName();

            endpoints.MapGet("/auto-model/status", async (
                HttpResponse response,
                [FromServices] IAutoModelSnapshotFactory factory,
                [FromQuery] string? specificEntity = null) =>
            {
                var snapshots = factory.GetSnapshots().WhereIf(specificEntity != null,
                    p => p.Table.Name?.Equals(specificEntity?.Trim(), StringComparison.OrdinalIgnoreCase) == true).ToList();
                var res = new
                {
                    count = snapshots.Count,
                    entities = snapshots.Select(p => p.Table.Name),
                    snapshots = snapshots.Select(x => new
                    {
                        x.Table,
                        x.Fields
                    })
                };
                await response.WriteAsJsonAsync(res);
            })
            .WithName("GetAutoModelStatus")
            .WithTags(tagName)
            .WithSummary("Gets AutoModel status")
            .WithDescription("Returns the model snapshots owned by this Monica host.");
        });
    }

}

/// <summary>
/// Configures AutoModel discovery and expression behavior for one Monica host.
/// </summary>
public class ModuleAutoModelOption : MinimalApiModuleOptions<ModuleAutoModel>
{
    /// <summary>
    /// Enables global active mode. Only fields marked with <c>AutoField</c> participate in AutoModel.
    /// </summary>
    public bool EnableActiveMode { get; set; }

    /// <summary>
    /// Allows default activation names to omit prefixes.
    /// </summary>
    public bool EnableIgnorePrefix { get; set; }

    /// <summary>
    /// When prefix omission is enabled for default activation names, activation-name auto-adjustment failures do not throw exceptions.
    /// </summary>
    public bool EnableIgnorePrefixAutoAdjust { get; set; }

    /// <summary>
    /// Enables using the field display name as an activation name.
    /// </summary>
    [Obsolete("Not implemented yet.")]
    public bool EnableTitleAsActivateName { get; set; }

    /// <summary>
    /// Includes properties marked with <c>JsonIgnoreAttribute</c> unless another AutoModel rule excludes them.
    /// </summary>
    public bool DisableAutoIgnorePropertyWithJsonIgnoreAttribute { get; set; }

    /// <summary>
    /// Includes properties marked with <c>NotMappedAttribute</c> unless another AutoModel rule excludes them.
    /// </summary>
    public bool DisableAutoIgnorePropertyWithNotMappedAttribute { get; set; }

    /// <summary>
    /// Throws when a model contains unsupported field types.
    /// </summary>
    public bool EnableErrorForUnsupportedFieldTypes { get; set; }

}
