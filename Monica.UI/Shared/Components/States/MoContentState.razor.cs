namespace Monica.UI.Shared.Components.States;

/// <summary>Presents centered loading, empty, or unavailable content inside a table, card, or results region.</summary>
/// <remarks>
/// Owns presentation only. Callers supply localized content and retain data, lifecycle, selection, and recovery actions.
/// Use for a block that replaces results, not for ordinary rows, inline progress, reading content, or contextual warnings.
/// The component sizes to its containing surface and never creates a scroll owner or claims viewport height.
/// </remarks>
public partial class MoContentState;
