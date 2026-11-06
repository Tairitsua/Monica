using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Monica.ModelQuery.Abstractions;
using Monica.ModelQuery.Providers;
using Monica.ModelQuery.Services;
using Monica.Core;
using Monica.Core.Modularity;
using Monica.Core.Modularity.Abstractions;
using Monica.Tool.Extensions;
using ExpressionGeneratorDynamicLinqProvider = Monica.ModelQuery.Providers.ExpressionGeneratorDynamicLinqProvider;

// ReSharper disable once CheckNamespace
namespace Monica.Modules;

public static class ModuleModelQueryBuilderExtensions
{
    extension(IMonicaBuilder builder)
    {
        /// <summary>
        /// Registers model query metadata, filtering, searching, and field projection services.
        /// The optional status endpoint follows the common minimal-API module settings.
        /// </summary>
        public ModuleRegistration<ModuleModelQuery, ModuleModelQueryOption> AddModelQuery(
            Action<ModuleModelQueryOption>? action = null)
        {
            return builder.AddModule<ModuleModelQuery, ModuleModelQueryOption>(action);
        }
    }
}

/// <summary>
/// Registers host-owned model query snapshots, transient query services, and an optional status endpoint.
/// </summary>
public class ModuleModelQuery : MonicaModule<ModuleModelQueryOption>, IWebModule
{
    public override void ConfigureServices(ModuleContext<ModuleModelQueryOption> context)
    {
        var services = context.Services;
        services.AddSingleton<SnapshotFactoryMemoryProvider>();
        services.AddSingleton<IModelQuerySnapshotFactory>(provider =>
            provider.GetRequiredService<SnapshotFactoryMemoryProvider>());
        services.AddSingleton(typeof(IModelQuerySnapshot<>), typeof(SnapshotMemoryProvider<>));
        services.AddTransient(typeof(IModelQuery<>), typeof(ModelQueryProvider<>));
        services.AddTransient(typeof(IModelQueryExpressionTokenizer<>),
            typeof(ModelQuery.Services.ExpressionTokenizer<>));
        services.AddTransient<IModelQueryExpressionGenerator, ExpressionGeneratorDynamicLinqProvider>();
        services.AddTransient<IModelQueryValueConverter, TypeConverter>();
    }

    public override void ConfigureEndpoints(WebModuleContext<ModuleModelQueryOption> context)
    {
        UseEndpoints(context, endpoints =>
        {
            var tagName = Option.GetApiGroupName();

            endpoints.MapGet("/model-query/status", async (
                HttpResponse response,
                [FromServices] IModelQuerySnapshotFactory factory,
                [FromQuery] string? modelName = null) =>
            {
                var snapshots = factory.GetSnapshots().WhereIf(modelName != null,
                    p => p.Model.Name?.Equals(modelName?.Trim(), StringComparison.OrdinalIgnoreCase) == true).ToList();
                var res = new
                {
                    count = snapshots.Count,
                    models = snapshots.Select(p => p.Model.Name),
                    snapshots = snapshots.Select(x => new
                    {
                        x.Model,
                        x.Fields
                    })
                };
                await response.WriteAsJsonAsync(res);
            })
            .WithName("GetModelQueryStatus")
            .WithTags(tagName)
            .WithSummary("Gets ModelQuery status")
            .WithDescription("Returns the model snapshots owned by this Monica host.");
        });
    }

}

/// <summary>
/// Configures ModelQuery discovery and expression behavior for one Monica host.
/// </summary>
public class ModuleModelQueryOption : MinimalApiModuleOptions<ModuleModelQuery>
{
    /// <summary>
    /// Requires <c>QueryField</c> on every included property, including navigation properties and nested fields.
    /// Defaults to <see langword="false"/>; a model annotation can override this setting.
    /// </summary>
    public bool RequireFieldAnnotation { get; set; }

    /// <summary>
    /// Omits navigation paths from fallback query names. Defaults to <see langword="false"/>.
    /// A model or field annotation can override this setting; nested identifiers retain their path.
    /// </summary>
    public bool OmitNavigationPrefix { get; set; }

    /// <summary>
    /// Restores the navigation path when an unqualified fallback query name conflicts with an earlier name,
    /// ignoring case. Defaults to <see langword="false"/>. Remaining duplicate names cause snapshot creation to fail.
    /// </summary>
    public bool AutoQualifyConflictingNames { get; set; }

    /// <summary>
    /// Includes properties marked with <c>JsonIgnoreAttribute</c> unless another rule excludes them.
    /// Defaults to <see langword="false"/>; <c>QueryField</c> also includes such a property unless excluded.
    /// </summary>
    public bool IncludeJsonIgnoredProperties { get; set; }

    /// <summary>
    /// Includes properties marked with <c>NotMappedAttribute</c> unless another rule excludes them.
    /// Defaults to <see langword="false"/>; <c>QueryField</c> also includes such a property unless excluded.
    /// </summary>
    public bool IncludeNotMappedProperties { get; set; }

    /// <summary>
    /// Throws when a model contains unsupported field types. Defaults to <see langword="false"/>, which skips them.
    /// </summary>
    public bool ThrowOnUnsupportedFieldType { get; set; }

}
