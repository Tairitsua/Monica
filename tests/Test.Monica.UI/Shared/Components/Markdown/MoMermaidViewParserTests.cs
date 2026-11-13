using Monica.UI.Shared.Components.Markdown;
using Xunit;

namespace Test.Monica.UI.Shared.Components.Markdown;

public sealed class MoMermaidViewParserTests
{
    [Fact]
    public void Parse_KeepsEachDiagramWithItsExplanationAndDoesNotSplitHeadingsInsideFences()
    {
        const string source = """
            ## Acceptance
            ```mermaid
            flowchart LR
                Handler --> Batch
            ```
            A [reference](topic.md) and an explanation.

            1. **T1** Save.
            2. **T2** Send.

            ```text
            ## Not another view
            ```

            ## Recovery
            ```mermaid
            flowchart LR
                Batch --> Retry
            ```
            ### Retry limit
            Preserve durable state.
            """;

        var result = MoMermaidViewParser.Parse(source);

        Assert.Equal(MoMermaidViewError.None, result.Error);
        Assert.Equal(2, result.Views.Count);
        Assert.Equal("Acceptance", result.Views[0].Title);
        Assert.Contains("Handler --> Batch", result.Views[0].Definition);
        Assert.Contains("[reference](topic.md)", result.Views[0].Description);
        Assert.Contains("## Not another view", result.Views[0].Description);
        Assert.DoesNotContain("Retry limit", result.Views[0].Description);
        Assert.Equal("### Retry limit\nPreserve durable state.", result.Views[1].Description.ReplaceLineEndings("\n"));
    }

    [Theory]
    [InlineData("", MoMermaidViewError.Structure)]
    [InlineData("Intro\n## View\n```mermaid\nflowchart LR\nA-->B\n```", MoMermaidViewError.Structure)]
    [InlineData("## *Formatted*\n```mermaid\nA-->B\n```", MoMermaidViewError.Structure)]
    [InlineData("## View\nProse before diagram", MoMermaidViewError.Diagram)]
    [InlineData("## View\n```mermaid\n```", MoMermaidViewError.Diagram)]
    [InlineData("## View\n```mermaid\nA-->B\n```\n```mermaid\nB-->C\n```", MoMermaidViewError.Diagram)]
    [InlineData("## View\n```mermaid\nA-->B\n```\n## view\n```mermaid\nB-->C\n```", MoMermaidViewError.DuplicateTitle)]
    public void Parse_RejectsAnInvalidGroupWithoutShowingPartialViews(string source, object error)
    {
        var result = MoMermaidViewParser.Parse(source);

        Assert.Equal((MoMermaidViewError)error, result.Error);
        Assert.Empty(result.Views);
    }
}
