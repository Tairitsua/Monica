using Monica.Core.Modularity.Diagnostics.Models;
using Monica.Core.Results;
using Monica.Core.TypeDiscovery.Abstractions;
using Monica.UI.UIModuleSystem.State;
using Monica.UI.UIModuleSystem.Support;
using Xunit;

namespace Test.Monica.UI.UIModuleSystem;

public sealed class ModuleResourceActivationSessionTests
{
    [Fact]
    public async Task LiveReads_ShouldDistinguishFailureFromEmptyAndPreserveLastSuccessfulEvidence()
    {
        IReadOnlyList<ResourceActivationObservation> held = [Observation("held")];
        var results = new Queue<Res<IReadOnlyList<ResourceActivationObservation>>>([
            Res.Fail("Initial read failed."),
            Res.Ok<IReadOnlyList<ResourceActivationObservation>>([]),
            Res.Ok(held),
            Res.Fail("Refresh failed.")
        ]);
        await using var session = new ModuleSystemWorkbenchSession(ModuleSystemWorkbenchTestData.Calls() with
        {
            GetResourceActivations = results.Dequeue
        });
        await session.InitializeAsync();
        var startup = session.Snapshot;

        await session.RefreshResourceActivationsAsync();
        Assert.Equal(ModuleSystemLazyLoadState.Failed, session.ResourceActivationsState);
        Assert.Null(session.ResourceActivations);

        await session.RefreshResourceActivationsAsync();
        Assert.Equal(ModuleSystemLazyLoadState.Ready, session.ResourceActivationsState);
        Assert.Empty(session.ResourceActivations!);

        await session.RefreshResourceActivationsAsync();
        Assert.Same(held, session.ResourceActivations);
        await session.RefreshResourceActivationsAsync();
        Assert.Equal(ModuleSystemLazyLoadState.Failed, session.ResourceActivationsState);
        Assert.Same(held, session.ResourceActivations);
        Assert.Same(startup, session.Snapshot);
    }

    [Fact]
    public async Task EveryLiveRead_ShouldRecheckAccessBeforeCallingTheFacade()
    {
        var authorized = true;
        var authorizationChecks = 0;
        var facadeCalls = 0;
        await using var session = new ModuleSystemWorkbenchSession(ModuleSystemWorkbenchTestData.Calls() with
        {
            GetResourceActivations = () =>
            {
                facadeCalls++;
                return Res.Ok<IReadOnlyList<ResourceActivationObservation>>([Observation("resource")]);
            }
        }, () =>
        {
            authorizationChecks++;
            return Task.FromResult(authorized);
        });

        await session.RefreshResourceActivationsAsync();
        authorized = false;
        await session.RefreshResourceActivationsAsync();
        Assert.True(session.IsAccessDenied);
        Assert.Equal(1, facadeCalls);

        authorized = true;
        await session.RefreshResourceActivationsAsync();
        Assert.False(session.IsAccessDenied);
        Assert.Equal(3, authorizationChecks);
        Assert.Equal(2, facadeCalls);
        Assert.Equal(ModuleSystemLazyLoadState.Ready, session.ResourceActivationsState);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PendingAuthorization_ShouldNotReadAfterNewerRequestOrDisposal(bool dispose)
    {
        var authorization = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var authorizationChecks = 0;
        var facadeCalls = 0;
        await using var session = new ModuleSystemWorkbenchSession(ModuleSystemWorkbenchTestData.Calls() with
        {
            GetResourceActivations = () =>
            {
                facadeCalls++;
                return Res.Ok<IReadOnlyList<ResourceActivationObservation>>([Observation("newer")]);
            }
        }, () => ++authorizationChecks == 1 ? authorization.Task : Task.FromResult(true));

        var pending = session.RefreshResourceActivationsAsync();
        if (dispose) await session.DisposeAsync();
        else await session.RefreshResourceActivationsAsync();
        authorization.SetResult(true);
        await pending.WaitAsync(TestContext.Current.CancellationToken);

        Assert.Equal(dispose ? 0 : 1, facadeCalls);
        if (dispose) Assert.Null(session.ResourceActivations);
        else Assert.Equal("newer", Assert.Single(session.ResourceActivations!).Key);
    }

    private static ResourceActivationObservation Observation(string key) => new()
    {
        Key = key,
        Kind = TypeActivationKind.Seeder,
        Mode = ActivationMode.Manual,
        Reason = "Awaiting an explicit request."
    };
}
