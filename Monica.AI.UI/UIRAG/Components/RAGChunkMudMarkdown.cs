using System.Net;
using Markdig.Extensions.Tables;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Rendering;
using Microsoft.Extensions.Localization;
using Monica.AI.RAG.Models;
using Monica.AI.UI.Localization;
using Monica.UI.Shared.Components.Markdown;
using MudBlazor;
using MarkdownBlock = Markdig.Syntax.Block;

namespace Monica.AI.UI.UIRAG.Components;

/// <summary>
/// Markdown renderer that emits chunk wrappers so previews can highlight chunk regions directly.
/// </summary>
public sealed class RAGChunkMudMarkdown : MoMudMarkdown
{
    private delegate void BlockRenderer(RenderTreeBuilder builder, ref int elementIndex, MarkdownBlock block, Typo bodyTypo);

    private readonly Stack<string> _activeChunkWrapperKeys = [];
    private List<ChunkHighlight> _orderedChunks = [];

    [Inject]
    public IStringLocalizer<AIResource> L { get; set; } = null!;

    [Parameter]
    public IReadOnlyList<ChunkHighlight> Chunks { get; set; } = Array.Empty<ChunkHighlight>();

    [Parameter]
    public int? MatchedChunkIndex { get; set; }

    protected override void OnParametersSet()
    {
        base.OnParametersSet();

        _orderedChunks = Chunks
            .OrderBy(chunk => chunk.Start)
            .ThenBy(chunk => chunk.End)
            .ThenBy(chunk => chunk.Index)
            .ToList();
    }

    protected override void RenderMarkdownRoot(RenderTreeBuilder builder, ref int elementIndex, ContainerBlock container)
    {
        _activeChunkWrapperKeys.Clear();

        try
        {
            base.RenderMarkdownRoot(builder, ref elementIndex, container);
        }
        finally
        {
            _activeChunkWrapperKeys.Clear();
        }
    }

    protected override void RenderMarkdown(RenderTreeBuilder builder, ref int elementIndex, ContainerBlock container)
        => RenderChunkGroupedBlocks(builder, ref elementIndex, container.Count, index => (MarkdownBlock)container[index], RenderMarkdownBlock);

    protected override void RenderList(RenderTreeBuilder builder, ref int elementIndex, ListBlock list)
    {
        if (list.Count == 0)
        {
            return;
        }

        var elementName = list.IsOrdered ? "ol" : "ul";
        _ = int.TryParse(list.OrderedStart, out var orderStart);

        builder.OpenElement(0, elementName);

        if (orderStart > 1)
        {
            builder.AddAttribute(1, "start", orderStart);
        }

        for (var i = 0; i < list.Count; i++)
        {
            var item = (ListItemBlock)list[i];
            builder.OpenElement(2, "li");
            RenderChunkGroupedBlocks(builder, ref elementIndex, item.Count, index => (MarkdownBlock)item[index], RenderListBlock);
            builder.CloseElement();
        }

        builder.CloseElement();
    }

    private void RenderChunkGroupedBlocks(
        RenderTreeBuilder builder,
        ref int elementIndex,
        int blockCount,
        Func<int, MarkdownBlock> blockAccessor,
        BlockRenderer renderer)
    {
        var bodyTypo = Props.Body.OverrideTypo ?? Typo.body1;
        var inheritedChunkKey = GetCurrentChunkWrapperKey();
        string? localWrapperKey = null;

        for (var i = 0; i < blockCount; i++)
        {
            var block = blockAccessor(i);
            var activeChunks = GetEffectiveChunks(block);
            var targetWrapperKey = GetTargetWrapperKey(activeChunks, inheritedChunkKey);

            if (!string.Equals(localWrapperKey, targetWrapperKey, StringComparison.Ordinal))
            {
                CloseChunkWrapper(builder, ref localWrapperKey);

                if (targetWrapperKey is not null)
                {
                    localWrapperKey = OpenChunkWrapper(builder, activeChunks, targetWrapperKey);
                }
            }

            renderer(builder, ref elementIndex, block, bodyTypo);
        }

        CloseChunkWrapper(builder, ref localWrapperKey);
    }

