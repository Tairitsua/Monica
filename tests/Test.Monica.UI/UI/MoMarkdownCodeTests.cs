using AwesomeAssertions;
using Bunit;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Options;
using Microsoft.JSInterop;
using Monica.Modules;
using Monica.Testing.Localization;
using Monica.UI.Localization;
using Monica.UI.Shared.Components.Markdown;
using Monica.UI.Shell.State;
using MudBlazor;
using MudBlazor.Services;
using Xunit;

namespace Test.Monica.UI;

public sealed class MoMarkdownCodeTests
{
    [Theory]
    [InlineData("目标：src/Services/FlightPlanAppService.cs。", "src/Services/FlightPlanAppService.cs")]
    [InlineData("Read ../src/订单/OrderService.cs:52:4.", "../src/订单/OrderService.cs:52:4")]
    [InlineData(@"Read D:\Code\Orders\OrderService.cs.", @"D:\Code\Orders\OrderService.cs")]
    [InlineData("Read /home/dev/src/dispatch.ts.", "/home/dev/src/dispatch.ts")]
    [InlineData("Read **src/main.tsx**.", "src/main.tsx")]
    [InlineData("Read `src/Orders.csproj`.", "src/Orders.csproj")]
    [InlineData("Read `../src/订单/OrderService.cs:52:4`.", "../src/订单/OrderService.cs:52:4")]
    [InlineData(@"Read `D:\Code\Orders\OrderService.cs`.", @"D:\Code\Orders\OrderService.cs")]
    public async Task FilePaths_ShouldCopyOnlyTheRenderedValue(string markdown, string expected)
    {
        await using var context = CreateContext();
        context.JSInterop.Setup<bool>("MoClipboard.copyText", expected).SetResult(true);
        var cut = context.Render<MoMarkdown>(parameters => parameters.Add(item => item.Value, markdown));

        var button = cut.Find("button.mo-markdown-code");
        button.QuerySelector("code")!.TextContent.Should().Be(expected);
        await button.ClickAsync(new MouseEventArgs());

        context.JSInterop.VerifyInvoke("MoClipboard.copyText").Arguments.Should().Equal(expected);
        cut.Find("[role=status]").TextContent.Should().Be("Markdown:Copied");
    }

    [Theory]
    [InlineData("BSSR / BSEC / BRWY / BRTA / BCWP")]
    [InlineData("flight data exchange")]
    [InlineData("data field / sub-field")]
    [InlineData("Order.Id")]
    [InlineData("Order.cs")]
    [InlineData("dotnet build src/Orders.csproj")]
    [InlineData("src/Orders.csproj --no-restore")]
    [InlineData("https://example.test/src/Order.cs")]
    public async Task OtherInlineCode_ShouldRemainPassive(string text)
    {
        await using var context = CreateContext();
        var cut = context.Render<MoMarkdown>(parameters => parameters.Add(item => item.Value,
            $"| Term |\n| --- |\n| `{text}` |"));

        cut.Find("td code").TextContent.Should().Be(text);
        cut.FindAll(".mo-markdown-code").Should().BeEmpty();
    }

    [Fact]
    public async Task LinksImagesUrlsAndRawHtml_ShouldNotContainCopyButtons()
    {
        await using var context = CreateContext();
        var cut = context.Render<MoMarkdown>(parameters => parameters.Add(item => item.Value,
            "[src/Order.cs and **`Order.Id`**](guide.md) ![src/Logo.cs](logo.png) https://example.test/src/Order.cs " +
            "api/v1.2 and Order.cs\n\nInline <a href=\"guide.md\">src/Order.cs</a>."));

        cut.FindAll(".mo-markdown-code").Should().BeEmpty();
        cut.Find("a code").TextContent.Should().Be("Order.Id");
        cut.Find("a").GetAttribute("href").Should().Be("guide.md");
        cut.Find("img").GetAttribute("alt").Should().Be("src/Logo.cs");
    }

