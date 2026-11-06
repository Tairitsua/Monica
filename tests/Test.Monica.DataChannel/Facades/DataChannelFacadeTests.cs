using AwesomeAssertions;
using Monica.Core.Results;
using Monica.DataChannel.Abstractions;
using Monica.DataChannel.Facades;
using Monica.DataChannel.Middlewares;
using Monica.DataChannel.Models;
using Monica.DataChannel.Pipeline;
using Test.Monica.DataChannel.Support;
using Xunit;

namespace Test.Monica.DataChannel.Facades;

public sealed class DataChannelFacadeTests
{
    [Fact]
    public async Task Capture_WhenOrdinaryRecordsAndBoundedExceptionsExist_ShouldSeparateRetainedLifetimeAndRequestedCounts()
    {
        var scenario = new DataChannelScenario();
        await using var application = await new DataChannelTestApplicationFactory(scenario).CreateAsync(cancellationToken: TestContext.Current.CancellationToken);
        await using var scope = application.CreateScope(TestContext.Current.CancellationToken);
        var facade = scope.Resolve<DataChannelFacade>();
        var channel = scope.Resolve<IDataChannelManager>().Fetch(scenario.Primary.Id)!;
        channel.Pipe.RecordState(PipelineState.Error, "Ordinary state record without an exception.");
        scenario.Primary.Outer.FailReceive = true;
        for (var index = 0; index < 4; index++)
        {
            var send = () => channel.SendDataFromInnerAsync($"failure-{index}");
            await send.Should().ThrowAsync<InvalidOperationException>();
        }

        var capture = ResultAssertions.Success(await facade.GetManagementSnapshotAsync(scope.CancellationToken));
        var status = capture.Channels.Single();
        var summary = capture.ExceptionSummary.ChannelSummaries.Single();
        status.Id.Should().Be(scenario.Primary.Id);
        status.GroupId.Should().Be(scenario.Primary.GroupId);
        status.ExceptionCount.Should().Be(3);
        status.TotalExceptionCount.Should().Be(4);
        status.MaxHistorySize.Should().Be(3);
        summary.CapturedAt.Should().Be(status.CapturedAt);
        summary.CurrentExceptionCount.Should().Be(status.ExceptionCount);
        summary.TotalExceptionCount.Should().Be(status.TotalExceptionCount);
        capture.ExceptionSummary.TotalHistoricalExceptions.Should().Be(4);
        capture.ExceptionSummary.TotalCurrentExceptions.Should().Be(3);
        capture.ExceptionSummary.ChannelsWithExceptions.Should().Be(1);
        ResultAssertions.Success(await facade.GetChannelsStatusAsync(scope.CancellationToken)).Single().TotalExceptionCount.Should().Be(4);
        ResultAssertions.Success(await facade.GetExceptionSummaryAsync(scope.CancellationToken)).TotalHistoricalExceptions.Should().Be(4);

        var detail = ResultAssertions.Success(await facade.GetChannelExceptionsAsync(channel.Id, 1, scope.CancellationToken));
        detail.CurrentExceptions.Should().Be(3);
        detail.TotalExceptions.Should().Be(4);
        detail.RequestedCount.Should().Be(1);
        var last = detail.Exceptions.Single();
        last.Sequence.Should().Be(5);
        last.Message.Should().Contain("failure-3");
        last.StackTrace.Should().Contain(nameof(EndpointBehavior.ReceiveAsync));
        last.SourceType.Should().BeEmpty();
        last.SourceDescription.Should().BeEmpty();
        last.CaptureError.Should().BeNull();
        ResultAssertions.Success(await facade.GetChannelExceptionsAsync(channel.Id, 0, scope.CancellationToken)).Exceptions.Should().BeEmpty();

        channel.Pipe.RecordState(PipelineState.Error, "Another ordinary record to clear.");
        var cleared = await facade.ClearChannelExceptionsAsync(channel.Id);
        cleared.Status.Should().Be(ResultStatus.Ok);
        cleared.Message.Should().Contain("retained observable history");
        channel.Pipe.ObservableTracker.Count.Should().Be(0);
        var after = ResultAssertions.Success(await facade.GetManagementSnapshotAsync(scope.CancellationToken));
        after.Channels.Single().ExceptionCount.Should().Be(0);
        after.Channels.Single().HasExceptions.Should().BeFalse();
        after.Channels.Single().IsInitialized.Should().BeTrue();
        after.ExceptionSummary.TotalHistoricalExceptions.Should().Be(4);
        after.ExceptionSummary.ChannelSummaries.Single().LatestException.Should().BeNull();
        detail.Exceptions.Single().Message.Should().Contain("failure-3", "an earlier capture remains frozen after clear");
    }

