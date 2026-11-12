using System.Net;
using AwesomeAssertions;
using Monica.Core.Results;
using Monica.Modules;
using Monica.Utilities.Connectivity.Models;
using Monica.Utilities.UIUtilities.State;
using Test.Monica.Utilities.Support;
using Xunit;

namespace Test.Monica.Utilities.UIUtilities.State;

public sealed class ConnectivityProbeStateTests
{
    [Fact]
    public async Task Reset_WhenAdmissionNotificationStillOwnsRequest_ShouldRejectNewRunUntilCancellationDrains()
    {
        await using var endpoint = await ControlledHttpEndpoint.StartAsync();
        var state = CreateHttpState(endpoint, "/complete");
        var notified = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var notifications = 0;
        state.Changed += HoldAdmissionAsync;
        var pending = state.RunAsync(TestContext.Current.CancellationToken);
        try
        {
            await ControlledHttpEndpoint.ObserveAsync(notified.Task);

            state.Reset();

            state.IsBusy.Should().BeTrue();
            state.ActiveRequest.Should().NotBeNull();
            Func<Task> duplicate = async () => await state.RunAsync(TestContext.Current.CancellationToken);
            await duplicate.Should().ThrowAsync<InvalidOperationException>();
        }
        finally
        {
            state.Reset();
            release.TrySetResult();
            Func<Task> completion = async () => await pending.WaitAsync(
                TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
            await completion.Should().ThrowAsync<OperationCanceledException>();
        }

        state.IsBusy.Should().BeFalse();
        state.ActiveRequest.Should().BeNull();
        state.LastResult.Should().BeNull();
        state.ErrorMessage.Should().BeNull();
        endpoint.RequestCount.Should().Be(0);
        notifications.Should().Be(2);

        Task HoldAdmissionAsync()
        {
            if (++notifications != 1)
                return Task.CompletedTask;
            notified.TrySetResult();
            return release.Task;
        }
    }

    [Fact]
    public async Task RunAsync_WhenAnotherHttpRequestIsActive_ShouldRejectDuplicateAdmission()
    {
        await using var endpoint = await ControlledHttpEndpoint.StartAsync();
        var state = CreateHttpState(endpoint, "/held");
        using var cancellation = new CancellationTokenSource();
        var pending = state.RunAsync(cancellation.Token);
        try
        {
            await ControlledHttpEndpoint.ObserveAsync(endpoint.Admitted.Task);
            var active = state.ActiveRequest;
            var revision = state.Revision;
            state.IsBusy.Should().BeTrue();

            Func<Task> duplicate = async () => await state.RunAsync(TestContext.Current.CancellationToken);
            await duplicate.Should().ThrowAsync<InvalidOperationException>();

            endpoint.RequestCount.Should().Be(1);
            state.Revision.Should().Be(revision);
            state.ActiveRequest.Should().BeSameAs(active);
        }
        finally
        {
            cancellation.Cancel();
            await AssertCanceledAndDrainedAsync(pending, endpoint);
        }

        state.IsBusy.Should().BeFalse();
        state.ActiveRequest.Should().BeNull();
        state.LastResult.Should().BeNull();
    }

    [Fact]
    public async Task Reset_WhenHttpRequestHasBeenAdmitted_ShouldCancelInvalidateAndClearWithoutLatePublication()
    {
        await using var endpoint = await ControlledHttpEndpoint.StartAsync();
        var options = new ModuleUtilitiesOption
        {
            DefaultHttpRequestPath = "/default",
            DefaultProbeTimeoutMilliseconds = 15000,
            MaximumProbeTimeoutMilliseconds = 20000
        };
        var state = CreateHttpState(endpoint, "/complete", options);
        (await state.RunAsync(TestContext.Current.CancellationToken)).Data!.Succeeded.Should().BeTrue();
        state.Path = "/held";
        var pending = state.RunAsync(TestContext.Current.CancellationToken);
        try
        {
            await ControlledHttpEndpoint.ObserveAsync(endpoint.Admitted.Task);
            var revision = state.Revision;

            state.Reset();

            state.Revision.Should().BeGreaterThan(revision);
            state.Host.Should().BeEmpty();
            state.Port.Should().BeNull();
            state.ProbeKind.Should().Be(ConnectivityProbeKind.Ping);
            state.Path.Should().Be("/default");
            state.TimeoutMilliseconds.Should().Be(15000);
            state.AllowInvalidCertificate.Should().BeFalse();
            state.LastResult.Should().BeNull();
            state.CompletedRequest.Should().BeNull();
        }
        finally
        {
            state.Reset();
            await AssertCanceledAndDrainedAsync(pending, endpoint);
        }

        state.IsBusy.Should().BeFalse();
        state.ActiveRequest.Should().BeNull();
        state.ErrorMessage.Should().BeNull();
        state.LastResult.Should().BeNull();
        state.CompletedRequest.Should().BeNull();
    }

    [Fact]
    public async Task RunAsync_WhenVisitRetiresAfterHttpAdmission_ShouldRetainEvidenceAndDraftThenAllowNextVisit()
    {
        await using var endpoint = await ControlledHttpEndpoint.StartAsync();
        var state = CreateHttpState(endpoint, "/complete");
        var first = await state.RunAsync(TestContext.Current.CancellationToken);
        first.Status.Should().Be(ResultStatus.Ok);
        first.Message.Should().BeNull();
        first.Data.Should().NotBeNull();
        first.Data!.Succeeded.Should().BeTrue();
        var acceptedRequest = state.CompletedRequest;
        state.Path = "/held";
        using var retiredVisit = new CancellationTokenSource();
        var pending = state.RunAsync(retiredVisit.Token);
        try
        {
            await ControlledHttpEndpoint.ObserveAsync(endpoint.Admitted.Task);
            state.ActiveRequest!.Path.Should().Be("/held");
            state.Path = "/complete";
            state.TimeoutMilliseconds = 17000;
            state.ActiveRequest!.Path.Should().Be("/held");
        }
        finally
        {
            retiredVisit.Cancel();
            await AssertCanceledAndDrainedAsync(pending, endpoint);
        }

        state.LastResult.Should().BeSameAs(first.Data);
        state.CompletedRequest.Should().BeSameAs(acceptedRequest);
        state.ErrorMessage.Should().BeNull();
        state.Path.Should().Be("/complete");
        state.TimeoutMilliseconds.Should().Be(17000);
        state.IsBusy.Should().BeFalse();
        state.ActiveRequest.Should().BeNull();

        var returnedVisit = await state.RunAsync(TestContext.Current.CancellationToken);

        returnedVisit.Status.Should().Be(ResultStatus.Ok);
        returnedVisit.Message.Should().BeNull();
        returnedVisit.Data.Should().NotBeNull();
        returnedVisit.Data!.Succeeded.Should().BeTrue();
        returnedVisit.Data.TimeoutMilliseconds.Should().Be(17000);
        state.LastResult.Should().BeSameAs(returnedVisit.Data);
        state.CompletedRequest!.Path.Should().Be("/complete");
        state.CompletedRequest!.TimeoutMilliseconds.Should().Be(17000);
        endpoint.RequestCount.Should().Be(3);
    }

    [Fact]
    public async Task RunAsync_WhenHttpsCompletesAndDraftChanges_ShouldRetainSubmittedTlsChoiceAndEffectiveTimeout()
    {
        await using var endpoint = await ControlledHttpEndpoint.StartAsync(useHttps: true);
        var options = new ModuleUtilitiesOption { MaximumProbeTimeoutMilliseconds = 9000 };
        var state = CreateHttpState(endpoint, "/complete", options);
        state.SetProbeKind(ConnectivityProbeKind.Https);
        state.AllowInvalidCertificate = true;
        state.TimeoutMilliseconds = 18000;

        var result = await state.RunAsync(TestContext.Current.CancellationToken);

        result.Status.Should().Be(ResultStatus.Ok);
        result.Message.Should().BeNull();
        result.Data.Should().NotBeNull();
        result.Data!.Succeeded.Should().BeTrue(
            "the owned HTTPS endpoint should accept its generated certificate; TCP failure: {0}; HTTP failure: {1}; DNS failure: {2}; endpoint: {3}",
            result.Data.Tcp?.FailureMessage, result.Data.Http?.FailureMessage,
            result.Data.ResolutionError, result.Data.Endpoint);
        result.Data.Http!.StatusCode.Should().Be(HttpStatusCode.NoContent);
        result.Data.TimeoutMilliseconds.Should().Be(9000);
        state.MaximumTimeoutMilliseconds.Should().Be(9000);
        var completed = state.CompletedRequest;
        completed!.ProbeKind.Should().Be(ConnectivityProbeKind.Https);
        completed.AllowInvalidCertificate.Should().BeTrue();
        completed.TimeoutMilliseconds.Should().Be(18000);

        state.AllowInvalidCertificate = false;
        state.TimeoutMilliseconds = 500;
        state.Host = "edited.example";

        state.CompletedRequest.Should().BeSameAs(completed);
        state.CompletedRequest!.Host.Should().Be(endpoint.Address.Host);
        state.CompletedRequest!.AllowInvalidCertificate.Should().BeTrue();
        state.LastResult.Should().BeSameAs(result.Data);
        state.LastResult!.TimeoutMilliseconds.Should().Be(9000);
    }

    [Fact]
    public void MaximumTimeoutMilliseconds_WhenConfiguredBelowServiceMinimum_ShouldExposeEffectiveLowerBound()
    {
        var state = UtilitiesTestServices.CreateConnectivityState(
            new ModuleUtilitiesOption { MaximumProbeTimeoutMilliseconds = 100 });

        state.MaximumTimeoutMilliseconds.Should().Be(250);
    }

    private static ConnectivityProbeState CreateHttpState(ControlledHttpEndpoint endpoint, string path,
        ModuleUtilitiesOption? options = null)
    {
        var state = UtilitiesTestServices.CreateConnectivityState(options);
        state.SetProbeKind(ConnectivityProbeKind.Http);
        state.Host = endpoint.Address.Host;
        state.Port = endpoint.Address.Port;
        state.Path = path;
        return state;
    }

    private static async Task AssertCanceledAndDrainedAsync(Task<Result<ConnectivityProbeResult>> pending,
        ControlledHttpEndpoint endpoint)
    {
        Func<Task> completion = async () => await pending.WaitAsync(
            TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        await completion.Should().ThrowAsync<OperationCanceledException>();
        await endpoint.ObserveCanceledConnectionDrainAsync();
    }
}
