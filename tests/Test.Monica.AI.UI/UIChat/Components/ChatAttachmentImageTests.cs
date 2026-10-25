using AwesomeAssertions;
using Bunit;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Localization;
using Microsoft.JSInterop;
using Monica.AI.Chat.Abstractions;
using Monica.AI.Chat.Facades;
using Monica.AI.Chat.Models;
using Monica.AI.UI.Localization;
using Monica.AI.UI.UIChat.Components;
using Monica.Testing.Localization;
using MudBlazor.Services;

namespace Test.Monica.AI.UI.UIChat.Components;

public sealed class ChatAttachmentImageTests : BunitContext
{
    private readonly AttachmentStore _store = new();
    private static readonly ChatAttachmentReference IMAGE = new() { Id = "image", FileName = "photo.png", MediaType = "image/png", Kind = ChatAttachmentKind.Image };

    public ChatAttachmentImageTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        var module = JSInterop.SetupModule("./_content/Monica.AI.UI/UIChat/Components/ChatAttachmentImage.razor.js");
        module.SetupModule("observeThumbnail", _ => true);
        Services.AddMudServices();
        Services.AddOptions<global::Monica.Modules.ModuleShellUIOption>();
        Services.AddOptions<global::Monica.Modules.ModuleClockOption>();
        Services.AddSingleton<global::Monica.UI.Shared.Services.MoTimeFormatter>();
        Services.AddSingleton<IStringLocalizer<AIResource>, EchoStringLocalizer<AIResource>>();
        Services.AddSingleton(new ChatAttachmentFacade(_store, new PartitionResolver()));
    }

    [Fact]
    public async Task Image_ShouldReadOnlyAfterVisibilityAndRetainItAcrossStreamingRenders()
    {
        var view = Render<ChatAttachmentImage>(parameters => parameters.Add(component => component.Attachment, IMAGE).Add(component => component.SessionId, "conversation"));
        _store.ReadCount.Should().Be(0);
        _store.Content.SetResult(new ChatAttachmentData(IMAGE, [1, 2, 3]));
        await view.InvokeAsync(view.Instance.LoadPreviewAsync);
        view.Render(parameters => parameters.Add(component => component.Attachment, IMAGE with { }).Add(component => component.SessionId, "conversation"));
        await view.InvokeAsync(view.Instance.LoadPreviewAsync);

        _store.ReadCount.Should().Be(1);
        _store.Scope.Should().Be(("trusted-partition", "conversation", "image"));
        view.Find("img").GetAttribute("src").Should().Be("data:image/png;base64,AQID");
    }

    [Fact]
    public async Task DisposalDuringRead_ShouldCancelAndDiscardLateImageBytes()
    {
        var view = Render<ChatAttachmentImage>(parameters => parameters.Add(component => component.Attachment, IMAGE).Add(component => component.SessionId, "conversation"));
        var read = view.InvokeAsync(view.Instance.LoadPreviewAsync);
        view.WaitForAssertion(() => _store.ReadCount.Should().Be(1));
        var disposal = view.Instance.DisposeAsync().AsTask();
        await Task.WhenAll(read, disposal);

        _store.Token.IsCancellationRequested.Should().BeTrue();
        view.FindAll("img").Should().BeEmpty();
        await view.Instance.LoadPreviewAsync();
        _store.ReadCount.Should().Be(1);
    }

    private sealed class PartitionResolver : IChatHistoryPartitionResolver
    {
        public ValueTask<ChatHistoryPartition> ResolveAsync(CancellationToken ct = default) => ValueTask.FromResult(new ChatHistoryPartition("trusted-partition"));
    }

    private sealed class AttachmentStore : IChatAttachmentStore
    {
        public TaskCompletionSource<ChatAttachmentData> Content { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int ReadCount { get; private set; }
        public (string Partition, string Session, string Attachment)? Scope { get; private set; }
        public CancellationToken Token { get; private set; }
        public Task<ChatAttachmentData> ReadAsync(ChatHistoryPartition partition, string sessionId, string attachmentId, CancellationToken ct = default)
        {
            ReadCount++;
            Scope = (partition.Key, sessionId, attachmentId);
            Token = ct;
            return Content.Task.WaitAsync(ct);
        }
        public Task<ChatAttachmentReference> SaveAsync(ChatHistoryPartition partition, string sessionId, string fileName, string mediaType, Stream content, CancellationToken ct = default) => throw new NotSupportedException();
        public Task DeleteAsync(ChatHistoryPartition partition, string sessionId, string attachmentId, CancellationToken ct = default) => throw new NotSupportedException();
    }

}
