using System.Text;
using Monica.Core.Results;
using Monica.DevOps.FileOps.State;
using Test.Monica.DevOps.FileOps.Support;
using Xunit;

namespace Test.Monica.DevOps.FileOps.State;

public sealed class FileOpsUploadDraftProtectionTests
{
    private static readonly TimeSpan ObservationTimeout = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task UploadAsync_WhenWhitespaceAliasDiscardIsCanceled_ShouldPreserveDiskAndDraftWithoutOpeningInput()
    {
        using var fixture = new FileOpsFilesystemFixture();
        var target = Path.Combine(fixture.Root, "notes.txt");
        byte[] original = Encoding.UTF8.GetBytes("Original disk text.");
        await File.WriteAllBytesAsync(target, original, TestContext.Current.CancellationToken);
        await using var state = new FileOpsWorkspaceController(fixture.Facade);
        await ObserveAsync(state.InitializeAsync());
        await ObserveAsync(state.OpenEntryAsync(Assert.Single(state.SortedEntries)));
        state.SetDraft("Unsaved notes.");
        var revision = state.DraftRevision;
        var opens = 0;
        FileOpsDiscardRequest? confirmation = null;

        var disposition = await ObserveAsync(state.UploadAsync(" notes.txt ", 20, (_, _) =>
        {
            opens++;
            throw new InvalidOperationException("A canceled overwrite must not open its input.");
        }, (request, _) =>
        {
            confirmation = request;
            return Task.FromResult(false);
        }));

        Assert.Equal(FileOpsOperationDisposition.Cancelled, disposition);
        Assert.Equal(0, opens);
        Assert.NotNull(confirmation);
        Assert.Equal(FileOpsDiscardReason.UploadReplacement, confirmation.Reason);
        Assert.Equal(target, confirmation.FullPath);
        Assert.Equal(target, confirmation.DestinationPath);
        Assert.Equal(revision, confirmation.DraftRevision);
        Assert.Equal(original, await File.ReadAllBytesAsync(target, TestContext.Current.CancellationToken));
        Assert.Equal(target, state.InspectedFile!.FullPath);
        Assert.Equal("Unsaved notes.", state.DraftText);
        Assert.Equal(revision, state.DraftRevision);
        Assert.True(state.IsDirty);
        Assert.Null(state.LastCommand);
        Assert.False(state.IsBusy);
    }

    [Fact]
    public async Task UploadAsync_WhenWhitespaceAliasDiscardIsAffirmed_ShouldConfirmAndOverwriteTheExactNormalizedTarget()
    {
        using var fixture = new FileOpsFilesystemFixture();
        var target = Path.Combine(fixture.Root, "notes.txt");
        await File.WriteAllTextAsync(target, "Original disk text.", TestContext.Current.CancellationToken);
        await using var state = new FileOpsWorkspaceController(fixture.Facade);
        await ObserveAsync(state.InitializeAsync());
        await ObserveAsync(state.OpenEntryAsync(Assert.Single(state.SortedEntries)));
        state.SetDraft("Unsaved notes.");
        byte[] replacement = Encoding.UTF8.GetBytes("Uploaded replacement text.");
        var input = new TrackingUploadStream(replacement);
        var opens = 0;
        FileOpsDiscardRequest? confirmation = null;

        var disposition = await ObserveAsync(state.UploadAsync(" notes.txt ", replacement.LongLength, (_, token) =>
        {
            opens++;
            Assert.False(token.CanBeCanceled);
            return Task.FromResult<Stream>(input);
        }, (request, _) =>
        {
            confirmation = request;
            return Task.FromResult(true);
        }));

        Assert.Equal(FileOpsOperationDisposition.Completed, disposition);
        Assert.Equal(target, confirmation!.FullPath);
        Assert.Equal(target, confirmation.DestinationPath);
        Assert.Equal(1, opens);
        Assert.Equal(1, input.DisposeCount);
        Assert.False(input.CanRead);
        Assert.Equal(replacement, await File.ReadAllBytesAsync(target, TestContext.Current.CancellationToken));
        Assert.Equal(target, Assert.Single(state.SortedEntries).FullPath);
        Assert.Equal(target, state.InspectedFile!.FullPath);
        Assert.Equal("Uploaded replacement text.", state.DraftText);
        Assert.Equal(state.DraftText, state.BaselineText);
        Assert.False(state.IsDirty);
        Assert.False(state.MetadataStale);
        Assert.True(state.LastCommand!.Succeeded);
        Assert.Equal(FileOpsOperationKind.Upload, state.LastCommand.Kind);
        Assert.Equal(ResultStatus.Ok, state.LastCommand.Status);
        Assert.True(state.LastRead!.Succeeded);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UploadAsync_WhenDraftChangesDuringInputOpening_ShouldRejectStaleApprovalAndReleaseInputOnce(bool returnsToApprovedText)
    {
        using var fixture = new FileOpsFilesystemFixture();
        var target = Path.Combine(fixture.Root, "notes.txt");
        byte[] original = Encoding.UTF8.GetBytes("Original disk text.");
        await File.WriteAllBytesAsync(target, original, TestContext.Current.CancellationToken);
        await using var state = new FileOpsWorkspaceController(fixture.Facade);
        await ObserveAsync(state.InitializeAsync());
        await ObserveAsync(state.OpenEntryAsync(Assert.Single(state.SortedEntries)));
        state.SetDraft("Approved draft.");
        var approvedRevision = state.DraftRevision;
        var opening = new TaskCompletionSource<Stream>(TaskCreationOptions.RunContinuationsAsynchronously);
        byte[] replacement = Encoding.UTF8.GetBytes("Uploaded replacement text.");
        var input = new TrackingUploadStream(replacement);
        var opens = 0;
        var upload = state.UploadAsync(" notes.txt ", replacement.LongLength, (_, _) =>
        {
            opens++;
            return opening.Task;
        }, (request, _) =>
        {
            Assert.Equal(target, request.DestinationPath);
            Assert.Equal(approvedRevision, request.DraftRevision);
            return Task.FromResult(true);
        });

        try
        {
            Assert.False(upload.IsCompleted);
            Assert.Equal(1, opens);
            state.SetDraft("Newer draft.");
            if (returnsToApprovedText) state.SetDraft("Approved draft.");
            var currentDraft = state.DraftText;
            var currentRevision = state.DraftRevision;
            Assert.True(currentRevision > approvedRevision);
            opening.SetResult(input);

            Assert.Equal(FileOpsOperationDisposition.Cancelled, await ObserveAsync(upload));

            Assert.Equal(1, input.DisposeCount);
            Assert.False(input.CanRead);
            Assert.Equal(original, await File.ReadAllBytesAsync(target, TestContext.Current.CancellationToken));
            Assert.Equal(target, state.InspectedFile!.FullPath);
            Assert.Equal(currentDraft, state.DraftText);
            Assert.Equal(currentRevision, state.DraftRevision);
            Assert.Equal("Original disk text.", state.BaselineText);
            Assert.True(state.IsDirty);
            Assert.Null(state.LastCommand);
            Assert.False(state.IsBusy);
        }
        finally
        {
            opening.TrySetResult(input);
            await ObserveAsync(upload);
        }
    }

    private static Task<T> ObserveAsync<T>(Task<T> task) =>
        task.WaitAsync(ObservationTimeout, TestContext.Current.CancellationToken);

    private sealed class TrackingUploadStream(byte[] content) : MemoryStream(content)
    {
        public int DisposeCount { get; private set; }

        protected override void Dispose(bool disposing)
        {
            if (disposing) DisposeCount++;
            base.Dispose(disposing);
        }
    }
}
