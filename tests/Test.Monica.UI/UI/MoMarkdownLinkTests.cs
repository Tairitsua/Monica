using AwesomeAssertions;
using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Monica.Modules;
using Monica.UI.Shared.Components.Markdown;
using Monica.UI.Shell.State;
using MudBlazor;
using MudBlazor.Services;
using Xunit;

namespace Test.Monica.UI;

public sealed class MoMarkdownLinkTests
{
    [Fact]
    public async Task SelectedLinks_ShouldKeepAuthoredIdentityAndFormattingWhileOtherUrlsUseTheResolver()
    {
        await using var context = new BunitContext();
        context.JSInterop.Mode = JSRuntimeMode.Loose;
        context.Services.AddMudServices();
        context.Services.AddMudMarkdownServices();
        context.Services.AddSingleton(new ThemeState(Options.Create(new ModuleUIOption())));
        context.Services.AddSingleton<IMoMarkdownAssetResolver, TestResolver>();
        string? activated = null;
        Func<MoMarkdownLink, RenderFragment?> template = link => link.Url == "topic.md#section"
            ? builder =>
            {
                builder.OpenComponent<MudLink>(0);
                builder.AddComponentParameter(1, nameof(MudLink.ChildContent), link.ChildContent);
                builder.AddComponentParameter(2, nameof(MudLink.OnClick), EventCallback.Factory.Create<Microsoft.AspNetCore.Components.Web.MouseEventArgs>(
                    context, () => activated = link.Url));
                builder.CloseComponent();
            }
            : null;

        var cut = context.Render<MoMarkdown>(parameters => parameters
            .Add(item => item.Value, "[**Topic** `code`](topic.md#section) [Other](other.md) ![Picture](image.png)")
            .Add(item => item.LinkTemplate, template));

        cut.Find("a b").TextContent.Should().Be("Topic");
        cut.Find("a code").TextContent.Should().Be("code");
        cut.FindAll("a")[0].Click();
        activated.Should().Be("topic.md#section");
        cut.FindAll("a")[1].GetAttribute("href").Should().Be("/resolved/other.md");
        cut.Find("img").GetAttribute("src").Should().Be("/resolved/image.png");

        cut.Render(parameters => parameters.Add(item => item.Value, "[Changed](topic.md#section)"));
        cut.Find("a").TextContent.Should().Be("Changed");
    }

    private sealed class TestResolver : IMoMarkdownAssetResolver
    {
        public string? ResolveUrl(string? originalUrl, bool isImage, string? scopeKey,
            string? documentRelativePath, string? documentCulture) => $"/resolved/{originalUrl}";
    }
}
