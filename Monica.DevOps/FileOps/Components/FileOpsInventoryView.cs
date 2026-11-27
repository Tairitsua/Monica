using Monica.DevOps.FileOps.Models;
using Monica.DevOps.FileOps.State;
using Monica.UI.Shared.Components.Visual;

namespace Monica.DevOps.FileOps.Components;

/// <summary>Filtered rows and capability counts over one accepted, sorted directory subset.</summary>
public sealed class FileOpsInventoryView(IReadOnlyList<FileOpsEntrySummary> entries, FileOpsViewQuery query)
{
    internal static IReadOnlyList<FileOpsCapability> Categories { get; } =
        [FileOpsCapability.Dir, FileOpsCapability.Editable, FileOpsCapability.Preview, FileOpsCapability.Binary, FileOpsCapability.Blocked];

    internal static MoVisualRole Role(FileOpsCapability capability) => capability switch
    {
        FileOpsCapability.Editable => MoVisualRole.Success,
        FileOpsCapability.Preview => MoVisualRole.Info,
        FileOpsCapability.Binary => MoVisualRole.Secondary,
        FileOpsCapability.Blocked => MoVisualRole.Warning,
        _ => MoVisualRole.Primary
    };

    internal static FileOpsCapability Classify(FileOpsEntrySummary entry) => entry switch
    {
        { IsDirectory: true } => FileOpsCapability.Dir,
        { CanDownload: false } => FileOpsCapability.Blocked,
        { CanEditText: true } => FileOpsCapability.Editable,
        { TextPreview: FileOpsTextPreviewKind.Text } => FileOpsCapability.Preview,
        _ => FileOpsCapability.Binary
    };

    private readonly FileOpsEntrySummary[] _matches = entries.Where(entry =>
        entry.Name.Contains(query.Search.Trim(), StringComparison.OrdinalIgnoreCase) ||
        entry.Extension.Contains(query.Search.Trim(), StringComparison.OrdinalIgnoreCase)).ToArray();

    internal IReadOnlyList<FileOpsEntrySummary> Filtered => query.Capability == FileOpsCapability.All
        ? _matches : _matches.Where(entry => Classify(entry) == query.Capability).ToArray();

    internal int Count(FileOpsCapability cap) => entries.Count(entry => Classify(entry) == cap);

    internal int OptionCount(FileOpsCapability cap) => cap == FileOpsCapability.All
        ? _matches.Length : _matches.Count(entry => Classify(entry) == cap);
}
