using Monica.DevOps.K8S.Models;
using Monica.DevOps.K8S.State;
using Test.Monica.DevOps.K8S.Support;
using Xunit;

namespace Test.Monica.DevOps.K8S.State;

public sealed class K8SInventoryVisitControllerTests
{
    private static K8SNavigationState Context() => new() { Namespace = "alpha", ResourceType = K8SResourceType.Deployment, HasContext = true };

    [Fact]
    public async Task ChangeContext_WhenOnlyKindChanges_ShouldProjectSameNamespaceCaptureWithoutReadingAgain()
    {
        var fixture = new K8SStateFixture();
        await using var visit = new K8SInventoryVisitController(fixture.Facade, Context(), () => Task.CompletedTask);
        await visit.InitializeAsync();
        var capture = visit.NamespaceCapture!;
        var row = visit.Capture!.Items[0];
        visit.SetSelected([row]);
        var commandCount = fixture.Provider.Commands.Count;

        await visit.ChangeContextAsync("alpha", K8SResourceType.Service);

        Assert.Same(capture, visit.NamespaceCapture);
        Assert.Equal(capture.FetchedAtUtc, visit.Capture!.FetchedAtUtc);
        Assert.Empty(visit.Capture.Items);
        Assert.Equal(0, visit.SelectedCount);
        Assert.True(visit.ContextMatchesNamespaceCapture);
        Assert.True(visit.ContextMatchesCapture);
        Assert.Equal(commandCount, fixture.Provider.Commands.Count);

        await visit.ChangeContextAsync("alpha", K8SResourceType.Deployment);

        Assert.Same(row, visit.Capture!.Items[0]);
        Assert.Equal(commandCount, fixture.Provider.Commands.Count);
    }

    [Fact]
    public async Task RefreshList_WhenLaterNamespaceReadFails_ShouldRetainWholeCaptureAndSelectedEvidence()
    {
        var fixture = new K8SStateFixture();
        await using var visit = new K8SInventoryVisitController(fixture.Facade, Context(), () => Task.CompletedTask);
        await visit.InitializeAsync();
        var namespaceCapture = visit.NamespaceCapture;
        var capture = visit.Capture;
        var selected = capture!.Items[0];
        visit.SetSelected([selected]);
        fixture.Provider.Handler = (arguments, _) => arguments.StartsWith("get pod ", StringComparison.Ordinal)
            ? Task.FromException<string>(new IOException("Pod evidence failed."))
            : Task.FromResult(fixture.Provider.DefaultOutput(arguments));

        await visit.RefreshListAsync();

        Assert.Same(namespaceCapture, visit.NamespaceCapture);
        Assert.Same(capture, visit.Capture);
        Assert.Same(selected, Assert.Single(visit.SelectedRows));
        Assert.NotNull(visit.ReadFailure);
    }

    [Fact]
    public async Task RefreshList_WhenReadFails_ShouldRetainOriginalCaptureAndSelection()
    {
        var fixture = new K8SStateFixture();
        await using var visit = new K8SInventoryVisitController(fixture.Facade, Context(), () => Task.CompletedTask);
        await visit.InitializeAsync();
        var capture = visit.Capture!;
        visit.SetSelected([capture.Items[0]]);
        fixture.Provider.Handler = (_, _) => Task.FromException<string>(new IOException("Read failed."));

        await visit.RefreshListAsync();

        Assert.Same(capture, visit.Capture);
        Assert.Equal(capture.FetchedAtUtc, visit.Capture!.FetchedAtUtc);
        Assert.Equal(1, visit.SelectedCount);
        Assert.Equal("alpha", visit.ReadFailure!.Namespace);
    }

    [Fact]
    public async Task ChangeContext_WhenReadFails_ShouldKeepCaptureIdentityDistinctFromAttempt()
    {
        var fixture = new K8SStateFixture();
        await using var visit = new K8SInventoryVisitController(fixture.Facade, Context(), () => Task.CompletedTask);
        await visit.InitializeAsync();
        var capture = visit.Capture;
        fixture.Provider.Handler = (_, _) => Task.FromException<string>(new IOException("New context failed."));

        await visit.ChangeContextAsync("beta", K8SResourceType.Service);

        Assert.Same(capture, visit.Capture);
        Assert.Equal("alpha", visit.Capture!.Namespace);
        Assert.Equal(K8SResourceType.Deployment, visit.Capture!.ResourceType);
        Assert.Equal("beta", visit.Namespace);
        Assert.Equal(K8SResourceType.Service, visit.ReadFailure!.ResourceType);
        Assert.False(visit.ContextMatchesCapture);
        Assert.False(visit.CanOperate);
    }

    [Fact]
    public async Task Initialize_WhenInitialDiscoveryFails_ShouldHaveNoSuccessfulEmptyCapture()
    {
        var fixture = new K8SStateFixture();
        fixture.Provider.Handler = (_, _) => Task.FromException<string>(new IOException("Discovery failed."));
        await using var visit = new K8SInventoryVisitController(fixture.Facade, Context(), () => Task.CompletedTask);

        await visit.InitializeAsync();

        Assert.Null(visit.Capture);
        Assert.Null(visit.Namespaces);
        Assert.NotNull(visit.ContextFailure);
        Assert.False(visit.CanOperate);
    }

