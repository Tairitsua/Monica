using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Logging;
using Monica.DevOps.K8S.Exceptions;
using Monica.DevOps.Localization;
using Monica.DevOps.K8S.Models;
using Monica.DevOps.K8S.Services;
using Monica.DevOps.K8S.Services.Support;
using Monica.Core.Results;

namespace Monica.DevOps.K8S.Facades;

public class K8SFacade(
    K8SClusterService clusterService,
    K8SRestartService restartService,
    K8SScaleService scaleService,
    K8SMessageLocalizer messageLocalizer,
    IStringLocalizer<K8SResource> localizer,
    ILogger<K8SFacade> logger)
{
    public Task<Res<K8SRuntimeConfig>> GetRuntimeConfigAsync(CancellationToken cancellationToken = default)
    {
        return ExecuteAsync(
            () => clusterService.GetRuntimeConfigAsync(cancellationToken),
            ex => localizer["ServiceMessages:LoadRuntimeConfigFailed", messageLocalizer.TranslateExceptionMessage(ex)].Value,
            "Failed to load K8S runtime configuration.");
    }

    public Task<Res> SaveRuntimeConfigAsync(K8SRuntimeConfig runtimeConfig, CancellationToken cancellationToken = default)
    {
        var normalizedConfig = runtimeConfig.Clone().Normalize();
        return ExecuteAsync(
            () => clusterService.UpdateRuntimeConfigAsync(normalizedConfig, cancellationToken),
            () => messageLocalizer.GetRuntimeConfigSavedMessage(normalizedConfig),
            ex => localizer["ServiceMessages:SaveRuntimeConfigFailed", messageLocalizer.TranslateExceptionMessage(ex)].Value,
            "Failed to save K8S runtime configuration.");
    }

    public Task<Res<List<K8SNamespaceInfo>>> ListNamespacesAsync(CancellationToken cancellationToken = default)
    {
        return ExecuteAsync(
            async () => (await clusterService.ListNamespacesAsync(cancellationToken)).ToList(),
            ex => localizer["ServiceMessages:ListNamespacesFailed", messageLocalizer.TranslateExceptionMessage(ex)].Value,
            "Failed to list K8S namespaces.");
    }

    public Task<Res<IReadOnlyList<K8SServiceSummary>>> ListServicesAsync(string namespaceName, CancellationToken cancellationToken = default)
    {
        return ExecuteAsync(
            () => clusterService.ListServicesAsync(namespaceName, cancellationToken),
            ex => messageLocalizer.TranslateExceptionMessage(ex),
            $"Failed to list K8S services for namespace {namespaceName}.");
    }

    public Task<Res<K8SResourceListResult>> ListResourcesAsync(
        string namespaceName,
        K8SResourceType resourceType,
        CancellationToken cancellationToken = default)
    {
        return ExecuteAsync(
            () => clusterService.ListResourcesAsync(namespaceName, resourceType, cancellationToken),
            ex => localizer["ServiceMessages:ListResourcesFailed", messageLocalizer.LocalizeResourceType(resourceType.ToDisplayName()), namespaceName, messageLocalizer.TranslateExceptionMessage(ex)].Value,
            $"Failed to list K8S resources of type {resourceType} for namespace {namespaceName}.");
    }

    public Task<Res<K8SResourceListResult>> ListResourcesAsync(
        string namespaceName,
        string resourceType,
        CancellationToken cancellationToken = default)
    {
        return TryMapResourceType(resourceType, parsedResourceType => ListResourcesAsync(namespaceName, parsedResourceType, cancellationToken));
    }

    /// <summary>
    /// Reads all supported kinds and their backing workload evidence using one runtime configuration snapshot.
    /// A failed read returns a failure result without a partial inventory.
    /// </summary>
    public Task<Res<K8SNamespaceInventoryResult>> ListNamespaceInventoryAsync(
        string namespaceName,
        CancellationToken cancellationToken = default)
    {
        return ExecuteAsync(
            () => clusterService.ListNamespaceInventoryAsync(namespaceName, cancellationToken),
            ex => messageLocalizer.TranslateExceptionMessage(ex),
            $"Failed to read K8S inventory for namespace {namespaceName}.");
    }

    public Task<Res<K8SServiceDetails>> DescribeServiceAsync(
        string namespaceName,
        string serviceName,
        CancellationToken cancellationToken = default)
    {
        return ExecuteAsync(
            () => clusterService.DescribeServiceAsync(namespaceName, serviceName, cancellationToken),
            ex => messageLocalizer.TranslateExceptionMessage(ex),
            $"Failed to describe K8S service {serviceName} in namespace {namespaceName}.");
    }

    public Task<Res<K8SResourceDetails>> DescribeResourceAsync(
        string namespaceName,
        K8SResourceType resourceType,
        string resourceName,
        CancellationToken cancellationToken = default)
    {
        return ExecuteAsync(
            () => clusterService.DescribeResourceAsync(namespaceName, resourceType, resourceName, cancellationToken),
            ex => localizer["ServiceMessages:DescribeResourceFailed", resourceName, namespaceName, messageLocalizer.TranslateExceptionMessage(ex)].Value,
            $"Failed to describe K8S resource {resourceType}/{resourceName} in namespace {namespaceName}.");
    }

    public Task<Res<K8SResourceDetails>> DescribeResourceAsync(
        string namespaceName,
        string resourceType,
        string resourceName,
        CancellationToken cancellationToken = default)
    {
        return TryMapResourceType(resourceType, parsedResourceType => DescribeResourceAsync(namespaceName, parsedResourceType, resourceName, cancellationToken));
    }

    public Task<Res<K8SPodDetails>> DescribePodAsync(
        string namespaceName,
        string podName,
        CancellationToken cancellationToken = default)
    {
        return ExecuteAsync(
            () => clusterService.DescribePodAsync(namespaceName, podName, cancellationToken),
            ex => localizer["ServiceMessages:DescribePodFailed", podName, namespaceName, messageLocalizer.TranslateExceptionMessage(ex)].Value,
            $"Failed to describe K8S pod {podName} in namespace {namespaceName}.");
    }

    public Task<Res<K8SRestartPreview>> GetServiceRestartPreviewAsync(
        string namespaceName,
        string serviceName,
        CancellationToken cancellationToken = default)
    {
        return ExecuteAsync(
            () => restartService.GetRestartPreviewAsync(namespaceName, serviceName, cancellationToken),
            ex => localizer["ServiceMessages:BuildRestartPreviewFailed", serviceName, namespaceName, messageLocalizer.TranslateExceptionMessage(ex)].Value,
            $"Failed to build restart preview for K8S service {serviceName} in namespace {namespaceName}.",
            messageLocalizer.LocalizeRestartPreview);
    }

    public Task<Res<K8SRestartPreview>> GetResourceRestartPreviewAsync(
        string namespaceName,
        K8SResourceType resourceType,
        string resourceName,
        CancellationToken cancellationToken = default)
    {
        return ExecuteAsync(
            () => restartService.GetResourceRestartPreviewAsync(namespaceName, resourceType, resourceName, cancellationToken),
            ex => localizer["ServiceMessages:BuildRestartPreviewFailed", resourceName, namespaceName, messageLocalizer.TranslateExceptionMessage(ex)].Value,
            $"Failed to build restart preview for K8S resource {resourceType}/{resourceName} in namespace {namespaceName}.",
            messageLocalizer.LocalizeRestartPreview);
    }

    public Task<Res<K8SRestartPreview>> GetResourceRestartPreviewAsync(
        string namespaceName,
        string resourceType,
        string resourceName,
        CancellationToken cancellationToken = default)
    {
        return TryMapResourceType(resourceType, parsedResourceType => GetResourceRestartPreviewAsync(namespaceName, parsedResourceType, resourceName, cancellationToken));
    }

    public Task<Res<K8SRestartPreview>> GetBatchRestartPreviewAsync(
        string namespaceName,
        K8SResourceType resourceType,
        IReadOnlyCollection<string> resourceNames,
        CancellationToken cancellationToken = default)
    {
        return ExecuteAsync(
            () => restartService.GetBatchRestartPreviewAsync(namespaceName, resourceType, resourceNames, cancellationToken),
            ex => localizer["ServiceMessages:BuildBatchRestartPreviewFailed", namespaceName, messageLocalizer.TranslateExceptionMessage(ex)].Value,
            $"Failed to build batch restart preview for K8S resource type {resourceType} in namespace {namespaceName}.",
            messageLocalizer.LocalizeRestartPreview);
    }

    public Task<Res<K8SRestartPreview>> GetBatchRestartPreviewAsync(
        string namespaceName,
        string resourceType,
        IReadOnlyCollection<string> resourceNames,
        CancellationToken cancellationToken = default)
    {
        return TryMapResourceType(resourceType, parsedResourceType => GetBatchRestartPreviewAsync(namespaceName, parsedResourceType, resourceNames, cancellationToken));
    }

    public Task<Res<K8SRestartPreview>> GetNamespaceRestartPreviewAsync(
        string namespaceName,
        CancellationToken cancellationToken = default)
    {
        return ExecuteAsync(
            () => restartService.GetNamespaceRestartPreviewAsync(namespaceName, cancellationToken),
            ex => localizer["ServiceMessages:BuildNamespaceRestartPreviewFailed", namespaceName, messageLocalizer.TranslateExceptionMessage(ex)].Value,
            $"Failed to build namespace restart preview for namespace {namespaceName}.",
            messageLocalizer.LocalizeRestartPreview);
    }

    public Task<Res<K8SRestartResult>> RestartServiceAsync(
        string namespaceName,
        string serviceName,
        CancellationToken cancellationToken = default)
    {
        return ExecuteAsync(
            () => restartService.RestartServiceAsync(namespaceName, serviceName, cancellationToken),
            ex => localizer["ServiceMessages:RestartResourceFailed", serviceName, namespaceName, messageLocalizer.TranslateExceptionMessage(ex)].Value,
            $"Failed to restart K8S service {serviceName} in namespace {namespaceName}.",
            messageLocalizer.LocalizeRestartResult);
    }

    public Task<Res<K8SRestartResult>> RestartResourceAsync(
        string namespaceName,
        K8SResourceType resourceType,
        string resourceName,
        CancellationToken cancellationToken = default)
    {
        return ExecuteAsync(
            () => restartService.RestartResourcesAsync(namespaceName, resourceType, [resourceName], cancellationToken),
            ex => localizer["ServiceMessages:RestartResourceFailed", resourceName, namespaceName, messageLocalizer.TranslateExceptionMessage(ex)].Value,
            $"Failed to restart K8S resource {resourceType}/{resourceName} in namespace {namespaceName}.",
            messageLocalizer.LocalizeRestartResult);
    }

    public Task<Res<K8SRestartResult>> RestartResourceAsync(
        string namespaceName,
        string resourceType,
        string resourceName,
        CancellationToken cancellationToken = default)
    {
        return TryMapResourceType(resourceType, parsedResourceType => RestartResourceAsync(namespaceName, parsedResourceType, resourceName, cancellationToken));
    }

    public Task<Res<K8SRestartResult>> RestartResourcesAsync(
        string namespaceName,
        K8SResourceType resourceType,
        IReadOnlyCollection<string> resourceNames,
        CancellationToken cancellationToken = default)
    {
        return ExecuteAsync(
            () => restartService.RestartResourcesAsync(namespaceName, resourceType, resourceNames, cancellationToken),
            ex => localizer["ServiceMessages:RestartResourcesFailed", namespaceName, messageLocalizer.TranslateExceptionMessage(ex)].Value,
            $"Failed to restart K8S resources of type {resourceType} in namespace {namespaceName}.",
            messageLocalizer.LocalizeRestartResult);
    }

    public Task<Res<K8SRestartResult>> RestartResourcesAsync(
        string namespaceName,
        string resourceType,
        IReadOnlyCollection<string> resourceNames,
        CancellationToken cancellationToken = default)
    {
        return TryMapResourceType(resourceType, parsedResourceType => RestartResourcesAsync(namespaceName, parsedResourceType, resourceNames, cancellationToken));
    }

    public Task<Res<K8SRestartResult>> RestartNamespaceAsync(
        string namespaceName,
        CancellationToken cancellationToken = default)
    {
        return ExecuteAsync(
            () => restartService.RestartNamespaceAsync(namespaceName, cancellationToken),
            ex => localizer["ServiceMessages:RestartNamespaceFailed", namespaceName, messageLocalizer.TranslateExceptionMessage(ex)].Value,
            $"Failed to restart all K8S services in namespace {namespaceName}.",
            messageLocalizer.LocalizeRestartResult);
    }

    public Task<Res<K8SScalePreview>> GetResourceScalePreviewAsync(
        string namespaceName,
        K8SResourceType resourceType,
        string resourceName,
        K8SScaleOperation operation,
        CancellationToken cancellationToken = default)
    {
        return ExecuteAsync(
            () => scaleService.GetResourceScalePreviewAsync(namespaceName, resourceType, resourceName, operation, cancellationToken),
            ex => localizer["ServiceMessages:BuildScalePreviewFailed", resourceName, namespaceName, messageLocalizer.TranslateExceptionMessage(ex)].Value,
            $"Failed to build {operation} preview for K8S resource {resourceType}/{resourceName} in namespace {namespaceName}.",
            messageLocalizer.LocalizeScalePreview);
    }

    public Task<Res<K8SScalePreview>> GetResourceScalePreviewAsync(
        string namespaceName,
        string resourceType,
        string resourceName,
        K8SScaleOperation operation,
        CancellationToken cancellationToken = default)
    {
        return TryMapResourceType(resourceType, parsedResourceType => GetResourceScalePreviewAsync(namespaceName, parsedResourceType, resourceName, operation, cancellationToken));
    }

    public Task<Res<K8SScalePreview>> GetBatchScalePreviewAsync(
        string namespaceName,
        K8SResourceType resourceType,
        IReadOnlyCollection<string> resourceNames,
        K8SScaleOperation operation,
        CancellationToken cancellationToken = default)
    {
        return ExecuteAsync(
            () => scaleService.GetBatchScalePreviewAsync(namespaceName, resourceType, resourceNames, operation, cancellationToken),
            ex => localizer["ServiceMessages:BuildBatchScalePreviewFailed", namespaceName, messageLocalizer.TranslateExceptionMessage(ex)].Value,
            $"Failed to build batch {operation} preview for K8S resource type {resourceType} in namespace {namespaceName}.",
            messageLocalizer.LocalizeScalePreview);
    }

    public Task<Res<K8SScalePreview>> GetBatchScalePreviewAsync(
        string namespaceName,
        string resourceType,
        IReadOnlyCollection<string> resourceNames,
        K8SScaleOperation operation,
        CancellationToken cancellationToken = default)
    {
        return TryMapResourceType(resourceType, parsedResourceType => GetBatchScalePreviewAsync(namespaceName, parsedResourceType, resourceNames, operation, cancellationToken));
    }

    public Task<Res<K8SScalePreview>> GetNamespaceScalePreviewAsync(
        string namespaceName,
        K8SScaleOperation operation,
        CancellationToken cancellationToken = default)
    {
        return ExecuteAsync(
            () => scaleService.GetNamespaceScalePreviewAsync(namespaceName, operation, cancellationToken),
            ex => localizer["ServiceMessages:BuildNamespaceScalePreviewFailed", namespaceName, messageLocalizer.TranslateExceptionMessage(ex)].Value,
            $"Failed to build namespace {operation} preview for namespace {namespaceName}.",
            messageLocalizer.LocalizeScalePreview);
    }

    public Task<Res<K8SScaleResult>> ScaleResourceAsync(
        string namespaceName,
        K8SResourceType resourceType,
        string resourceName,
        K8SScaleOperation operation,
        CancellationToken cancellationToken = default)
    {
        return ExecuteAsync(
            () => scaleService.ScaleResourcesAsync(namespaceName, resourceType, [resourceName], operation, cancellationToken),
            ex => localizer["ServiceMessages:ScaleResourceFailed", resourceName, namespaceName, messageLocalizer.TranslateExceptionMessage(ex)].Value,
            $"Failed to execute {operation} for K8S resource {resourceType}/{resourceName} in namespace {namespaceName}.",
            messageLocalizer.LocalizeScaleResult);
    }

    public Task<Res<K8SScaleResult>> ScaleResourceAsync(
        string namespaceName,
        string resourceType,
        string resourceName,
        K8SScaleOperation operation,
        CancellationToken cancellationToken = default)
    {
        return TryMapResourceType(resourceType, parsedResourceType => ScaleResourceAsync(namespaceName, parsedResourceType, resourceName, operation, cancellationToken));
    }

    public Task<Res<K8SScaleResult>> ScaleResourcesAsync(
        string namespaceName,
        K8SResourceType resourceType,
        IReadOnlyCollection<string> resourceNames,
        K8SScaleOperation operation,
        CancellationToken cancellationToken = default)
    {
        return ExecuteAsync(
            () => scaleService.ScaleResourcesAsync(namespaceName, resourceType, resourceNames, operation, cancellationToken),
            ex => localizer["ServiceMessages:ScaleResourcesFailed", namespaceName, messageLocalizer.TranslateExceptionMessage(ex)].Value,
            $"Failed to execute {operation} for K8S resources of type {resourceType} in namespace {namespaceName}.",
            messageLocalizer.LocalizeScaleResult);
    }

    public Task<Res<K8SScaleResult>> ScaleResourcesAsync(
        string namespaceName,
        string resourceType,
        IReadOnlyCollection<string> resourceNames,
        K8SScaleOperation operation,
        CancellationToken cancellationToken = default)
    {
        return TryMapResourceType(resourceType, parsedResourceType => ScaleResourcesAsync(namespaceName, parsedResourceType, resourceNames, operation, cancellationToken));
    }

    public Task<Res<K8SScaleResult>> ScaleNamespaceAsync(
        string namespaceName,
        K8SScaleOperation operation,
        CancellationToken cancellationToken = default)
    {
        return ExecuteAsync(
            () => scaleService.ScaleNamespaceAsync(namespaceName, operation, cancellationToken),
            ex => localizer["ServiceMessages:ScaleNamespaceFailed", namespaceName, messageLocalizer.TranslateExceptionMessage(ex)].Value,
            $"Failed to execute namespace {operation} for namespace {namespaceName}.",
            messageLocalizer.LocalizeScaleResult);
    }

    private async Task<Res<T>> TryMapResourceType<T>(
        string resourceType,
        Func<K8SResourceType, Task<Res<T>>> action)
    {
        if (!K8SResourceTypeExtensions.TryParse(resourceType, out var parsedResourceType))
        {
            return Res.Fail(messageLocalizer.GetUnsupportedResourceTypeMessage(resourceType), ResStatus.BadRequest);
        }

        return await action(parsedResourceType);
    }

    private async Task<Res<T>> ExecuteAsync<T>(
        Func<Task<T>> action,
        Func<Exception, string> failureMessageFactory,
        string logMessage,
        Func<T, T>? successTransform = null)
    {
        try
        {
            var result = await action();
            return Res.Ok(successTransform == null ? result : successTransform(result));
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "{LogMessage}", logMessage);
            return Res.Fail(failureMessageFactory(ex), GetResultStatus(ex));
        }
    }

    private async Task<Res> ExecuteAsync(
        Func<Task> action,
        Func<string> successMessageFactory,
        Func<Exception, string> failureMessageFactory,
        string logMessage)
    {
        try
        {
            await action();
            return Res.Ok(successMessageFactory());
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "{LogMessage}", logMessage);
            return Res.Fail(failureMessageFactory(ex), GetResultStatus(ex));
        }
    }

    private static ResStatus GetResultStatus(Exception exception)
    {
        return exception switch
        {
            ArgumentException => ResStatus.BadRequest,
            InvalidOperationException => ResStatus.BadRequest,
            KeyNotFoundException => ResStatus.BadRequest,
            FileNotFoundException => ResStatus.BadRequest,
            K8SOperationException => ResStatus.BadRequest,
            _ => ResStatus.InternalError
        };
    }
}
