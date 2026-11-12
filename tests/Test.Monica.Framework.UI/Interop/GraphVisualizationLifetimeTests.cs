using System.Reflection;
using AwesomeAssertions;
using Microsoft.JSInterop;
using Monica.Framework.UI.UIBuildingBlocks.Components;
using Monica.ServiceDiscovery.UIServiceDiscovery.Components;
using Xunit;

namespace Test.Monica.Framework.UI.Interop;

public sealed class GraphVisualizationLifetimeTests
{
    public static TheoryData<Type> GraphTypes => new()
    {
        typeof(BuildingBlockVisualization),
        typeof(ServiceVisualization),
        typeof(DomainDependencyVisualization)
    };

    [Theory]
    [MemberData(nameof(GraphTypes))]
    public async Task Disposal_cancels_waiting_commands_without_disposing_admission_primitives(Type graphType)
    {
        var graph = CreateGraph(graphType);
        var controller = new ControlledGraphController(blockCommand: true);
        SetField(graph, "_graphController", controller);

        var activeCommand = InvokeGraphAsync(graph, "blocked-command");
        await controller.CommandStarted.WaitAsync(TestContext.Current.CancellationToken);

        var waitingCommands = Enumerable.Range(0, 16)
            .Select(index => InvokeGraphAsync(graph, $"waiting-command-{index}"))
            .ToArray();
        await Task.Yield();

        var disposal = graph.DisposeAsync().AsTask();
        var lifetimeCancellation = GetField<CancellationTokenSource>(graph, "_lifetimeCancellation");
        await WaitUntilAsync(
            () => lifetimeCancellation.IsCancellationRequested,
            TestContext.Current.CancellationToken);

        controller.ReleaseCommand();

        Func<Task> completeAll = () => Task.WhenAll(waitingCommands.Prepend(activeCommand).Append(disposal));
        await completeAll.Should().NotThrowAsync();
        controller.InvocationIdentifiers.Should().Equal("blocked-command", "dispose");
        controller.DisposeCalls.Should().Be(1);

        var interopGate = GetField<SemaphoreSlim>(graph, "_interopGate");
        Func<Task> reacquireGate = async () =>
        {
            await interopGate.WaitAsync(TestContext.Current.CancellationToken);
            interopGate.Release();
        };
        await reacquireGate.Should().NotThrowAsync();
        lifetimeCancellation.Token.IsCancellationRequested.Should().BeTrue();
    }

    [Theory]
    [MemberData(nameof(GraphTypes))]
    public async Task Disposal_stops_browser_callbacks_before_releasing_the_DotNet_reference(Type graphType)
    {
        var graph = CreateGraph(graphType);
        var controller = new ControlledGraphController(blockDispose: true);
        var callbackReference = CreateCallbackReference(graphType, graph);
        SetField(graph, "_graphController", controller);
        SetField(graph, "_dotNetReference", callbackReference);

        var disposal = graph.DisposeAsync().AsTask();
        await controller.DisposeStarted.WaitAsync(TestContext.Current.CancellationToken);

        ReadCallbackTarget(callbackReference).Should().BeSameAs(graph);
        controller.ReleaseDispose();
        await disposal;

        Action readDisposedReference = () => ReadCallbackTarget(callbackReference);
        readDisposedReference.Should().Throw<TargetInvocationException>()
            .Where(exception => exception.InnerException is ObjectDisposedException);
        controller.DisposeCalls.Should().Be(1);
    }

    private static IAsyncDisposable CreateGraph(Type graphType) =>
        (IAsyncDisposable)(Activator.CreateInstance(graphType)
            ?? throw new InvalidOperationException($"Could not create {graphType.FullName}."));

    private static Task InvokeGraphAsync(IAsyncDisposable graph, string identifier)
    {
        var method = graph.GetType().GetMethod(
            "InvokeGraphAsync",
            BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("InvokeGraphAsync was not found.");

        return (Task)(method.Invoke(graph, [identifier, Array.Empty<object?>()])
            ?? throw new InvalidOperationException("InvokeGraphAsync returned no task."));
    }

    private static object CreateCallbackReference(Type graphType, object graph)
    {
        var create = typeof(DotNetObjectReference)
            .GetMethods(BindingFlags.Public | BindingFlags.Static)
            .Single(method => method.Name == nameof(DotNetObjectReference.Create) && method.IsGenericMethodDefinition)
            .MakeGenericMethod(graphType);

        return create.Invoke(null, [graph])
            ?? throw new InvalidOperationException("Could not create the callback reference.");
    }

    private static object ReadCallbackTarget(object callbackReference)
    {
        var value = callbackReference.GetType().GetProperty(nameof(DotNetObjectReference<object>.Value))
            ?? throw new InvalidOperationException("Callback reference value was not found.");

        return value.GetValue(callbackReference)
            ?? throw new InvalidOperationException("Callback reference value was null.");
    }

    private static void SetField(object target, string fieldName, object value)
    {
        var field = target.GetType().GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException($"{fieldName} was not found on {target.GetType().FullName}.");
        field.SetValue(target, value);
    }

    private static TField GetField<TField>(object target, string fieldName)
    {
        var field = target.GetType().GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException($"{fieldName} was not found on {target.GetType().FullName}.");
        return (TField)(field.GetValue(target)
            ?? throw new InvalidOperationException($"{fieldName} was null."));
    }

    private static async Task WaitUntilAsync(Func<bool> condition, CancellationToken cancellationToken)
    {
        while (!condition())
        {
            cancellationToken.ThrowIfCancellationRequested();
            await Task.Yield();
        }
    }

    private sealed class ControlledGraphController(
        bool blockCommand = false,
        bool blockDispose = false) : IJSObjectReference
    {
        private readonly TaskCompletionSource _commandStarted =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _commandRelease =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _disposeStarted =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _disposeRelease =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal Task CommandStarted => _commandStarted.Task;
        internal Task DisposeStarted => _disposeStarted.Task;
        internal List<string> InvocationIdentifiers { get; } = [];
        internal int DisposeCalls { get; private set; }

        internal void ReleaseCommand() => _commandRelease.TrySetResult();
        internal void ReleaseDispose() => _disposeRelease.TrySetResult();

        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args) =>
            InvokeAsync<TValue>(identifier, CancellationToken.None, args);

        public async ValueTask<TValue> InvokeAsync<TValue>(
            string identifier,
            CancellationToken cancellationToken,
            object?[]? args)
        {
            InvocationIdentifiers.Add(identifier);

            if (identifier == "blocked-command")
            {
                _commandStarted.TrySetResult();
                if (blockCommand)
                {
                    await _commandRelease.Task;
                }
            }
            else if (identifier == "dispose")
            {
                _disposeStarted.TrySetResult();
                if (blockDispose)
                {
                    await _disposeRelease.Task;
                }
            }

            return default!;
        }

        public ValueTask DisposeAsync()
        {
            DisposeCalls++;
            return ValueTask.CompletedTask;
        }
    }
}
