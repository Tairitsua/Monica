using AwesomeAssertions;
using Bunit;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Localization;
using Monica.AI.Chat.Models;
using Monica.AI.Models;
using Monica.AI.UI.Localization;
using Monica.AI.UI.UIChat.Components;
using Monica.Testing.Localization;
using MudBlazor;
using MudBlazor.Services;

namespace Test.Monica.AI.UI.UIChat.Components;

public sealed class SessionListTests : BunitContext
{
    public SessionListTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddMudServices();
        Services.AddOptions<global::Monica.Modules.ModuleShellUIOption>();
        Services.AddOptions<global::Monica.Modules.ModuleClockOption>();
        Services.AddSingleton<global::Monica.UI.Shared.Services.MoTimeFormatter>();
        Services.AddSingleton<IStringLocalizer<AIResource>, EchoStringLocalizer<AIResource>>();
        Render<MudPopoverProvider>();
    }

    [Fact]
    public void Render_WhenHistoryIsLoading_ShouldExposeBusyStatusWithoutEmptyState()
    {
        var component = Render<SessionList>(parameters => parameters
            .Add(item => item.IsLoading, true));

        component.Find(".session-list-content").GetAttribute("aria-busy").Should().Be("true");
        component.Find("[role='status']").TextContent.Should().Contain("Chat:History:Loading");
        component.Markup.Should().NotContain("Session:NoConversations");
    }

    [Fact]
    public void Render_WhenSessionExists_ShouldExposeNonDestructiveOrganizationActions()
    {
        var summary = new ChatSessionSummary
        {
            SessionId = "session-1",
            Title = "Conversation",
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
            Settings = new ChatSessionSettings("provider", "model", null, null)
        };

        var component = Render<SessionList>(parameters => parameters
            .Add(item => item.Sessions, [summary])
            .Add(item => item.CurrentSessionId, summary.SessionId));

        component.Find("button[aria-label='Session:Pin']").Should().NotBeNull();
        component.Find("button[aria-label='Session:Archive:Action']").Should().NotBeNull();
        component.Markup.Should().Contain("Session:Archive:Title").And.NotContain("Delete");
    }

    [Fact]
    public void SelectedConversation_ShouldStaySelectedWhenSavingMovesItToTheTop()
    {
        var first = new ChatSessionSummary { SessionId = "first", Title = "First conversation", Settings = new("provider") };
        var selected = new ChatSessionSummary { SessionId = "selected", Title = "Selected conversation", Settings = new("provider") };
        var view = Render<SessionList>(parameters => parameters
            .Add(component => component.Sessions, [first, selected])
            .Add(component => component.CurrentSessionId, selected.SessionId));

        view.Find(".selected .session-title").TextContent.Should().Be(selected.Title);

        view.Render(parameters => parameters.Add(component => component.Sessions, [selected, first]));

        view.FindAll(".selected").Should().ContainSingle()
            .Which.QuerySelector(".session-title")!.TextContent.Should().Be(selected.Title);
    }

    [Fact]
    public void PinAndArchiveActions_ShouldNotSelectConversation()
    {
        var selected = new List<string>();
        var pinned = new List<string>();
        var archived = new List<string>();
        var summary = new ChatSessionSummary { SessionId = "pinned", Title = "Pinned", IsPinned = true, Settings = new("provider") };
        var view = Render<SessionList>(parameters => parameters
            .Add(item => item.Sessions, [summary])
            .Add(item => item.OnSelectSession, selected.Add)
            .Add(item => item.OnPinSession, pinned.Add)
            .Add(item => item.OnArchiveSession, archived.Add));

        view.Find("button[aria-label='Session:Unpin']").Click();
        view.Find("button[aria-label='Session:Archive:Action']").Click();

        pinned.Should().Equal("pinned");
        archived.Should().Equal("pinned");
        selected.Should().BeEmpty();
    }
}
