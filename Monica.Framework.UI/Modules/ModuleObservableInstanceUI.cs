using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Monica.Core;
using Monica.Core.Modularity;
using Monica.Core.Modularity.Abstractions;
using Monica.Core.Modularity.Models;
using Monica.Core.Results;
using Monica.Framework.UI.Localization;
using Monica.Framework.UI.Pages;
using Monica.UI.Shell.Models;
using Monica.Framework.UI.UIObservableInstance.Support;
using Monica.UI.Icons;
using MudBlazor;

// ReSharper disable once CheckNamespace
namespace Monica.Modules;

public static class ModuleObservableInstanceUIBuilderExtensions
{
    extension(IMonicaBuilder builder)
    {
        /// <summary>
        /// Configure the ObservableInstanceUI module
        /// </summary>
        public ModuleRegistration<ModuleObservableInstanceUI, ModuleObservableInstanceUIOption> AddObservableInstanceUI(
            Action<ModuleObservableInstanceUIOption>? action = null)
        {
            return builder.AddModule<ModuleObservableInstanceUI, ModuleObservableInstanceUIOption>(action);
        }
    }
}

/// <summary>
/// ObservableInstance UI module
/// </summary>
public class ModuleObservableInstanceUI : MonicaModule<ModuleObservableInstanceUIOption>, IWebHostRequiredModule, IUIModule
{
    public override void Describe(ModuleDescriptor module)
    {
        module.Require<ModuleObservableInstance, ModuleObservableInstanceOption>();
        // Exception panes render StackTraceViewer, which needs the parser registered by the stack trace UI module.
        module.Require<ModuleStackTraceUI, ModuleStackTraceUIOption>();
        module.Require<ModuleLocalization, ModuleLocalizationOption>(
            static option => option.AddResource<ObservableInstanceResource>());
        module.Require<ModuleShellUI, ModuleShellUIOption>(static option =>
            option.ConfigureNavigation(static registry =>
            {
                var category = registry.RegisterLocalizedCategory<ObservableInstanceResource>(
                    "Monica.Framework.UI.ObservableInstances", "Navigation:Category", 10,
                    parentId: BuiltInNavigationCategoryIds.Debug,
                    icon: MoIcons.Pulse);

                registry.RegisterLocalizedPage<UIObservableInstanceMonitorPage, ObservableInstanceResource>(
                    UIObservableInstanceMonitorPage.PAGE_URL,
                    "Pages:ObservableInstance:Title",
                    MoIcons.Package,
                    category,
                    addToNav: true,
                    navOrder: 50);
                registry.RegisterLocalizedPage<UIObservableInstanceRegistryPage, ObservableInstanceResource>(
                    UIObservableInstanceRegistryPage.PAGE_URL, "Workbench:InstancesTitle", MoIcons.Package,
                    category, addToNav: true, navOrder: 51);
                registry.RegisterLocalizedPage<UIObservableInstanceGroupsPage, ObservableInstanceResource>(
                    UIObservableInstanceGroupsPage.PAGE_URL, "Workbench:GroupsTitle", MoIcons.Workspace,
                    category, addToNav: true, navOrder: 52);
                registry.RegisterLocalizedPage<UIObservableInstanceExceptionsPage, ObservableInstanceResource>(
                    UIObservableInstanceExceptionsPage.PAGE_URL, "Workbench:ExceptionsTitle", MoIcons.Error,
                    category, addToNav: true, navOrder: 53);
            }));
    }

    public override void ConfigureServices(ModuleContext<ModuleObservableInstanceUIOption> context)
    {
        // The query service is stateless; the Blazor circuit owns workspace evidence and filters.
        context.Services.AddSingleton<ObservableInstanceMonitorService>();
        context.Services.AddScoped<ObservableWorkspaceState>();
    }

    public override void ConfigureEndpoints(WebModuleContext<ModuleObservableInstanceUIOption> context)
    {
        UseEndpoints(context, endpoints =>
        {
            var tagName = Option.GetApiGroupName();

            // Get all instances
            endpoints.MapGet("/observable-instance-ui/instances",
                async ([FromServices] ObservableInstanceMonitorService service) =>
                {
                    var result = await service.GetAllInstancesAsync();
                    return result.GetResponse();
                })
                .WithName("获取所有Observable实例")
                .WithTags(tagName)
                .WithSummary("获取所有Observable实例")
                .WithDescription("获取所有已注册的Observable Instance信息");

            // Get instance by ID
            endpoints.MapGet("/observable-instance-ui/instances/{id}",
                async ([FromRoute] string id,
                       [FromServices] ObservableInstanceMonitorService service) =>
                {
                    var result = await service.GetInstanceByIdAsync(id);
                    return result.GetResponse();
                })
                .WithName("获取Observable实例详情")
                .WithTags(tagName)
                .WithSummary("获取Observable实例详情")
                .WithDescription("根据实例ID获取详细信息");

            // Get instances with exceptions
            endpoints.MapGet("/observable-instance-ui/instances/with-exceptions",
                async ([FromServices] ObservableInstanceMonitorService service) =>
                {
                    var result = await service.GetInstancesWithExceptionsAsync();
                    return result.GetResponse();
                })
                .WithName("获取有异常的实例")
                .WithTags(tagName)
                .WithSummary("获取有异常的实例")
                .WithDescription("获取所有包含异常记录的实例");

            // Get statistics
            endpoints.MapGet("/observable-instance-ui/statistics",
                async ([FromServices] ObservableInstanceMonitorService service) =>
                {
                    var result = await service.GetStatisticsAsync();
                    return result.GetResponse();
                })
                .WithName("获取Observable实例统计")
                .WithTags(tagName)
                .WithSummary("获取Observable实例统计")
                .WithDescription("获取实例的统计信息，包括总数、异常分布等");

            // Get instance history
            endpoints.MapGet("/observable-instance-ui/instances/{id}/history",
                async ([FromRoute] string id,
                       [FromQuery] int? limit,
                       [FromServices] ObservableInstanceMonitorService service) =>
                {
                    var result = await service.GetInstanceHistoryAsync(id, limit);
                    return result.GetResponse();
                })
                .WithName("获取实例历史")
                .WithTags(tagName)
                .WithSummary("获取实例历史")
                .WithDescription("获取实例的状态变更历史");

            // Get instance exceptions
            endpoints.MapGet("/observable-instance-ui/instances/{id}/exceptions",
                async ([FromRoute] string id,
                       [FromServices] ObservableInstanceMonitorService service) =>
                {
                    var result = await service.GetInstanceExceptionsAsync(id);
                    return result.GetResponse();
                })
                .WithName("获取实例异常")
                .WithTags(tagName)
                .WithSummary("获取实例异常")
                .WithDescription("获取实例的所有异常记录");

            // Clear instance history
            endpoints.MapPost("/observable-instance-ui/instances/{id}/clear-history",
                async ([FromRoute] string id,
                       [FromServices] ObservableInstanceMonitorService service) =>
                {
                    var result = await service.ClearInstanceHistoryAsync(id);
                    return result.GetResponse();
                })
                .WithName("清除实例历史")
                .WithTags(tagName)
                .WithSummary("清除实例历史")
                .WithDescription("清除指定实例的状态历史记录");
        });
    }
}

/// <summary>
/// ObservableInstanceUI module options
/// </summary>
public class ModuleObservableInstanceUIOption : MinimalApiModuleOptions<ModuleObservableInstanceUI>
{
}
