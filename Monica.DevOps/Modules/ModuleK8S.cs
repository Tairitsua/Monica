using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Monica.Core;
using Monica.Core.Modularity;
using Monica.Core.Modularity.Abstractions;
using Monica.Core.Modularity.Models;
using Monica.Core.Results;
using Monica.DevOps.K8S.Abstractions;
using Monica.DevOps.K8S.Facades;
using Monica.DevOps.K8S.Models;
using Monica.DevOps.K8S.Providers.InCluster;
using Monica.DevOps.K8S.Providers.SshRemoteKubectl;
using Monica.DevOps.K8S.Services;
using Monica.DevOps.K8S.Services.Support;
using Monica.DevOps.Localization;

// ReSharper disable once CheckNamespace
namespace Monica.Modules;

public class ModuleK8S : MonicaModule<ModuleK8SOption>, IWebModule
{
    public override void Describe(ModuleDescriptor module)
    {
        module.Require<ModuleLocalization>();
    }

    public override void ConfigureServices(ModuleContext<ModuleK8SOption> context)
    {
        var services = context.Services;
        services.AddSingleton<IK8SRuntimeConfigStore, K8SRuntimeConfigStore>();
        services.AddScoped<IK8SProvider, SshRemoteKubectlProvider>();
        services.AddScoped<K8SMessageLocalizer>();
        services.AddScoped<K8SResourceDiscoveryService>();
        services.AddScoped<K8SClusterService>();
        services.AddScoped<K8SRestartService>();
        services.AddScoped<K8SScaleService>();
        services.AddScoped<K8SFacade>();

        if (Option.NodePortAddresses.Enabled)
        {
            services.AddSingleton<IK8SInClusterServiceReader, K8SInClusterServiceReader>();
            services.AddSingleton<K8SNodePortAddressResolver>();
            services.AddHostedService<K8SNodePortAddressInitializer>();
        }
    }