    [Fact]
    public async Task RefreshList_WhenRowsAreRecreated_ShouldReconcileSelectedIdentities()
    {
        var fixture = new K8SStateFixture();
        await using var visit = new K8SInventoryVisitController(fixture.Facade, Context(), () => Task.CompletedTask);
        await visit.InitializeAsync();
        var original = visit.Capture!.Items[0];
        var removed = visit.Capture!.Items[1];
        visit.SetSelected(visit.Capture!.Items);
        fixture.Provider.WorkloadJson = """{"items":[{"kind":"Deployment","metadata":{"name":"api","namespace":"alpha"},"spec":{"replicas":3},"status":{"readyReplicas":2}}]}""";

        await visit.RefreshListAsync();

        var selected = Assert.Single(visit.SelectedRows);
        Assert.Equal("api", selected.Name);
        Assert.NotSame(original, selected);
        Assert.Equal(1, visit.HiddenSelectionCount([]));
        Assert.Equal(0, visit.HiddenSelectionCount([selected]));
        Assert.Same(K8SResourceIdentity.Comparer, visit.SelectedRows.Comparer);

        // Native parameter adoption can report an older row object with a still-current identity.
        visit.SetSelected([original, removed,
            new K8SResourceSummary { Namespace = "beta", ResourceType = K8SResourceType.Deployment, Name = "api" },
            new K8SResourceSummary { Namespace = "alpha", ResourceType = K8SResourceType.Service, Name = "api" }]);
        Assert.Same(selected, Assert.Single(visit.SelectedRows));

        visit.SetSelected([]);
        Assert.Equal(0, visit.SelectedCount);
        visit.SetSelected([original]);
        Assert.Same(selected, Assert.Single(visit.SelectedRows));
        visit.ClearSelected();
        Assert.Equal(0, visit.SelectedCount);
    }

    [Fact]
    public async Task Operate_WhenConfirmationIsPending_ShouldFreezeIdentityAndRejectContextMutation()
    {
        var fixture = new K8SStateFixture();
        var navigation = Context();
        await using var visit = new K8SInventoryVisitController(fixture.Facade, navigation, () => Task.CompletedTask);
        await visit.InitializeAsync();
        visit.SetSelected([visit.Capture!.Items[0]]);
        var request = visit.CreateRequest(K8SInventoryOperation.Restart, K8SOperationScope.Selected)!;
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var confirm = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var running = visit.OperateAsync(request, (_, frozen, _) =>
        {
            Assert.Same(request, frozen);
            entered.SetResult();
            return confirm.Task;
        }, (_, _, _) => Task.FromResult(false));
        await entered.Task;
        try
        {
            Assert.True(visit.IsBusy);
            Assert.Equal(K8SInventoryActivity.Reviewing, visit.Activity);
            Assert.Contains("alpha / Deployment / api", visit.ActivityIdentity);
            navigation.Namespace = "beta";
            await visit.ChangeContextAsync("beta", K8SResourceType.Service);
            visit.ClearSelected();
        }
        finally { confirm.TrySetResult(true); }
        await running;

        Assert.Equal("alpha", request.Namespace);
        Assert.Equal("api", Assert.Single(request.Names));
        var command = Assert.Single(fixture.Provider.Commands, x => x.StartsWith("rollout restart", StringComparison.Ordinal));
        Assert.Contains("'alpha'", command);
        Assert.Contains("'api'", command);
        Assert.Equal(request, visit.LatestOperation!.Request);
        Assert.NotEmpty(visit.LatestOperation.Restart!.ExecutedCommands);
    }

    [Fact]
    public async Task Operate_WhenConfirmationIsCanceled_ShouldExecuteNoCommand()
    {
        var fixture = new K8SStateFixture();
        await using var visit = new K8SInventoryVisitController(fixture.Facade, Context(), () => Task.CompletedTask);
        await visit.InitializeAsync();
        visit.SetSelected([visit.Capture!.Items[0]]);
        var request = visit.CreateRequest(K8SInventoryOperation.Restart, K8SOperationScope.Selected)!;

        await visit.OperateAsync(request, (_, _, _) => Task.FromResult(false), (_, _, _) => Task.FromResult(false));

        Assert.DoesNotContain(fixture.Provider.Commands, x => x.StartsWith("rollout restart", StringComparison.Ordinal));
        Assert.Null(visit.LatestOperation);
    }

