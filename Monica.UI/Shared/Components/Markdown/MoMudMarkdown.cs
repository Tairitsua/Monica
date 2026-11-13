using System.Text;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Rendering;
using MudBlazor;

namespace Monica.UI.Shared.Components.Markdown;

/// <summary>
/// Extends <see cref="MudMarkdown"/> with Mermaid diagrams, copyable file paths and selective link templates.
/// </summary>
public class MoMudMarkdown : MudMarkdown
{
    private IReadOnlyList<MoMarkdownHeading> _headings = [];
    private int _tableOfContentsHeadingCount;

    /// <summary>
    /// Gets or sets whether Mermaid fenced code blocks should be rendered as diagrams.
    /// </summary>
    [Parameter]
    public bool EnableMermaid { get; set; } = true;

    /// <summary>Host-owned flowchart aliases, accessible labels and trusted kind icons.</summary>
    [Parameter] public IReadOnlyDictionary<string, MoMermaidNode>? MermaidNodes { get; set; }

    /// <summary>Activates a host-owned diagram node without evaluating authored JavaScript.</summary>
    [Parameter] public EventCallback<string> MermaidNodeActivated { get; set; }

    /// <summary>
    /// Enables click-to-copy for recognizable file paths in prose or inline code. Defaults to true.
    /// Other inline code, including identifiers and commands, keeps standard Markdown rendering.
    /// Code inside links remains part of the link; fenced blocks retain their own copy control.
    /// </summary>
    [Parameter]
    public bool EnableCodeCopy { get; set; } = true;

    /// <summary>
    /// Raised when the parsed markdown headings change.
    /// </summary>
    [Parameter]
    public EventCallback<IReadOnlyList<MoMarkdownHeading>> HeadingsChanged { get; set; }

    /// <summary>
    /// Optionally renders selected non-image links before asset URL resolution.
    /// Return null to preserve the default renderer for a link.
    /// </summary>
    [Parameter]
    public Func<MoMarkdownLink, RenderFragment?>? LinkTemplate { get; set; }

    public override async Task SetParametersAsync(ParameterView parameters)
    {
        await base.SetParametersAsync(parameters);

        var headings = MoMarkdownHeadingParser.Parse(Value);
        _tableOfContentsHeadingCount = headings.Count(x => x.Level <= 3);

        if (_headings.SequenceEqual(headings))
        {
            return;
        }

        _headings = headings;

        if (HeadingsChanged.HasDelegate)
        {
            await HeadingsChanged.InvokeAsync(_headings);
        }
    }

    protected override void BuildRenderTree(RenderTreeBuilder builder)
    {
        var originalHasTableOfContents = HasTableOfContents;
        HasTableOfContents = HasTableOfContents && _tableOfContentsHeadingCount > 0;

        try
        {
            base.BuildRenderTree(builder);
        }
        finally
        {
            HasTableOfContents = originalHasTableOfContents;
        }
    }

    /// <inheritdoc />
    protected override void RenderInlines(RenderTreeBuilder builder, ref int elementIndex, ContainerInline inlines)
    {
        if (LinkTemplate is not null)
        {
            foreach (var link in inlines.OfType<LinkInline>().Where(static link => !link.IsImage).ToArray())
            {
                RenderFragment label = childBuilder =>
                {
                    var childIndex = 0;
                    RenderInlines(childBuilder, ref childIndex, link);
                };
                var content = LinkTemplate(new MoMarkdownLink(link.Url, link.Title, label));
                if (content is not null)
                {
                    link.ReplaceBy(new TemplatedInline(content), copyChildren: false);
                }
            }
        }

        if (EnableCodeCopy && !IsLinkOrHtmlContent(inlines))
        {
            foreach (var inline in inlines.ToArray())
            {
                RenderFragment? content = inline switch
                {
                    CodeInline code when MoMarkdownCodePaths.IsFilePath(code.Content)
                        => builder => RenderCopyableCode(builder, code.Content),
                    LiteralInline literal => MoMarkdownCodePaths.CreateContent(literal.Content.ToString()),
                    _ => null
                };
                if (content is not null)
                {
                    inline.ReplaceBy(new TemplatedInline(content), copyChildren: false);
                }
            }
        }

        base.RenderInlines(builder, ref elementIndex, inlines);
    }

