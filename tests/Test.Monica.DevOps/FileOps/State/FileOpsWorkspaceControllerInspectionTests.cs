using Monica.Core.Results;
using Monica.DevOps.FileOps.Exceptions;
using Monica.DevOps.FileOps.Models;
using Monica.DevOps.FileOps.State;
using Test.Monica.DevOps.FileOps.Support;
using Xunit;

namespace Test.Monica.DevOps.FileOps.State;

public sealed partial class FileOpsWorkspaceControllerTests
{
    [Theory]
    [InlineData(FileOpsDiscardReason.Reload)]
    [InlineData(FileOpsDiscardReason.CloseInspector)]
    [InlineData(FileOpsDiscardReason.Leave)]
    public async Task DirtyInspectionAction_WhenDiscardIsRefused_ShouldRetainTargetBaselineAndDraftWithoutReadingAgain(FileOpsDiscardReason reason)
    {
        var service = new ControlledFileOpsServices();
        await using var state = new FileOpsWorkspaceController(service.CreateFacade());
        await ObserveAsync(state.InitializeAsync());
        var entry = state.SortedEntries[0];
        await ObserveAsync(state.OpenEntryAsync(entry));
        state.SetDraft("retained dirty text");
        var baseline = state.BaselineText;
        FileOpsDiscardConfirmation refuse = (request, _) =>
        {
            Assert.Equal(reason, request.Reason);
            Assert.Equal(entry.FullPath, request.FullPath);
            return Task.FromResult(false);
        };

        var disposition = await (reason switch
        {
            FileOpsDiscardReason.Reload => state.ReloadAsync(refuse),
            FileOpsDiscardReason.CloseInspector => state.CloseInspectorAsync(refuse),
            FileOpsDiscardReason.Leave => state.ConfirmLeaveAsync(refuse),
            _ => throw new InvalidOperationException("Unexpected scenario")
        });

        Assert.Equal(FileOpsOperationDisposition.Cancelled, disposition);
        Assert.Equal(entry.FullPath, state.InspectedFile!.FullPath);
        Assert.Equal(baseline, state.BaselineText);
        Assert.Equal("retained dirty text", state.DraftText);
        Assert.True(state.IsDirty);
        Assert.Single(service.TextReads);
        Assert.Single(service.Browses);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ReloadOrOpenAsync_WhenNewerDraftAppearsDuringReplacementRead_ShouldRejectLateReplacementAndKeepCurrentTarget(bool reload)
    {
        var service = new ControlledFileOpsServices();
        var reading = Signal<FileOpsFileTextContent>();
        await using var state = new FileOpsWorkspaceController(service.CreateFacade());
        await ObserveAsync(state.InitializeAsync());
        var alpha = state.SortedEntries.Single(entry => entry.Name == "alpha.txt");
        var beta = state.SortedEntries.Single(entry => entry.Name == "beta.txt");
        await ObserveAsync(state.OpenEntryAsync(alpha));
        state.SetDraft("approved old draft");
        var baseline = state.BaselineText;
        service.ReadText = (_, _) => reading.Task;
        var replacing = reload ? state.ReloadAsync(AcceptDiscard) : state.OpenEntryAsync(beta, AcceptDiscard);
        try
        {
            Assert.False(replacing.IsCompleted);
            state.SetDraft("newer protected draft");
            reading.SetResult(ControlledFileOpsServices.TextSnapshot(reload ? alpha.FullPath : beta.FullPath, "late disk replacement"));
            Assert.Equal(FileOpsOperationDisposition.Cancelled, await ObserveAsync(replacing));
            Assert.Equal(alpha.FullPath, state.InspectedFile!.FullPath);
            Assert.Equal(baseline, state.BaselineText);
            Assert.Equal("newer protected draft", state.DraftText);
            Assert.True(state.IsDirty);
        }
        finally { reading.TrySetResult(ControlledFileOpsServices.TextSnapshot(alpha.FullPath)); }
    }

    [Fact]
    public async Task CloseInspectorAndLeaveAsync_WhenAffirmed_ShouldClearOnlyExplicitCloseAndRetainDraftForDeparture()
    {
        var service = new ControlledFileOpsServices();
        await using var state = new FileOpsWorkspaceController(service.CreateFacade());
        await ObserveAsync(state.InitializeAsync());
        var entry = state.SortedEntries[0];
        await ObserveAsync(state.OpenEntryAsync(entry));
        state.SetDraft("explicitly discarded draft");
        Assert.Equal(FileOpsOperationDisposition.Completed, await state.CloseInspectorAsync(AcceptDiscard));
        Assert.Null(state.InspectedFile);
        Assert.Null(state.InspectedEntry);
        Assert.Equal(string.Empty, state.DraftText);

        await ObserveAsync(state.OpenEntryAsync(entry));
        state.SetDraft("draft retained until route departure");
        Assert.Equal(FileOpsOperationDisposition.Completed, await state.ConfirmLeaveAsync(AcceptDiscard));
        Assert.Equal(entry.FullPath, state.InspectedFile!.FullPath);
        Assert.Equal("draft retained until route departure", state.DraftText);
        Assert.True(state.IsDirty);
        Assert.Equal(2, service.TextReads.Count);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task OpenEntryAsync_WhenFirstPreviewIsBinaryOrTooLarge_ShouldKeepExactFailedTargetAndDetachedAttemptEvidence(bool binary)
    {
        var service = new ControlledFileOpsServices();
        service.ReadText = (path, _) => Task.FromException<FileOpsFileTextContent>(binary
            ? FileOpsOperationException.BinaryFilePreviewUnsupported(path)
            : FileOpsOperationException.TextFileTooLarge(path, "4 KiB"));
        await using var state = new FileOpsWorkspaceController(service.CreateFacade());
        await ObserveAsync(state.InitializeAsync());
        var entry = state.SortedEntries[0];
        var path = entry.FullPath;

        await ObserveAsync(state.OpenEntryAsync(entry));

        var attempted = Assert.IsType<FileOpsInspectionAttempt>(state.InspectionAttempt);
        Assert.Equal(path, attempted.FullPath);
        Assert.Equal(Path.GetFileName(path), attempted.Name);
        var attemptedEntry = Assert.IsType<FileOpsEntrySummary>(attempted.Entry);
        Assert.Equal(path, attemptedEntry.FullPath);
        Assert.Equal(path, state.InspectedEntry!.FullPath);
        Assert.Equal(ResultStatus.BadRequest, attempted.Outcome.Status);
        Assert.False(attempted.Outcome.Succeeded);
        Assert.Contains(binary ? "BinaryFilePreviewUnsupported" : "TextFileTooLarge", attempted.Outcome.Message);
        Assert.Null(state.InspectedFile);
        Assert.Equal(string.Empty, state.DraftText);
        entry.FullPath = Path.Combine(ControlledFileOpsServices.Root, "caller-mutated.txt");
        attemptedEntry.FullPath = Path.Combine(ControlledFileOpsServices.Root, "attempt-mutated.txt");
        Assert.Equal(path, state.InspectionAttempt!.Entry!.FullPath);
        Assert.Equal(path, state.InspectedEntry!.FullPath);
    }

    [Fact]
    public async Task OpenEntryAsync_WhenReplacementPreviewFails_ShouldRetainPriorDirtyEditorAndDescribeFailedNewTargetSeparately()
    {
        var service = new ControlledFileOpsServices();
        await using var state = new FileOpsWorkspaceController(service.CreateFacade());
        await ObserveAsync(state.InitializeAsync());
        var alpha = state.SortedEntries.Single(entry => entry.Name == "alpha.txt");
        var beta = state.SortedEntries.Single(entry => entry.Name == "beta.txt");
        await ObserveAsync(state.OpenEntryAsync(alpha));
        var baseline = state.BaselineText;
        state.SetDraft("dirty alpha draft");
        service.ReadText = (path, _) => Task.FromException<FileOpsFileTextContent>(FileOpsOperationException.BinaryFilePreviewUnsupported(path));

        await ObserveAsync(state.OpenEntryAsync(beta, AcceptDiscard));

        Assert.Equal(alpha.FullPath, state.InspectedFile!.FullPath);
        Assert.Equal(alpha.FullPath, state.InspectedEntry!.FullPath);
        Assert.Equal(baseline, state.BaselineText);
        Assert.Equal("dirty alpha draft", state.DraftText);
        Assert.True(state.IsDirty);
        Assert.Equal(beta.FullPath, state.InspectionAttempt!.FullPath);
        Assert.False(state.InspectionAttempt!.Outcome.Succeeded);
        await ObserveAsync(state.OpenEntryAsync(alpha));
        Assert.Null(state.InspectionAttempt);
        Assert.Equal("dirty alpha draft", state.DraftText);
        Assert.Equal(2, service.TextReads.Count);
    }

    [Fact]
    public async Task OpenTextPathAsync_WhenPathIsOutsideDisplayedSubset_ShouldAcceptRealTextEvidenceWithoutSyntheticRow()
    {
        var service = new ControlledFileOpsServices();
        await using var state = new FileOpsWorkspaceController(service.CreateFacade());
        await ObserveAsync(state.InitializeAsync());
        var path = Path.Combine(ControlledFileOpsServices.Root, "out-of-cap.txt");
        service.ReadText = (target, _) => Task.FromResult(ControlledFileOpsServices.TextSnapshot(target, "real restored content", 25, 3));

        await ObserveAsync(state.OpenTextPathAsync(path));

        Assert.Equal(path, Assert.Single(service.TextReads).Path);
        Assert.Null(state.InspectedEntry);
        Assert.Null(state.InspectionAttempt);
        Assert.Equal(path, state.InspectedFile!.FullPath);
        Assert.Equal(25, state.InspectedFile!.SizeBytes);
        Assert.Equal(3, state.InspectedFile!.LineCount);
        Assert.Equal("real restored content", state.DraftText);
        Assert.DoesNotContain(state.SortedEntries, entry => entry.FullPath == path);
    }

    [Fact]
    public async Task OpenTextPathAsync_WhenUndisplayedPreviewFails_ShouldRetainExactAttemptPathAndNameWithoutFabricatedMetadata()
    {
        var service = new ControlledFileOpsServices();
        await using var state = new FileOpsWorkspaceController(service.CreateFacade());
        await ObserveAsync(state.InitializeAsync());
        var path = Path.Combine(ControlledFileOpsServices.Root, "undisplayed-binary.bin");
        service.ReadText = (target, _) => Task.FromException<FileOpsFileTextContent>(FileOpsOperationException.BinaryFilePreviewUnsupported(target));

        await ObserveAsync(state.OpenTextPathAsync(path));

        var attempted = Assert.IsType<FileOpsInspectionAttempt>(state.InspectionAttempt);
        Assert.Equal(path, attempted.FullPath);
        Assert.Equal("undisplayed-binary.bin", attempted.Name);
        Assert.Null(attempted.Entry);
        Assert.Null(state.InspectedEntry);
        Assert.Null(state.InspectedFile);
        Assert.False(attempted.Outcome.Succeeded);
        Assert.Equal(ResultStatus.BadRequest, attempted.Outcome.Status);
        Assert.Equal(path, Assert.Single(service.TextReads).Path);
    }
}
