using Markdig.Syntax;
using Markdig.Syntax.Inlines;

namespace Monica.UI.Shared.Components.Markdown;

internal sealed record MoMermaidView(string Title, string Definition, string Description);
internal enum MoMermaidViewError { None, Structure, Diagram, DuplicateTitle }
internal sealed record MoMermaidViewParseResult(IReadOnlyList<MoMermaidView> Views, MoMermaidViewError Error);

/// <summary>Parses a mermaid-tabs fence as named Markdown sections, without interpreting authored HTML or callbacks.</summary>
internal static class MoMermaidViewParser
{
    internal static MoMermaidViewParseResult Parse(string source)
    {
        var document = Markdig.Markdown.Parse(source);
        List<MoMermaidView> views = [];
        HashSet<string> titles = new(StringComparer.OrdinalIgnoreCase);
        if (document.Count == 0) return new([], MoMermaidViewError.Structure);
        for (var index = 0; index < document.Count;)
        {
            if (document[index] is not HeadingBlock { Level: 2 } heading)
                return new([], MoMermaidViewError.Structure);
            if (heading.IsSetext || heading.Inline?.FirstChild is not LiteralInline { NextSibling: null } literal)
                return new([], MoMermaidViewError.Structure);
            var title = literal.Content.ToString().Trim();
            if (string.IsNullOrWhiteSpace(title)) return new([], MoMermaidViewError.Structure);
            if (!titles.Add(title)) return new([], MoMermaidViewError.DuplicateTitle);
            index++;
            if (index >= document.Count || document[index] is not FencedCodeBlock diagram || !IsMermaid(diagram)
                || string.IsNullOrWhiteSpace(diagram.Lines.ToString()))
                return new([], MoMermaidViewError.Diagram);
            index++;
            var descriptionStart = diagram.Span.End + 1;
            while (index < document.Count && document[index] is not HeadingBlock { Level: 2 })
            {
                if (document[index] is FencedCodeBlock other && IsMermaid(other))
                    return new([], MoMermaidViewError.Diagram);
                index++;
            }
            var descriptionEnd = index < document.Count ? document[index].Span.Start : source.Length;
            views.Add(new(title, diagram.Lines.ToString(), source[descriptionStart..descriptionEnd].Trim()));
        }
        return new(views, MoMermaidViewError.None);
    }

    private static bool IsMermaid(FencedCodeBlock block)
        => string.Equals(block.Info?.Trim(), "mermaid", StringComparison.OrdinalIgnoreCase);
}
