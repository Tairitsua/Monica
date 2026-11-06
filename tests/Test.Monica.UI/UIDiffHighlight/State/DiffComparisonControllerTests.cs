using AwesomeAssertions;
using Monica.Core.Results;
using Monica.UI.UIDiffHighlight.Models;
using Monica.UI.UIDiffHighlight.State;
using Xunit;

namespace Test.Monica.UI.UIDiffHighlight.State;

public sealed class DiffComparisonControllerTests
{
    [Fact]
    public void Capture_WhenInputsContainSeparatorsOrOptionsMutate_ShouldKeepDetachedFieldIdentity()
    {
        var options = new DiffHighlightOptions { Style = new() { AddedLineStyle = "original" } };
        var first = DiffComparisonInput.Capture("a\u001fb", "c", options);
        var second = DiffComparisonInput.Capture("a", "b\u001fc", options);
        first.Should().NotBe(second);

        options.Mode = EDiffHighlightMode.Mixed;
        options.Style.AddedLineStyle = "changed";
        first.Options!.Mode.Should().Be(EDiffHighlightMode.Line);
        first.Options.Style!.AddedLineStyle.Should().Be("original");
        var request = first.Options.ToOptions();
        request.Style!.AddedLineStyle = "request mutation";
        first.Options.ToOptions().Style!.AddedLineStyle.Should().Be("original");
        DiffComparisonInput.Capture(null, null, null).Should().Be(new DiffComparisonInput("", "", null));
    }

    [Fact]
    public async Task RequestAsync_WhenDraftChangesDuringWork_ShouldSerializeAndCompareOnlyLatestPendingDraft()
    {
        var firstResult = NewCompletion<Result<DiffHighlightResult>>();
        var latestResult = NewCompletion<Result<DiffHighlightResult>>();
        var latestStarted = NewCompletion();
        var calls = new List<DiffComparisonInput>();
        await using var controller = new DiffComparisonController(input =>
        {
            calls.Add(input);
            if (calls.Count == 1) return firstResult.Task;
            latestStarted.SetResult();
            return latestResult.Task;
        });

        controller.SetDraft(Input("first"));
        var firstWait = controller.RequestAsync();
        controller.SetDraft(Input("superseded"));
        var skippedWait = controller.RequestAsync();
        controller.SetDraft(Input("latest"));
        var latestWait = controller.RequestAsync();
        calls.Select(input => input.NewText).Should().Equal("first");
        controller.State.HasPendingRequest.Should().BeTrue();

        firstResult.SetResult(Result.Success(ComparisonResult("first")));
        await latestStarted.Task;
        calls.Select(input => input.NewText).Should().Equal("first", "latest");
        controller.State.Accepted.Should().BeNull();
        firstWait.IsCompleted.Should().BeFalse();
        skippedWait.IsCompleted.Should().BeFalse();
        latestWait.IsCompleted.Should().BeFalse();

        latestResult.SetResult(Result.Success(ComparisonResult("latest")));
        await Task.WhenAll(firstWait, skippedWait, latestWait);
        controller.State.IsBusy.Should().BeFalse();
        controller.State.IsAcceptedCurrent.Should().BeTrue();
        controller.State.Accepted!.CreateResult().HighlightedContent.Should().Be("latest");
    }

    [Fact]
    public async Task SetDraft_WhenAutomaticWorkIsNotRequested_ShouldRejectOldCompletionAndRetainPriorEvidence()
    {
        var running = NewCompletion<Result<DiffHighlightResult>>();
        var calls = 0;
        await using var controller = new DiffComparisonController(_ => ++calls == 1
            ? Task.FromResult(Result.Success(ComparisonResult("accepted"))) : running.Task);
        controller.SetDraft(Input("accepted"));
        await controller.RequestAsync();
        var accepted = controller.State.Accepted;

        controller.SetDraft(Input("running"));
        var wait = controller.RequestAsync();
        controller.SetDraft(Input("manual draft"));
        running.SetResult(Result.Success(ComparisonResult("running")));
        await wait;

        controller.State.Accepted.Should().BeSameAs(accepted);
        controller.State.IsAcceptedCurrent.Should().BeFalse();
        controller.State.IsBusy.Should().BeFalse();
        calls.Should().Be(2);
        controller.SetDraft(Input("accepted"));
        controller.State.IsAcceptedCurrent.Should().BeTrue();
    }

    [Fact]
    public async Task RequestAsync_WhenCurrentComparisonFails_ShouldKeepWholePriorEvidenceAndIdentifyFailureScope()
    {
        var calls = 0;
        await using var controller = new DiffComparisonController(_ => Task.FromResult<Result<DiffHighlightResult>>(
            ++calls == 1 ? Result.Success(ComparisonResult("accepted")) : Result.Failure<DiffHighlightResult>("comparison failed")));
        controller.SetDraft(Input("accepted"));
        await controller.RequestAsync();
        var accepted = controller.State.Accepted;

        controller.SetDraft(Input("failed"));
        await controller.RequestAsync();
        controller.State.Accepted.Should().BeSameAs(accepted);
        controller.State.IsAcceptedCurrent.Should().BeFalse();
        controller.State.IsFailureCurrent.Should().BeTrue();
        controller.State.Failure!.Message.Should().Be("comparison failed");

        controller.SetDraft(Input("different"));
        controller.State.IsFailureCurrent.Should().BeFalse();
        controller.SetDraft(Input("accepted"));
        controller.State.IsAcceptedCurrent.Should().BeTrue();
    }