    private void RenderMarkdownBlock(RenderTreeBuilder builder, ref int elementIndex, MarkdownBlock block, Typo bodyTypo)
    {
        switch (block)
        {
            case ParagraphBlock paragraph:
            {
                RenderParagraphBlock(builder, ref elementIndex, paragraph, bodyTypo);
                break;
            }
            case HeadingBlock heading:
            {
                EnableLinkNavigation = true;

                var typo = Props.Heading.OverrideTypo?.Invoke((Typo)heading.Level) ?? (Typo)heading.Level;
                RenderParagraphBlock(builder, ref elementIndex, heading, typo, BuildHeadingId(heading));
                break;
            }
            case QuoteBlock quote:
            {
                builder.OpenElement(0, "blockquote");
                RenderMarkdown(builder, ref elementIndex, quote);
                builder.CloseElement();
                break;
            }
            case Table table:
            {
                RenderTable(builder, ref elementIndex, table);
                break;
            }
            case ListBlock list:
            {
                RenderList(builder, ref elementIndex, list);
                break;
            }
            case ThematicBreakBlock:
            {
                builder.OpenComponent<MudDivider>(0);
                builder.CloseComponent();
                break;
            }
            case FencedCodeBlock code:
            {
                RenderCodeBlock(builder, ref elementIndex, code, code.Info, bodyTypo);
                break;
            }
            case CodeBlock code:
            {
                RenderCodeBlock(builder, ref elementIndex, code, info: null, bodyTypo);
                break;
            }
            case HtmlBlock html:
            {
                if (TryParseDetailsHtml(html.Lines.ToString(), out var header, out var content))
                {
                    RenderDetailsHtml(builder, ref elementIndex, header, content);
                }
                else
                {
                    RenderHtml(builder, ref elementIndex, html.Lines);
                }

                break;
            }
            default:
            {
                OnRenderMarkdownBlockDefault(builder, ref elementIndex, block);
                break;
            }
        }
    }

    private void RenderListBlock(RenderTreeBuilder builder, ref int elementIndex, MarkdownBlock block, Typo bodyTypo)
    {
        switch (block)
        {
            case ListBlock list:
            {
                RenderList(builder, ref elementIndex, list);
                break;
            }
            case ParagraphBlock paragraph:
            {
                RenderParagraphBlock(builder, ref elementIndex, paragraph, bodyTypo);
                break;
            }
            case FencedCodeBlock code:
            {
                RenderCodeBlock(builder, ref elementIndex, code, code.Info, bodyTypo);
                break;
            }
            case CodeBlock code:
            {
                RenderCodeBlock(builder, ref elementIndex, code, info: null, bodyTypo);
                break;
            }
            default:
            {
                OnRenderListDefault(builder, ref elementIndex, block, bodyTypo);
                break;
            }
        }
    }

    private IReadOnlyList<ChunkHighlight> GetEffectiveChunks(MarkdownBlock block)
    {
        return block switch
        {
            HeadingBlock heading when heading.Inline is null => [],
            ParagraphBlock paragraph when paragraph.Inline is null => [],
            ListBlock list => TryGetUniformChunks(list, out var listChunks) ? listChunks : [],
            QuoteBlock quote => TryGetUniformChunks(quote, out var quoteChunks) ? quoteChunks : [],
            _ => GetOverlappingChunks(block)
        };
    }

    private bool TryGetUniformChunks(MarkdownBlock block, out IReadOnlyList<ChunkHighlight> chunks)
    {
        switch (block)
        {
            case Table table:
            {
                chunks = GetOverlappingChunks(table);
                return true;
            }
            case ListBlock list:
            {
                return TryGetUniformContainerChunks(list, out chunks);
            }
            case QuoteBlock quote:
            {
                return TryGetUniformContainerChunks(quote, out chunks);
            }
            case ListItemBlock item:
            {
                return TryGetUniformContainerChunks(item, out chunks);
            }
            default:
            {
                chunks = GetEffectiveChunks(block);
                return true;
            }
        }
    }

    private bool TryGetUniformContainerChunks(ContainerBlock container, out IReadOnlyList<ChunkHighlight> chunks)
    {
        chunks = [];
        string? uniformChunkKey = null;

        for (var i = 0; i < container.Count; i++)
        {
            if (!TryGetUniformChunks((MarkdownBlock)container[i], out var childChunks))
            {
                chunks = [];
                return false;
            }

            var childChunkKey = BuildChunkKey(childChunks);
            if (uniformChunkKey is null)
            {
                uniformChunkKey = childChunkKey;
                chunks = childChunks;
                continue;
            }

            if (!string.Equals(uniformChunkKey, childChunkKey, StringComparison.Ordinal))
            {
                chunks = [];
                return false;
            }
        }

        return true;
    }

    private string OpenChunkWrapper(
        RenderTreeBuilder builder,
        IReadOnlyList<ChunkHighlight> chunks,
        string chunkKey)
    {
        var metadata = BuildChunkMetadata(chunks);

        builder.OpenElement(0, "div");
        builder.AddAttribute(1, "class", BuildChunkClass(chunks));
        builder.AddAttribute(2, "data-rag-chunk", "true");
        builder.AddAttribute(3, "data-rag-chunk-ids", chunkKey);
        builder.AddAttribute(4, "data-rag-chunk-label", metadata);
        builder.AddAttribute(5, "title", metadata);

        if (ContainsMatchedChunk(chunks))
        {
            builder.AddAttribute(6, "data-rag-chunk-target", "true");
        }

        _activeChunkWrapperKeys.Push(chunkKey);

        return chunkKey;
    }

    private void CloseChunkWrapper(RenderTreeBuilder builder, ref string? openedChunkKey)
    {
        if (openedChunkKey is null)
        {
            return;
        }

        builder.CloseElement();
        _activeChunkWrapperKeys.Pop();
        openedChunkKey = null;
    }