    public override void ConfigureEndpoints(WebModuleContext<ModuleK8SOption> context)
    {
        var app = context.ApplicationBuilder;
        UseEndpoints(context, endpoints =>
        {
            var tagName = Option.GetApiGroupName();

            endpoints.MapGet("/k8s/runtime-config",
                    async ([FromServices] K8SFacade facade, CancellationToken cancellationToken) =>
                        (await facade.GetRuntimeConfigAsync(cancellationToken)).GetResponse())
                .WithName("GetK8SRuntimeConfig")
                .WithTags(tagName)
                .WithSummary("Gets the current K8S runtime configuration")
                .WithDescription("Returns the current K8S runtime configuration used by the mixed Monica K8S module.");

            endpoints.MapPut("/k8s/runtime-config",
                    async ([FromBody] K8SRuntimeConfig runtimeConfig,
                        [FromServices] K8SFacade facade,
                        CancellationToken cancellationToken) =>
                        (await facade.SaveRuntimeConfigAsync(runtimeConfig, cancellationToken)).GetResponse())
                .WithName("UpdateK8SRuntimeConfig")
                .WithTags(tagName)
                .WithSummary("Updates the K8S runtime configuration")
                .WithDescription("Updates the runtime K8S access settings including node addresses, execution node, credentials, and namespace scope.");

            endpoints.MapGet("/k8s/namespaces",
                    async ([FromServices] K8SFacade facade, CancellationToken cancellationToken) =>
                        (await facade.ListNamespacesAsync(cancellationToken)).GetResponse())
                .WithName("ListK8SNamespaces")
                .WithTags(tagName)
                .WithSummary("Lists available namespaces")
                .WithDescription("Lists namespaces visible to the current K8S provider and applies the configured namespace scope.");

            endpoints.MapGet("/k8s/namespaces/{namespaceName}/services",
                    async ([FromRoute] string namespaceName,
                        [FromServices] K8SFacade facade,
                        CancellationToken cancellationToken) =>
                        (await facade.ListServicesAsync(namespaceName, cancellationToken)).GetResponse())
                .WithName("ListK8SServices")
                .WithTags(tagName)
                .WithSummary("Lists services in a namespace")
                .WithDescription("Discovers services in the target namespace through remote kubectl execution.");

            endpoints.MapGet("/k8s/namespaces/{namespaceName}/resources/{resourceType}",
                    async ([FromRoute] string namespaceName,
                        [FromRoute] string resourceType,
                        [FromServices] K8SFacade facade,
                        CancellationToken cancellationToken) =>
                        (await facade.ListResourcesAsync(namespaceName, resourceType, cancellationToken)).GetResponse())
                .WithName("ListK8SResources")
                .WithTags(tagName)
                .WithSummary("Lists K8S resources by type")
                .WithDescription("Lists services, deployments, statefulsets, or daemonsets in the selected namespace.");

            endpoints.MapGet("/k8s/namespaces/{namespaceName}/services/{serviceName}",
                    async ([FromRoute] string namespaceName,
                        [FromRoute] string serviceName,
                        [FromServices] K8SFacade facade,
                        CancellationToken cancellationToken) =>
                        (await facade.DescribeServiceAsync(namespaceName, serviceName, cancellationToken)).GetResponse())
                .WithName("DescribeK8SService")
                .WithTags(tagName)
                .WithSummary("Gets service details")
                .WithDescription("Returns service summary information, kubectl describe output, and resolved backing workloads for the target service.");

            endpoints.MapGet("/k8s/namespaces/{namespaceName}/resources/{resourceType}/{resourceName}",
                    async ([FromRoute] string namespaceName,
                        [FromRoute] string resourceType,
                        [FromRoute] string resourceName,
                        [FromServices] K8SFacade facade,
                        CancellationToken cancellationToken) =>
                        (await facade.DescribeResourceAsync(namespaceName, resourceType, resourceName, cancellationToken)).GetResponse())
                .WithName("DescribeK8SResource")
                .WithTags(tagName)
                .WithSummary("Gets resource details")
                .WithDescription("Returns summary and describe output for a service or rollout-capable workload resource.");

            endpoints.MapGet("/k8s/namespaces/{namespaceName}/pods/{podName}",
                    async ([FromRoute] string namespaceName,
                        [FromRoute] string podName,
                        [FromServices] K8SFacade facade,
                        CancellationToken cancellationToken) =>
                        (await facade.DescribePodAsync(namespaceName, podName, cancellationToken)).GetResponse())
                .WithName("DescribeK8SPod")
                .WithTags(tagName)
                .WithSummary("Gets pod details")
                .WithDescription("Returns pod summary information and kubectl describe output for the target pod.");

            endpoints.MapGet("/k8s/namespaces/{namespaceName}/services/{serviceName}/restart-preview",
                    async ([FromRoute] string namespaceName,
                        [FromRoute] string serviceName,
                        [FromServices] K8SFacade facade,
                        CancellationToken cancellationToken) =>
                        (await facade.GetServiceRestartPreviewAsync(namespaceName, serviceName, cancellationToken)).GetResponse())
                .WithName("PreviewK8SServiceRestart")
                .WithTags(tagName)
                .WithSummary("Builds a service restart preview")
                .WithDescription("Shows which workloads would be restarted for the selected service and which kubectl commands would be executed.");

            endpoints.MapPost("/k8s/namespaces/{namespaceName}/resources/{resourceType}/restart-preview",
                    async ([FromRoute] string namespaceName,
                        [FromRoute] string resourceType,
                        [FromBody] K8SResourceSelectionRequest request,
                        [FromServices] K8SFacade facade,
                        CancellationToken cancellationToken) =>
                        (await facade.GetBatchRestartPreviewAsync(namespaceName, resourceType, request.ResourceNames, cancellationToken)).GetResponse())
                .WithName("PreviewK8SResourceRestartBatch")
                .WithTags(tagName)
                .WithSummary("Builds a batch restart preview")
                .WithDescription("Builds a restart preview for selected services or workload resources.");

            endpoints.MapGet("/k8s/namespaces/{namespaceName}/restart-preview",
                    async ([FromRoute] string namespaceName,
                        [FromServices] K8SFacade facade,
                        CancellationToken cancellationToken) =>
                        (await facade.GetNamespaceRestartPreviewAsync(namespaceName, cancellationToken)).GetResponse())
                .WithName("PreviewK8SNamespaceRestart")
                .WithTags(tagName)
                .WithSummary("Builds a namespace restart preview")
                .WithDescription("Shows which workloads would be restarted across services in the selected namespace.");

            endpoints.MapPost("/k8s/namespaces/{namespaceName}/resources/{resourceType}/scale-down-preview",
                    async ([FromRoute] string namespaceName,
                        [FromRoute] string resourceType,
                        [FromBody] K8SResourceSelectionRequest request,
                        [FromServices] K8SFacade facade,
                        CancellationToken cancellationToken) =>
                        (await facade.GetBatchScalePreviewAsync(namespaceName, resourceType, request.ResourceNames, K8SScaleOperation.ScaleDown, cancellationToken)).GetResponse())
                .WithName("PreviewK8SResourceScaleDownBatch")
                .WithTags(tagName)
                .WithSummary("Builds a batch scale-down preview")
                .WithDescription("Builds a scale-down preview for selected services or workload resources.");

            endpoints.MapPost("/k8s/namespaces/{namespaceName}/resources/{resourceType}/scale-up-preview",
                    async ([FromRoute] string namespaceName,
                        [FromRoute] string resourceType,
                        [FromBody] K8SResourceSelectionRequest request,
                        [FromServices] K8SFacade facade,
                        CancellationToken cancellationToken) =>
                        (await facade.GetBatchScalePreviewAsync(namespaceName, resourceType, request.ResourceNames, K8SScaleOperation.ScaleUp, cancellationToken)).GetResponse())
                .WithName("PreviewK8SResourceScaleUpBatch")
                .WithTags(tagName)
                .WithSummary("Builds a batch scale-up preview")
                .WithDescription("Builds a scale-up preview for selected services or workload resources.");

            endpoints.MapGet("/k8s/namespaces/{namespaceName}/scale-down-preview",
                    async ([FromRoute] string namespaceName,
                        [FromServices] K8SFacade facade,
                        CancellationToken cancellationToken) =>
                        (await facade.GetNamespaceScalePreviewAsync(namespaceName, K8SScaleOperation.ScaleDown, cancellationToken)).GetResponse())
                .WithName("PreviewK8SNamespaceScaleDown")
                .WithTags(tagName)
                .WithSummary("Builds a namespace scale-down preview")
                .WithDescription("Builds a scale-down preview for service-backed workloads in the selected namespace.");

            endpoints.MapGet("/k8s/namespaces/{namespaceName}/scale-up-preview",
                    async ([FromRoute] string namespaceName,
                        [FromServices] K8SFacade facade,
                        CancellationToken cancellationToken) =>
                        (await facade.GetNamespaceScalePreviewAsync(namespaceName, K8SScaleOperation.ScaleUp, cancellationToken)).GetResponse())
                .WithName("PreviewK8SNamespaceScaleUp")
                .WithTags(tagName)
                .WithSummary("Builds a namespace scale-up preview")
                .WithDescription("Builds a scale-up preview for service-backed workloads in the selected namespace.");

            endpoints.MapPost("/k8s/namespaces/{namespaceName}/services/{serviceName}/restart",
                    async ([FromRoute] string namespaceName,
                        [FromRoute] string serviceName,
                        [FromServices] K8SFacade facade,
                        CancellationToken cancellationToken) =>
                        (await facade.RestartServiceAsync(namespaceName, serviceName, cancellationToken)).GetResponse())
                .WithName("RestartK8SService")
                .WithTags(tagName)
                .WithSummary("Restarts one service")
                .WithDescription("Restarts the workloads resolved for one service in the selected namespace.");

            endpoints.MapPost("/k8s/namespaces/{namespaceName}/resources/{resourceType}/restart",
                    async ([FromRoute] string namespaceName,
                        [FromRoute] string resourceType,
                        [FromBody] K8SResourceSelectionRequest request,
                        [FromServices] K8SFacade facade,
                        CancellationToken cancellationToken) =>
                        (await facade.RestartResourcesAsync(namespaceName, resourceType, request.ResourceNames, cancellationToken)).GetResponse())
                .WithName("RestartK8SResourceBatch")
                .WithTags(tagName)
                .WithSummary("Restarts selected resources")
                .WithDescription("Restarts rollout-capable workloads or services selected from the current resource list.");

            endpoints.MapPost("/k8s/namespaces/{namespaceName}/restart",
                    async ([FromRoute] string namespaceName,
                        [FromServices] K8SFacade facade,
                        CancellationToken cancellationToken) =>
                        (await facade.RestartNamespaceAsync(namespaceName, cancellationToken)).GetResponse())
                .WithName("RestartK8SNamespace")
                .WithTags(tagName)
                .WithSummary("Restarts services in a namespace")
                .WithDescription("Restarts every restartable workload resolved from services inside the selected namespace.");

            endpoints.MapPost("/k8s/namespaces/{namespaceName}/resources/{resourceType}/scale-down",
                    async ([FromRoute] string namespaceName,
                        [FromRoute] string resourceType,
                        [FromBody] K8SResourceSelectionRequest request,
                        [FromServices] K8SFacade facade,
                        CancellationToken cancellationToken) =>
                        (await facade.ScaleResourcesAsync(namespaceName, resourceType, request.ResourceNames, K8SScaleOperation.ScaleDown, cancellationToken)).GetResponse())
                .WithName("ScaleDownK8SResourceBatch")
                .WithTags(tagName)
                .WithSummary("Scales down selected resources")
                .WithDescription("Records previous replica counts and scales selected service-backed or workload resources down to zero replicas.");

            endpoints.MapPost("/k8s/namespaces/{namespaceName}/resources/{resourceType}/scale-up",
                    async ([FromRoute] string namespaceName,
                        [FromRoute] string resourceType,
                        [FromBody] K8SResourceSelectionRequest request,
                        [FromServices] K8SFacade facade,
                        CancellationToken cancellationToken) =>
                        (await facade.ScaleResourcesAsync(namespaceName, resourceType, request.ResourceNames, K8SScaleOperation.ScaleUp, cancellationToken)).GetResponse())
                .WithName("ScaleUpK8SResourceBatch")
                .WithTags(tagName)
                .WithSummary("Scales up selected resources")
                .WithDescription("Restores selected service-backed or workload resources to their recorded replica counts, falling back to one replica when no record exists.");

            endpoints.MapPost("/k8s/namespaces/{namespaceName}/scale-down",
                    async ([FromRoute] string namespaceName,
                        [FromServices] K8SFacade facade,
                        CancellationToken cancellationToken) =>
                        (await facade.ScaleNamespaceAsync(namespaceName, K8SScaleOperation.ScaleDown, cancellationToken)).GetResponse())
                .WithName("ScaleDownK8SNamespace")
                .WithTags(tagName)
                .WithSummary("Scales down services in a namespace")
                .WithDescription("Records previous replica counts and scales every service-backed scalable workload in the selected namespace down to zero replicas.");

            endpoints.MapPost("/k8s/namespaces/{namespaceName}/scale-up",
                    async ([FromRoute] string namespaceName,
                        [FromServices] K8SFacade facade,
                        CancellationToken cancellationToken) =>
                        (await facade.ScaleNamespaceAsync(namespaceName, K8SScaleOperation.ScaleUp, cancellationToken)).GetResponse())
                .WithName("ScaleUpK8SNamespace")
                .WithTags(tagName)
                .WithSummary("Scales up services in a namespace")
                .WithDescription("Restores every service-backed scalable workload in the selected namespace to its recorded replica count, falling back to one replica when no record exists.");
        });
    }
}

public static class ModuleK8SBuilderExtensions
{
    extension(IMonicaBuilder builder)
    {
        public ModuleRegistration<ModuleK8S, ModuleK8SOption> AddK8S(Action<ModuleK8SOption>? action = null)
        {
            var module = builder.AddModule<ModuleK8S, ModuleK8SOption>(action);
            module.Require<ModuleLocalization, ModuleLocalizationOption>()
                .AddResource<K8SResource>();
            return module;
        }
    }
}



public class ModuleK8SOption : MinimalApiModuleOptions<ModuleK8S>
{
    /// <summary>
    /// Gets or sets optional in-cluster NodePort address publication. Disabled by default; when enabled,
    /// one attempt runs in the background without delaying host startup. Addresses published after initial
    /// registration are collected by service discovery on a subsequent heartbeat when configured as metadata.
    /// </summary>
    public K8SNodePortAddressOptions NodePortAddresses { get; set; } = new();

    public K8SRuntimeConfig RuntimeConfig { get; set; } = new();

    public int SshPort { get; set; } = 22;

    public int CommandTimeoutSeconds { get; set; } = 30;

    public string KubectlCommand { get; set; } = "kubectl";
}