    [Fact]
    public async Task Capture_WhenMetadataAndInfoValuesChange_ShouldKeepDetachedValuesAndLocalCaptureFailures()
    {
        var metadata = new MutablePayload();
        var infoValue = new MutablePayload();
        var information = new MutableInfoMiddleware();
        information.SetValue("nested", infoValue);
        information.SetValue("unreadable", new FailingPayload());
        var scenario = new DataChannelScenario();
        scenario.Primary.Middlewares.AddRange([new MutableMetadataMiddleware(metadata), information, new FailingMetadataMiddleware()]);
        await using var application = await new DataChannelTestApplicationFactory(scenario).CreateAsync(cancellationToken: TestContext.Current.CancellationToken);
        await using var scope = application.CreateScope(TestContext.Current.CancellationToken);
        var facade = scope.Resolve<DataChannelFacade>();
        var capture = ResultAssertions.Success(await facade.GetManagementSnapshotAsync(scope.CancellationToken));
        var components = capture.Channels.Single().Middlewares;
        var capturedMetadata = components[0].Metadata;
        var capturedInfo = components[1].InfoDictionary!;

        metadata.Content = "Changed metadata";
        metadata.Children.Clear();
        infoValue.Content = "Changed information";
        information.ClearInfo();
        capturedMetadata.Json.Should().Contain("Original evidence 完整");
        capturedMetadata.Json.Should().Contain("nested");
        capturedMetadata.Json.Should().NotContain("Changed");
        capturedInfo["nested"].Json.Should().Contain("Original evidence 完整");
        capturedInfo["unreadable"].Json.Should().BeNull();
        capturedInfo["unreadable"].CaptureError.Should().NotBeNullOrEmpty();
        components[2].Metadata.Json.Should().BeNull();
        components[2].Metadata.CaptureError.Should().Contain("Metadata getter failed");
        var current = ResultAssertions.Success(await facade.GetManagementSnapshotAsync(scope.CancellationToken)).Channels.Single();
        current.Middlewares[0].Metadata.Json.Should().Contain("Changed metadata");
        current.Middlewares[1].InfoDictionary.Should().BeEmpty();
    }

    [Fact]
    public async Task ResolveMiddleware_WhenNamesRepeatAndDebuggerIsDerived_ShouldUseExactOwnerKeys()
    {
        var first = new FirstNamespace.SameName();
        var second = new SecondNamespace.SameName();
        var derived = new DerivedDebugger();
        var scenario = new DataChannelScenario();
        scenario.Primary.Middlewares.AddRange([first, second, derived]);
        await using var application = await new DataChannelTestApplicationFactory(scenario).CreateAsync(cancellationToken: TestContext.Current.CancellationToken);
        await using var scope = application.CreateScope(TestContext.Current.CancellationToken);
        var facade = scope.Resolve<DataChannelFacade>();
        var components = ResultAssertions.Success(await facade.GetChannelsStatusAsync(scope.CancellationToken)).Single().Middlewares;
        components.Select(component => component.ComponentKey).Should().OnlyHaveUniqueItems();
        components.Should().OnlyContain(component => component.CanDebugMessages);
        components.Select(component => component.Ordinal).Should().Equal(0, 1, 2);
        components.Should().OnlyContain(component => component.Stage == PipelineComponentStage.Transform);
        ResultAssertions.Success(await facade.GetMiddlewareByKeyAsync<MessageDebuggerMiddleware>(scenario.Primary.Id, components[0].ComponentKey))
            .Should().BeSameAs(first);
        ResultAssertions.Success(await facade.GetMiddlewareByKeyAsync<MessageDebuggerMiddleware>(scenario.Primary.Id, components[1].ComponentKey))
            .Should().BeSameAs(second);
        ResultAssertions.Success(await facade.GetMiddlewareByKeyAsync<MessageDebuggerMiddleware>(scenario.Primary.Id, components[2].ComponentKey))
            .Should().BeSameAs(derived);
        var ambiguous = await facade.GetMiddlewareAsync<MessageDebuggerMiddleware>(scenario.Primary.Id, nameof(FirstNamespace.SameName));
        ambiguous.Status.Should().Be(ResultStatus.BadRequest);
        ambiguous.Message.Should().Contain("ambiguous");
        ambiguous.Data.Should().BeNull();
    }