    private IReadOnlyList<ChunkHighlight> GetOverlappingChunks(MarkdownObject markdownObject)
    {
        if (_orderedChunks.Count == 0)
        {
            return [];
        }

        var span = markdownObject.Span;
        if (span.Start < 0 || span.End < span.Start)
        {
            return [];
        }

        var spanEndExclusive = span.End + 1;
        return _orderedChunks
            .Where(chunk => chunk.Start < spanEndExclusive && chunk.End > span.Start)
            .ToList();
    }

    private bool ContainsMatchedChunk(IReadOnlyList<ChunkHighlight> chunks)
        => MatchedChunkIndex.HasValue && chunks.Any(chunk => chunk.Index == MatchedChunkIndex.Value);

    private string BuildChunkClass(IReadOnlyList<ChunkHighlight> chunks)
    {
        var classes = new List<string> { "rag-chunk", "rag-chunk--block" };

        if (chunks.Count > 1)
        {
            
            classes.Add("rag-chunk--overlap");
        }

        if (ContainsMatchedChunk(chunks))
        {
            classes.Add("rag-chunk--matched");
        }

        return string.Join(" ", classes);
    }

    private string BuildChunkMetadata(IReadOnlyList<ChunkHighlight> chunks)
    {
        var chunkLabel = chunks.Count == 1
            ? L["RAG:ChunkViewer:ChunkNumber", chunks[0].Index].Value
            : L["RAG:ChunkViewer:ChunkNumbers", string.Join(", ", chunks.Select(chunk => chunk.Index))].Value;

        var sections = chunks
            .Select(chunk => chunk.Section)
            .Where(section => !string.IsNullOrWhiteSpace(section))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (sections.Count > 0)
        {
            chunkLabel = $"{chunkLabel} · {L["RAG:ChunkViewer:Section", string.Join(" / ", sections)]}";
        }

        if (ContainsMatchedChunk(chunks))
        {
            chunkLabel = $"{chunkLabel} · {L["RAG:ChunkViewer:MatchedBadge"]}";
        }

        return chunkLabel;
    }

    private string? GetCurrentChunkWrapperKey()
        => _activeChunkWrapperKeys.Count > 0 ? _activeChunkWrapperKeys.Peek() : null;

    private static string? GetTargetWrapperKey(IReadOnlyList<ChunkHighlight> chunks, string? inheritedChunkKey)
    {
        if (chunks.Count == 0)
        {
            return null;
        }

        var chunkKey = BuildChunkKey(chunks);
        return string.Equals(chunkKey, inheritedChunkKey, StringComparison.Ordinal)
            ? null
            : chunkKey;
    }

    private static string BuildChunkKey(IReadOnlyList<ChunkHighlight> chunks)
        => chunks.Count == 0
            ? string.Empty
            : string.Join(",", chunks.Select(chunk => chunk.Index));

    private static string? BuildHeadingId(HeadingBlock heading)
    {
        if (heading.Inline is null)
        {
            return null;
        }

        var words = new List<string>();

        foreach (var inline in heading.Inline)
        {
            if (inline is not LiteralInline literalInline || literalInline.Content.IsEmpty)
            {
                continue;
            }

            var text = literalInline.Content.ToString();
            words.AddRange(
                text.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .Select(word => word.ToLowerInvariant()));
        }

        if (words.Count == 0)
        {
            return null;
        }

        return WebUtility.UrlEncode(string.Join("-", words));
    }

    private static bool TryParseDetailsHtml(string html, out string header, out string content)
    {
        header = string.Empty;
        content = string.Empty;

        if (string.IsNullOrWhiteSpace(html))
        {
            return false;
        }

        var detailsStart = html.IndexOf("<details", StringComparison.OrdinalIgnoreCase);
        if (detailsStart < 0)
        {
            return false;
        }

        var detailsOpenEnd = html.IndexOf('>', detailsStart);
        if (detailsOpenEnd < 0)
        {
            return false;
        }

        var detailsClose = html.LastIndexOf("</details>", StringComparison.OrdinalIgnoreCase);
        if (detailsClose <= detailsOpenEnd)
        {
            return false;
        }

        var summaryStart = html.IndexOf("<summary", detailsOpenEnd + 1, StringComparison.OrdinalIgnoreCase);
        if (summaryStart < 0)
        {
            return false;
        }

        var summaryOpenEnd = html.IndexOf('>', summaryStart);
        if (summaryOpenEnd < 0)
        {
            return false;
        }

        var summaryClose = html.IndexOf("</summary>", summaryOpenEnd + 1, StringComparison.OrdinalIgnoreCase);
        if (summaryClose < 0 || summaryClose > detailsClose)
        {
            return false;
        }

        header = html[(summaryOpenEnd + 1)..summaryClose].Trim();
        content = html[(summaryClose + "</summary>".Length)..detailsClose].Trim();

        return true;
    }
}
