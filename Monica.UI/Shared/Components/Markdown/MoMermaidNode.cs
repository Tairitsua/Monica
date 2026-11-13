namespace Monica.UI.Shared.Components.Markdown;

/// <summary>Host-owned presentation for a flowchart node, keyed by its authored Mermaid alias.</summary>
/// <param name="Label">Accessible name used when the host supplies a node activation callback.</param>
/// <param name="Icon">Optional trusted 24-unit SVG body, normally from MoIcons. Never read SVG markup from authored Markdown.</param>
/// <param name="Kind">Optional localized kind shown in the node's tooltip alongside its label.</param>
public sealed record MoMermaidNode(string Label, string? Icon = null, string? Kind = null);
