using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Monica.Core.Execution;
using Monica.Core.HostedService.Abstractions;
using Monica.Core.HostedService.Models;

namespace Monica.Core.HostedService.Services.Support;

internal static class HostedServiceExecutionAdapter
{
    private static readonly MethodInfo START_METHOD = typeof(IHostedService).GetMethod(nameof(IHostedService.StartAsync))!;
    private static readonly MethodInfo STOP_METHOD = typeof(IHostedService).GetMethod(nameof(IHostedService.StopAsync))!;

    public static async Task ExecuteLifecycleAsync(
        IServiceScopeFactory serviceScopeFactory,
        object service,
        string serviceName,
        ExecutionPoint point,
        HostedServiceLifecyclePhase phase,
        Func<Task> terminal,
        CancellationToken cancellationToken)
    {
        await using var scope = serviceScopeFactory.CreateAsyncScope();
        var serviceType = service.GetType();
        var isStart = point == HostedServiceExecutionPoints.Start;
        var entryMethod = isStart ? START_METHOD : STOP_METHOD;
        var descriptor = ExecutionDescriptor.ForMethod<ExecutionUnit, ExecutionUnit>(
            point,
            serviceType,
            entryMethod,
            isBusinessOperation: false,
            transactionMode: ExecutionTransactionMode.None);
        var features = CreateFeatures(serviceName, phase, serviceType);

        await scope.ServiceProvider.GetRequiredService<IExecutionPipeline>()
            .ExecuteAsync(
                descriptor,
                ExecutionUnit.Value,
                service,
                terminal,
                cancellationToken,
                features)
            .ConfigureAwait(false);
    }

    public static async Task ExecuteWorkItemAsync<TWorkItem>(
        IServiceScopeFactory serviceScopeFactory,
        object service,
        string serviceName,
        CancellationToken cancellationToken)
        where TWorkItem : class, IHostedServiceWorkItem
    {
        await using var scope = serviceScopeFactory.CreateAsyncScope();
        var workItem = scope.ServiceProvider.GetRequiredService<TWorkItem>();
        var descriptor = ExecutionDescriptor.ForInterface<ExecutionUnit, ExecutionUnit>(
            HostedServiceExecutionPoints.WorkItem,
            workItem.GetType(),
            typeof(IHostedServiceWorkItem),
            isBusinessOperation: true,
            transactionMode: ExecutionTransactionMode.Automatic);
        var features = CreateFeatures(
            serviceName,
            HostedServiceLifecyclePhase.WorkItem,
            service.GetType());

        await scope.ServiceProvider.GetRequiredService<IExecutionPipeline>()
            .ExecuteAsync(
                descriptor,
                ExecutionUnit.Value,
                workItem,
                () => workItem.ExecuteAsync(cancellationToken),
                cancellationToken,
                features)
            .ConfigureAwait(false);
    }

    public static async Task<TResult> ExecuteWorkItemAsync<TWorkItem, TInput, TResult>(
        IServiceScopeFactory serviceScopeFactory,
        object service,
        string serviceName,
        TInput input,
        CancellationToken cancellationToken)
        where TWorkItem : class, IHostedServiceWorkItem<TInput, TResult>
    {
        await using var scope = serviceScopeFactory.CreateAsyncScope();
        var workItem = scope.ServiceProvider.GetRequiredService<TWorkItem>();
        var descriptor = ExecutionDescriptor.ForInterface<TInput, TResult>(
            HostedServiceExecutionPoints.WorkItem,
            workItem.GetType(),
            typeof(IHostedServiceWorkItem<TInput, TResult>),
            isBusinessOperation: true,
            transactionMode: ExecutionTransactionMode.Automatic);
        var features = CreateFeatures(
            serviceName,
            HostedServiceLifecyclePhase.WorkItem,
            service.GetType());

        return await scope.ServiceProvider.GetRequiredService<IExecutionPipeline>()
            .ExecuteAsync(
                descriptor,
                input,
                workItem,
                () => workItem.ExecuteAsync(input, cancellationToken),
                cancellationToken,
                features)
            .ConfigureAwait(false);
    }

    private static ExecutionFeatureCollection CreateFeatures(
        string serviceName,
        HostedServiceLifecyclePhase phase,
        Type ownerType)
    {
        var features = new ExecutionFeatureCollection();
        features.Set(new HostedServiceExecutionFeature(serviceName, phase, ownerType));
        return features;
    }
}
