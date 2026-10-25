using System.Reflection;
using AwesomeAssertions;
using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Monica.Core.Results;
using Monica.Modules;
using Monica.UI.Pages;
using Monica.UI.UISystemInfo.State;
using MudBlazor;
using Xunit;

namespace Test.Monica.UI.UISystemInfo.Pages;

public sealed class UISystemInfoPageTests
{
    [Fact]
    public async Task Render_WhenLinksAreConfigured_ShouldExposeEnabledDestinations()
    {
        var snapshot = SystemInfoTestData.Snapshot();
        var factory = new SystemInfoPageSessionFactory(SystemInfoTestData.Calls(
            () => Res.Ok(snapshot)));
        var options = Options.Create(new ModuleSystemInfoUIOption
        {
            CustomLinks =
            [
                SystemInfoTestData.Link("API console", "/swagger"),
                SystemInfoTestData.Link("Disabled", "/disabled", enabled: false)
            ]
        });
        await using var context = new SystemInfoUiTestContext(configureServices: services =>
        {
            services.AddSingleton(factory);
            services.AddSingleton<IOptions<ModuleSystemInfoUIOption>>(options);
        });

        var cut = context.Render<UISystemInfoPage>();

        var link = cut.Find("a[href='/swagger']");
        link.TextContent.Should().Contain("API console");
        link.GetAttribute("target").Should().Be("_blank");
        link.GetAttribute("rel").Should().Contain("noopener");
        cut.FindAll("a[href='/disabled']").Should().BeEmpty();
    }

    [Fact]
    public async Task Retry_WhenInitialCaptureFailed_ShouldReplaceErrorWithEvidence()
    {
        var callCount = 0;
        var factory = new SystemInfoPageSessionFactory(SystemInfoTestData.Calls(
            () => ++callCount == 1
                ? Res.Fail("initial capture failed")
                : Res.Ok(SystemInfoTestData.Snapshot())));
        await using var context = CreateContext(factory, new ModuleSystemInfoUIOption());

        var cut = context.Render<UISystemInfoPage>();

        cut.Find(".system-info-load-state[role='alert']").TextContent
            .Should().Contain("initial capture failed");
        cut.FindAll("button")
            .Single(button => button.TextContent.Contains("Page:Actions:Retry", StringComparison.Ordinal))
            .Click();
        cut.WaitForAssertion(() =>
        {
            cut.FindAll(".system-info-load-state").Should().BeEmpty();
            cut.FindAll(".system-info-hero").Should().ContainSingle();
        });
        callCount.Should().Be(2);
    }

    [Fact]
    public async Task Refresh_WhenCaptureFails_ShouldKeepExistingEvidenceVisible()
    {
        var snapshot = SystemInfoTestData.Snapshot();
        var callCount = 0;
        var factory = new SystemInfoPageSessionFactory(SystemInfoTestData.Calls(
            () => ++callCount == 1 ? Res.Ok(snapshot) : Res.Fail("refresh failed")));
        await using var context = CreateContext(factory, new ModuleSystemInfoUIOption());

        var cut = context.Render<UISystemInfoPage>();
        var originalIdentity = cut.Find("#system-info-service-name").TextContent;

        cut.FindAll("button")
            .Single(button => button.TextContent.Contains("Page:Actions:Refresh", StringComparison.Ordinal))
            .Click();

        cut.Find("#system-info-service-name").TextContent.Should().Be(originalIdentity);
        cut.FindAll(".system-info-load-state").Should().BeEmpty();
        callCount.Should().Be(2);
    }

    [Fact]
    public async Task Render_WhenEveryConfiguredLinkIsDisabled_ShouldOmitTheLinkPanel()
    {
        var factory = new SystemInfoPageSessionFactory(SystemInfoTestData.Calls(
            () => Res.Ok(SystemInfoTestData.Snapshot())));
        var options = new ModuleSystemInfoUIOption
        {
            CustomLinks =
            [
                SystemInfoTestData.Link("Disabled", "/disabled", enabled: false)
            ]
        };
        await using var context = CreateContext(factory, options);

        var cut = context.Render<UISystemInfoPage>();

        cut.FindAll(".system-info-links").Should().BeEmpty();
        cut.FindAll(".system-info-dossier").Should().ContainSingle();
    }