    [Fact]
    public async Task ListsTablesAndParameterChanges_ShouldPreserveMarkdownAndHonorOptOut()
    {
        await using var context = CreateContext();
        var cut = context.Render<MoMarkdown>(parameters => parameters.Add(item => item.Value,
            "- src/Order.cs and `Order.Id`\n\n| File |\n| --- |\n| src/main.ts |"));

        cut.FindAll("li .mo-markdown-code").Should().HaveCount(1);
        cut.Find("li > p > code").TextContent.Should().Be("Order.Id");
        cut.FindAll("td .mo-markdown-code").Should().HaveCount(1);

        cut.Render(parameters => parameters.Add(item => item.EnableCodeCopy, false));
        cut.FindAll(".mo-markdown-code").Should().BeEmpty();
        cut.Find("li code").TextContent.Should().Be("Order.Id");
        cut.Find("td").TextContent.Should().Be("src/main.ts");
    }

    [Fact]
    public async Task FencedCodeAndMermaid_ShouldKeepTheirExistingRenderers()
    {
        await using var context = CreateContext();
        var cut = context.Render<MoMarkdown>(parameters => parameters.Add(item => item.Value,
            "```csharp\nvar path = \"src/Order.cs\";\n```\n\n```mermaid\ngraph TD\nA-->B\n```"));

        cut.FindComponents<MudCodeHighlight>().Should().ContainSingle()
            .Which.Instance.Text.Should().Be("var path = \"src/Order.cs\";");
        cut.FindComponents<MoMarkdownMermaidBlock>().Should().ContainSingle();
        cut.FindAll(".mo-markdown-code").Should().BeEmpty();
    }

    [Fact]
    public async Task RefusedClipboardWrite_ShouldReportFailureAndAllowRetry()
    {
        await using var context = CreateContext();
        var clipboard = context.JSInterop.Setup<bool>("MoClipboard.copyText", "Order.Id").SetResult(false);
        var cut = context.Render<MoMarkdownInlineCode>(parameters => parameters.Add(item => item.Text, "Order.Id"));

        await cut.Find("button").ClickAsync(new MouseEventArgs());
        cut.Find("[role=status]").TextContent.Should().Be("Markdown:CopyFailed");

        clipboard.SetResult(true);
        await cut.Find("button").ClickAsync(new MouseEventArgs());
        cut.Find("[role=status]").TextContent.Should().Be("Markdown:Copied");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ClipboardErrorOrTimeout_ShouldReportFailure(bool timeout)
    {
        await using var context = CreateContext();
        context.JSInterop.Setup<bool>("MoClipboard.copyText", "Order.Id")
            .SetException<Exception>(timeout ? new TaskCanceledException() : new JSException("Clipboard unavailable"));
        var cut = context.Render<MoMarkdownInlineCode>(parameters => parameters.Add(item => item.Text, "Order.Id"));

        await cut.Find("button").ClickAsync(new MouseEventArgs());

        cut.Find("[role=status]").TextContent.Should().Be("Markdown:CopyFailed");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LateClipboardCompletion_ShouldNotUpdateChangedOrDisposedContent(bool dispose)
    {
        await using var context = CreateContext();
        var clipboard = context.JSInterop.Setup<bool>("MoClipboard.copyText", "Order.Id");
        var cut = context.Render<MoMarkdownInlineCode>(parameters => parameters.Add(item => item.Text, "Order.Id"));
        var copying = cut.Find("button").ClickAsync(new MouseEventArgs());
        cut.WaitForAssertion(() => context.JSInterop.VerifyInvoke("MoClipboard.copyText"));

        if (dispose) await cut.InvokeAsync(cut.Instance.Dispose);
        else cut.Render(parameters => parameters.Add(item => item.Text, "Order.Total"));
        clipboard.SetResult(true);
        await copying;

        cut.Find("[role=status]").TextContent.Should().BeEmpty();
    }

    private static BunitContext CreateContext()
    {
        var context = new BunitContext();
        context.JSInterop.Mode = JSRuntimeMode.Loose;
        context.Services.AddMudServices();
        context.Services.AddMudMarkdownServices();
        context.Services.AddSingleton<IStringLocalizer<SharedResource>, EchoStringLocalizer<SharedResource>>();
        context.Services.AddSingleton(new ThemeState(Options.Create(new ModuleUIOption())));
        context.Services.AddSingleton<IMoMarkdownAssetResolver, PassThroughMarkdownAssetResolver>();
        return context;
    }
}