    [Fact]
    public async Task RequestAsync_WhenIdenticalRequestIsAlreadyActive_ShouldJoinActualWork()
    {
        var completion = NewCompletion<Result<DiffHighlightResult>>();
        var calls = 0;
        await using var controller = new DiffComparisonController(_ => { calls++; return completion.Task; });
        controller.SetDraft(Input("same"));
        var first = controller.RequestAsync();
        controller.SetDraft(Input("same")).Should().BeFalse();
        var second = controller.RequestAsync();
        first.Should().BeSameAs(second);
        calls.Should().Be(1);

        completion.SetResult(Result.Success(ComparisonResult("same")));
        await Task.WhenAll(first, second);
        controller.State.IsAcceptedCurrent.Should().BeTrue();
    }

    [Fact]
    public async Task ClearEvidence_WhenWorkIsActive_ShouldDrainWithoutRepopulatingTheSkippedEmptyView()
    {
        var completion = NewCompletion<Result<DiffHighlightResult>>();
        await using var controller = new DiffComparisonController(_ => completion.Task);
        controller.SetDraft(Input("active"));
        var wait = controller.RequestAsync();
        controller.SetDraft(DiffComparisonInput.Capture("", "", null));
        controller.ClearEvidence();
        completion.SetResult(Result.Success(ComparisonResult("active")));
        await wait;

        controller.State.Accepted.Should().BeNull();
        controller.State.Failure.Should().BeNull();
        controller.State.IsBusy.Should().BeFalse();
    }

    [Fact]
    public async Task DisposeAsync_WhenRealWorkIsActive_ShouldStopAdmissionAndAwaitRetirementWithoutAcceptance()
    {
        var completion = NewCompletion<Result<DiffHighlightResult>>();
        var calls = 0;
        var controller = new DiffComparisonController(_ => { calls++; return completion.Task; });
        controller.SetDraft(Input("active"));
        var wait = controller.RequestAsync();
        controller.SetDraft(Input("queued"));
        var queued = controller.RequestAsync();
        var retirement = controller.DisposeAsync().AsTask();
        retirement.IsCompleted.Should().BeFalse();
        controller.State.IsRetired.Should().BeTrue();
        controller.State.HasPendingRequest.Should().BeFalse();
        controller.SetDraft(Input("retired")).Should().BeFalse();
        await controller.RequestAsync();

        completion.SetResult(Result.Success(ComparisonResult("active")));
        await Task.WhenAll(wait, queued, retirement);
        controller.State.Accepted.Should().BeNull();
        calls.Should().Be(1);
        await controller.DisposeAsync();
    }

    [Fact]
    public async Task RequestAsync_WhenDelegateThrows_ShouldPublishFailureAndAllowAnExplicitRetry()
    {
        var calls = 0;
        await using var controller = new DiffComparisonController(_ => ++calls == 1
            ? throw new InvalidOperationException("delegate failed") : Task.FromResult(Result.Success(ComparisonResult("retried"))));
        controller.SetDraft(Input("retried"));
        await controller.RequestAsync();
        controller.State.Failure!.Message.Should().Be("delegate failed");
        controller.State.IsBusy.Should().BeFalse();
        await controller.RequestAsync();
        controller.State.Failure.Should().BeNull();
        controller.State.IsAcceptedCurrent.Should().BeTrue();
    }

    [Fact]
    public async Task Accepted_WhenBackendAndRenderingObjectsMutate_ShouldKeepIndependentCompleteProjections()
    {
        var backend = ComparisonResult("source");
        backend.Options.Style = new() { AddedLineStyle = "source style" };
        await using var controller = new DiffComparisonController(_ => Task.FromResult(Result.Success(backend)));
        controller.SetDraft(Input("source"));
        await controller.RequestAsync();

        backend.HighlightedContent = "mutated backend";
        backend.Options.Style.AddedLineStyle = "mutated style";
        backend.Statistics.TotalChanges = 99;
        backend.Lines[0].NewContent = "mutated line";
        backend.Lines[0].CharacterDiffs![0].Content = "mutated range";
        var first = controller.State.Accepted!.CreateResult();
        first.HighlightedContent.Should().Be("source");
        first.Options.Style!.AddedLineStyle.Should().Be("source style");
        first.Statistics.TotalChanges.Should().Be(1);
        first.Lines[0].NewContent.Should().Be("source");
        first.Lines[0].CharacterDiffs![0].Content.Should().Be("source");

        first.Options.Style.AddedLineStyle = "reader mutation";
        first.Lines.Clear();
        var second = controller.State.Accepted.CreateResult();
        second.Options.Style!.AddedLineStyle.Should().Be("source style");
        second.Lines.Should().ContainSingle();
    }

    private static DiffComparisonInput Input(string updated) => DiffComparisonInput.Capture("original", updated, new());
    private static TaskCompletionSource NewCompletion() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static TaskCompletionSource<T> NewCompletion<T>() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static DiffHighlightResult ComparisonResult(string updated) => new()
    {
        HighlightedContent = updated,
        Statistics = new() { TotalChanges = 1, AddedLines = 1, TotalNewLines = 1 },
        Lines = [new() { Type = EDiffLineType.Added, NewLineNumber = 1, NewContent = updated,
            CharacterDiffs = [new() { Type = EDiffLineType.Added, Start = 0, Length = updated.Length, Content = updated }] }]
    };
}