    [Fact]
    public async Task Refresh_WhenPageIsDisposedDuringCompletion_ShouldNotPublishLateFeedback()
    {
        var snapshot = SystemInfoTestData.Snapshot();
        var captureCount = 0;
        var factory = new SystemInfoPageSessionFactory(
            SystemInfoTestData.Calls(() =>
            {
                captureCount++;
                return Res.Ok(snapshot);
            }),
            clockInterval: TimeSpan.FromDays(1));
        await using var context = CreateContext(factory, new ModuleSystemInfoUIOption());
        var cut = context.Render<UISystemInfoPage>();
        var session = ReadSession(cut);
        var completionEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseCompletion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var notificationCount = 0;
        session.Changed += BlockRefreshCompletionAsync;

        var refreshTask = FindAction(cut, "Page:Actions:Refresh").ClickAsync();
        await completionEntered.Task.WaitAsync(Xunit.TestContext.Current.CancellationToken);

        try
        {
            await cut.InvokeAsync(() => cut.Instance.DisposeAsync().AsTask());
            cut.Dispose();
        }
        finally
        {
            releaseCompletion.TrySetResult();
        }

        await refreshTask.WaitAsync(Xunit.TestContext.Current.CancellationToken);

        cut.IsDisposed.Should().BeTrue();
        captureCount.Should().Be(2);
        context.Services.GetRequiredService<ISnackbar>().ShownSnackbars.Should().BeEmpty();

        Task BlockRefreshCompletionAsync()
        {
            if (Interlocked.Increment(ref notificationCount) != 2)
            {
                return Task.CompletedTask;
            }

            completionEntered.TrySetResult();
            return releaseCompletion.Task;
        }
    }

    [Fact]
    public async Task Restart_WhenPageIsDisposedBeforeConfirmation_ShouldIgnoreTheLateDialogResult()
    {
        var restartCount = 0;
        var factory = new SystemInfoPageSessionFactory(SystemInfoTestData.Calls(
            () => Res.Ok(SystemInfoTestData.Snapshot()),
            () =>
            {
                restartCount++;
                return Res.Ok();
            }));
        await using var context = CreateContext(factory, new ModuleSystemInfoUIOption
        {
            EnableSelfRestartAction = true
        });
        var cut = context.Render<UISystemInfoPage>();

        var restartTask = FindAction(cut, "Page:Actions:Restart").ClickAsync();
        await context.DialogProvider.WaitForAssertionAsync(() =>
            _ = FindAction(context.DialogProvider, "Page:Dialogs:Restart:Confirm"));

        await cut.InvokeAsync(() => cut.Instance.DisposeAsync().AsTask());
        cut.Dispose();
        await FindAction(context.DialogProvider, "Page:Dialogs:Restart:Confirm").ClickAsync();
        await restartTask.WaitAsync(Xunit.TestContext.Current.CancellationToken);

        cut.IsDisposed.Should().BeTrue();
        restartCount.Should().Be(0);
        context.Services.GetRequiredService<ISnackbar>().ShownSnackbars.Should().BeEmpty();
    }

    private static SystemInfoUiTestContext CreateContext(
        SystemInfoPageSessionFactory factory,
        ModuleSystemInfoUIOption options) => new(configureServices: services =>
    {
        services.AddSingleton(factory);
        services.AddSingleton<IOptions<ModuleSystemInfoUIOption>>(Options.Create(options));
    });

    private static SystemInfoPageSession ReadSession(IRenderedComponent<UISystemInfoPage> cut)
    {
        var field = typeof(UISystemInfoPage).GetField("_session", BindingFlags.Instance | BindingFlags.NonPublic);
        return field?.GetValue(cut.Instance) as SystemInfoPageSession
            ?? throw new InvalidOperationException("The rendered System Info page did not own a session.");
    }

    private static AngleSharp.Dom.IElement FindAction<TComponent>(
        IRenderedComponent<TComponent> rendered,
        string label)
        where TComponent : IComponent => rendered.FindAll("button")
        .Single(button => button.TextContent.Contains(label, StringComparison.Ordinal));
}