    [Fact]
    public async Task Capture_WhenComponentsAreReplacedAppendedOrRemoved_ShouldReflectCurrentSlotsAndRejectRetiredKeys()
    {
        var original = new DerivedDebugger();
        var scenario = new DataChannelScenario();
        scenario.Primary.Middlewares.Add(original);
        await using var application = await new DataChannelTestApplicationFactory(scenario).CreateAsync(cancellationToken: TestContext.Current.CancellationToken);
        await using var scope = application.CreateScope(TestContext.Current.CancellationToken);
        var facade = scope.Resolve<DataChannelFacade>();
        var channel = scope.Resolve<IDataChannelManager>().Fetch(scenario.Primary.Id)!;
        var before = ResultAssertions.Success(await facade.GetChannelsStatusAsync(scope.CancellationToken)).Single();
        var replacement = new DerivedDebugger();
        var appended = new DerivedDebugger();
        channel.Pipe.TransformMiddlewares[0] = replacement;
        channel.Pipe.TransformMiddlewares.Add(appended);
        channel.Pipe.OuterEndpoint = new ScenarioOuterEndpoint(scenario);
        var changed = ResultAssertions.Success(await facade.GetChannelsStatusAsync(scope.CancellationToken)).Single();
        changed.Middlewares.Should().HaveCount(2);
        changed.Middlewares.Select(component => component.Ordinal).Should().Equal(0, 1);
        changed.Middlewares[0].ComponentKey.Should().NotBe(before.Middlewares[0].ComponentKey);
        changed.OuterEndpoint.ComponentKey.Should().NotBe(before.OuterEndpoint.ComponentKey);
        changed.InnerEndpoint.ComponentKey.Should().Be(before.InnerEndpoint.ComponentKey);
        ResultAssertions.Success(await facade.GetMiddlewareByKeyAsync<MessageDebuggerMiddleware>(channel.Id, changed.Middlewares[0].ComponentKey))
            .Should().BeSameAs(replacement);
        ResultAssertions.Success(await facade.GetMiddlewareByKeyAsync<MessageDebuggerMiddleware>(channel.Id, changed.Middlewares[1].ComponentKey))
            .Should().BeSameAs(appended);
        var retired = await facade.GetMiddlewareByKeyAsync<MessageDebuggerMiddleware>(channel.Id, before.Middlewares[0].ComponentKey);
        retired.Status.Should().Be(ResultStatus.BadRequest);
        retired.Message.Should().Contain("unavailable");
        retired.Data.Should().BeNull();

        channel.Pipe.TransformMiddlewares.RemoveAt(1);
        var removed = ResultAssertions.Success(await facade.GetChannelsStatusAsync(scope.CancellationToken)).Single();
        removed.Middlewares.Should().HaveCount(1);
        removed.Middlewares[0].ComponentKey.Should().Be(changed.Middlewares[0].ComponentKey);
        var removedResult = await facade.GetMiddlewareByKeyAsync<MessageDebuggerMiddleware>(channel.Id, changed.Middlewares[1].ComponentKey);
        removedResult.Status.Should().Be(ResultStatus.BadRequest);
        removedResult.Message.Should().Contain("unavailable");
        removedResult.Data.Should().BeNull();
    }

    [Fact]
    public async Task Capture_WhenTheHostHasNoChannelsOrCallerCancels_ShouldReturnAnEmptyCaptureOrPropagateCancellation()
    {
        var scenario = new DataChannelScenario();
        scenario.Channels.Clear();
        await using var application = await new DataChannelTestApplicationFactory(scenario).CreateAsync(cancellationToken: TestContext.Current.CancellationToken);
        await using var scope = application.CreateScope(TestContext.Current.CancellationToken);
        var facade = scope.Resolve<DataChannelFacade>();
        var empty = ResultAssertions.Success(await facade.GetManagementSnapshotAsync(scope.CancellationToken));
        empty.Channels.Should().BeEmpty();
        empty.ExceptionSummary.TotalChannels.Should().Be(0);
        empty.ExceptionSummary.TotalHistoricalExceptions.Should().Be(0);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(scope.CancellationToken);
        cancellation.Cancel();
        var read = () => facade.GetManagementSnapshotAsync(cancellation.Token);
        await read.Should().ThrowAsync<OperationCanceledException>();
        var missing = await facade.GetChannelExceptionsAsync("missing", cancellationToken: scope.CancellationToken);
        missing.Status.Should().Be(ResultStatus.BadRequest);
        missing.Message.Should().Contain("was not found");
        missing.Data.Should().BeNull();
    }
}
