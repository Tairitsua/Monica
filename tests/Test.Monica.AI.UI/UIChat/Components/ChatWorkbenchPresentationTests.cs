using AwesomeAssertions;
using Bunit;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Localization;
using Monica.AI.Chat.Models;
using Monica.AI.Models;
using Monica.AI.UI.Localization;
using Monica.AI.UI.UIChat.Components;
using Monica.AI.UI.UIChat.Support;
using Monica.Testing.Localization;
using MudBlazor.Services;

namespace Test.Monica.AI.UI.UIChat.Components;

public sealed class ChatWorkbenchPresentationTests : BunitContext
{
    public ChatWorkbenchPresentationTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddMudServices();
        Services.AddSingleton<IStringLocalizer<global::Monica.UI.Localization.SharedResource>, EchoStringLocalizer<global::Monica.UI.Localization.SharedResource>>();
        Services.AddOptions<global::Monica.Modules.ModuleShellUIOption>();
        Services.AddOptions<global::Monica.Modules.ModuleClockOption>();
        Services.AddSingleton<global::Monica.UI.Shared.Services.MoTimeFormatter>();
        Services.AddSingleton<IStringLocalizer<AIResource>, EchoStringLocalizer<AIResource>>();
        _ = Render<MudBlazor.MudPopoverProvider>();
    }

    [Fact]
    public void Context_WhenCapacityIsUnspecified_ShouldUseThePlanningDefault()
    {
        var view = Render<ChatContextIndicator>(parameters => parameters
            .Add(component => component.Usage, new ChatContextUsage { EstimatedNextInputTokens = 800 }));

        var usage = new ChatContextUsage { EstimatedNextInputTokens = 800 };
        var expectedPercent = (usage.OccupancyRatio ?? 0) * 100;
        double.Parse(view.Find("[role='progressbar']").GetAttribute("aria-valuenow")!,
            System.Globalization.CultureInfo.InvariantCulture).Should().BeApproximately(expectedPercent, 0.000001);
        expectedPercent.Should().BeLessThan(1);
    }

    [Fact]
    public void ContextActivator_ShouldOpenItsDetailsWithOneAccessibleControl()
    {
        var view = Render<ChatContextIndicator>();
        view.Find(".context-activator").Click();
        view.FindComponent<MudBlazor.MudPopover>().Instance.Open.Should().BeTrue();
        view.FindAll("[role='button'] [role='button'], button button").Should().BeEmpty();
    }

    [Fact]
    public void Process_ShouldPreserveRequestToolOrderAndCollapseAfterCompletion()
    {
        ChatExecutionStep[] steps =
        [
            Request("request-1", 1, "first-model"),
            new() { Id = "tool", Sequence = 2, Kind = ChatExecutionStepKind.Tool, Status = ChatExecutionStatus.Completed,
                Tool = new ChatToolExecution { CallId = "call", Name = "retrieve-knowledge" } },
            Request("request-2", 3, "last-model")
        ];
        var view = Render<ChatExecutionProcess>(parameters => parameters
            .Add(component => component.Steps, steps).Add(component => component.Running, true));

        var headings = view.FindAll(".step-heading").Select(element => element.TextContent).ToArray();
        headings[0].Should().Contain("first-model");
        headings[1].Should().Contain("retrieve-knowledge");
        headings[2].Should().Contain("last-model");

        view.Render(parameters => parameters.Add(component => component.Steps, steps).Add(component => component.Running, false));
        view.FindAll(".process-step").Should().BeEmpty();
    }

    [Fact]
    public void Metrics_WhenProviderOmitsUsage_ShouldDisplayUnavailable()
    {
        var view = Render<ChatExecutionMetrics>(parameters => parameters
            .Add(component => component.Steps, [Request("request", 1, "model")]));

        view.Markup.Should().Contain("—");
        view.Markup.Should().NotContain("0%");
    }

    [Fact]
    public void TimelineWindow_ShouldKeepFocusedIntervalsInsideRecordedExtent()
    {
        var viewport = new TrajectoryViewport();
        viewport.UpdateExtent(100);
        viewport.Focus(20, 40);
        viewport.Zoom(.5);
        viewport.Start.Should().Be(25);
        viewport.End.Should().Be(35);
        viewport.Pan(-10);
        viewport.Start.Should().Be(0);
        viewport.End.Should().Be(10);
        viewport.Pan(20);
        viewport.End.Should().Be(100);
        viewport.Start.Should().Be(90);
        viewport.Reset();
        viewport.Width.Should().Be(100);
    }

    private static ChatExecutionStep Request(string id, long sequence, string model) => new()
    {
        Id = id, Sequence = sequence, Kind = ChatExecutionStepKind.ModelRequest, Status = ChatExecutionStatus.Completed,
        Request = new ChatModelRequest
        {
            ProviderId = "provider", ModelName = model, Settings = new ChatSessionSettings("provider", model, null, null),
            Output = [new ChatContentPart { Kind = ChatContentKind.Reasoning, Text = "reasoning evidence" }]
        }
    };
}