    /// <inheritdoc />
    protected override void OnRenderInlinesDefault(RenderTreeBuilder builder, ref int elementIndex, Inline inline)
    {
        if (inline is TemplatedInline templated)
        {
            builder.AddContent(0, templated.Content);
            elementIndex++;
            return;
        }

        base.OnRenderInlinesDefault(builder, ref elementIndex, inline);
    }

    private static bool IsLinkOrHtmlContent(ContainerInline inlines)
    {
        for (var container = inlines; container is not null; container = container.Parent)
        {
            if (container is LinkInline || container.Any(static inline => inline is HtmlInline))
            {
                return true;
            }
        }

        return false;
    }

    internal static void RenderCopyableCode(RenderTreeBuilder builder, string text)
    {
        builder.OpenComponent<MoMarkdownInlineCode>(0);
        builder.AddComponentParameter(1, nameof(MoMarkdownInlineCode.Text), text);
        builder.CloseComponent();
    }

    private sealed class TemplatedInline(RenderFragment content) : Inline
    {
        public RenderFragment Content { get; } = content;
    }

    protected override void RenderCodeBlock(in RenderTreeBuilder builder, ref int elementIndex, in CodeBlock code, in string? info)
    {
        if (EnableMermaid && string.Equals(info?.Trim(), "mermaid-tabs", StringComparison.OrdinalIgnoreCase))
        {
            builder.OpenComponent<MoMarkdownMermaidViews>(0);
            builder.AddComponentParameter(1, nameof(MoMarkdownMermaidViews.Definition), CreateCodeBlockText(code));
            builder.AddComponentParameter(2, nameof(MoMarkdownMermaidViews.Nodes), MermaidNodes);
            builder.AddComponentParameter(3, nameof(MoMarkdownMermaidViews.NodeActivated), MermaidNodeActivated);
            builder.AddComponentParameter(4, nameof(MoMarkdownMermaidViews.MarkdownPipeline), MarkdownPipeline);
            builder.AddComponentParameter(5, nameof(MoMarkdownMermaidViews.Props), Props);
            builder.AddComponentParameter(6, nameof(MoMarkdownMermaidViews.Styling), Styling);
            builder.AddComponentParameter(7, nameof(MoMarkdownMermaidViews.LinkTemplate), LinkTemplate);
            builder.AddComponentParameter(8, nameof(MoMarkdownMermaidViews.EnableCodeCopy), EnableCodeCopy);
            builder.CloseComponent();
            elementIndex += 2;
            return;
        }

        if (EnableMermaid && string.Equals(info?.Trim(), "mermaid", StringComparison.OrdinalIgnoreCase))
        {
            builder.OpenComponent<MoMarkdownMermaidBlock>(0);
            builder.AddComponentParameter(1, nameof(MoMarkdownMermaidBlock.Definition), CreateCodeBlockText(code));
            builder.AddComponentParameter(2, nameof(MoMarkdownMermaidBlock.Nodes), MermaidNodes);
            builder.AddComponentParameter(3, nameof(MoMarkdownMermaidBlock.NodeActivated), MermaidNodeActivated);
            builder.CloseComponent();
            elementIndex += 2;
            return;
        }

        base.RenderCodeBlock(builder, ref elementIndex, code, info);
    }

    private static string CreateCodeBlockText(CodeBlock code)
    {
        if (code.Lines.Count == 0)
        {
            return string.Empty;
        }

        var builder = new StringBuilder();

        foreach (var line in code.Lines)
        {
            if (builder.Length != 0)
            {
                builder.AppendLine();
            }

            builder.Append(line.ToString());
        }

        return builder.ToString();
    }
}