    [Fact]
    public async Task Operate_WhenPostCommandReadFails_ShouldKeepCompletedResultSeparate()
    {
        var fixture = new K8SStateFixture();
        await using var visit = new K8SInventoryVisitController(fixture.Facade, Context(), () => Task.CompletedTask);
        await visit.InitializeAsync();
        var capture = visit.Capture!;
        visit.SetSelected([capture.Items[0]]);
        var failReads = false;
        fixture.Provider.Handler = (arguments, _) =>
        {
            if (arguments.StartsWith("rollout restart", StringComparison.Ordinal)) failReads = true;
            else if (failReads) return Task.FromException<string>(new IOException("Refresh after command failed."));
            return Task.FromResult(fixture.Provider.DefaultOutput(arguments));
        };

        await visit.OperateAsync(visit.CreateRequest(K8SInventoryOperation.Restart, K8SOperationScope.Selected)!,
            (_, _, _) => Task.FromResult(true), (_, _, _) => Task.FromResult(false));

        Assert.Null(visit.LatestOperation!.Failure);
        Assert.NotNull(visit.LatestOperation.Restart);
        Assert.Single(visit.LatestOperation.Commands);
        Assert.NotNull(visit.ReadFailure);
        Assert.Same(capture, visit.Capture);
    }

    [Fact]
    public async Task Dispose_WhenProviderIgnoresCancellation_ShouldDrainActualProducerAndSuppressPublication()
    {
        var fixture = new K8SStateFixture();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var released = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Provider.Handler = (arguments, _) =>
        {
            if (!arguments.StartsWith("get deployment", StringComparison.Ordinal)) return Task.FromResult(fixture.Provider.DefaultOutput(arguments));
            entered.TrySetResult();
            return released.Task;
        };
        var published = 0;
        var visit = new K8SInventoryVisitController(fixture.Facade, Context(), () => { published++; return Task.CompletedTask; });
        var reading = visit.InitializeAsync();
        await entered.Task;
        var first = visit.DisposeAsync().AsTask();
        var second = visit.DisposeAsync().AsTask();
        var publicationAtRetirement = published;

        try { Assert.Same(first, second); Assert.False(first.IsCompleted); }
        finally { released.TrySetResult(fixture.Provider.WorkloadJson); }
        await Task.WhenAll(reading, first);

        Assert.Null(visit.Capture);
        Assert.Null(visit.NamespaceCapture);
        Assert.Equal(publicationAtRetirement, published);
        Assert.False(visit.IsInitialized);
    }

    [Fact]
    public async Task WhenIdle_WhenReadIsPending_ShouldWaitForProducerBeforeFollowingContext()
    {
        var fixture = new K8SStateFixture();
        await using var visit = new K8SInventoryVisitController(fixture.Facade, Context(), () => Task.CompletedTask);
        await visit.InitializeAsync();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var released = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Provider.Handler = (arguments, _) => { entered.TrySetResult(); return released.Task; };
        var reading = visit.RefreshListAsync();
        await entered.Task;
        var idle = visit.WhenIdleAsync();
        try { Assert.False(idle.IsCompleted); }
        finally { released.TrySetResult("""{"items":[]}"""); }
        await Task.WhenAll(reading, idle);
        fixture.Provider.Handler = null;

        await visit.ChangeContextAsync("beta", K8SResourceType.Service);

        Assert.Equal("beta", visit.Capture!.Namespace);
        Assert.Equal(K8SResourceType.Service, visit.Capture!.ResourceType);
        Assert.True(visit.ContextMatchesCapture);
    }

    [Fact]
    public void Create_WhenCallerMutatesNames_ShouldKeepAnImmutableCopiedRequest()
    {
        var names = new List<string> { "api", "api" };
        var request = K8SOperationRequest.Create("alpha", K8SResourceType.Deployment,
            K8SOperationScope.Selected, K8SInventoryOperation.Restart, names);
        names.Clear();
        Assert.Equal("api", Assert.Single(request.Names));
    }

    [Fact]
    public async Task Dispose_WhenCommandIgnoresCancellation_ShouldDrainCommandWithoutPublishingOrRefreshing()
    {
        var fixture = new K8SStateFixture();
        var visit = new K8SInventoryVisitController(fixture.Facade, Context(), () => Task.CompletedTask);
        await visit.InitializeAsync();
        visit.SetSelected([visit.Capture!.Items[0]]);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var released = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Provider.Handler = (arguments, _) =>
        {
            if (!arguments.StartsWith("rollout restart", StringComparison.Ordinal)) return Task.FromResult(fixture.Provider.DefaultOutput(arguments));
            entered.TrySetResult();
            return released.Task;
        };
        var operation = visit.OperateAsync(visit.CreateRequest(K8SInventoryOperation.Restart, K8SOperationScope.Selected)!,
            (_, _, _) => Task.FromResult(true), (_, _, _) => Task.FromResult(false));
        await entered.Task;
        var commandsAtRetirement = fixture.Provider.Commands.Count;
        Assert.Equal(K8SInventoryActivity.Executing, visit.Activity);
        Assert.Contains("alpha / Deployment / api", visit.ActivityIdentity);
        var retirement = visit.DisposeAsync().AsTask();
        try { Assert.False(retirement.IsCompleted); }
        finally { released.TrySetResult("command completed"); }
        await Task.WhenAll(operation, retirement);

        Assert.Equal(commandsAtRetirement, fixture.Provider.Commands.Count);
        Assert.Null(visit.LatestOperation);
    }
}
