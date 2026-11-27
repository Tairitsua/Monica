using System.Globalization;
using Monica.DevOps.FileOps.Models;

namespace Monica.DevOps.FileOps.State;

internal sealed record FileOpsPolicyFields(
    string Roots, string Startup, string ReadOnly, bool Write, bool Delete, bool Hidden,
    string TextLimit, string UploadLimit, string DownloadLimit, string EntryLimit, string Extensions)
{
    internal static FileOpsPolicyFields From(FileOpsRuntimeConfig config) => new(
        FileOpsRuntimeConfig.ToMultilineText(config.AllowedRoots), config.StartupPath,
        FileOpsRuntimeConfig.ToMultilineText(config.ReadOnlyPaths), config.AllowWriteOperations,
        config.AllowDeleteOperations, config.AllowHiddenEntries,
        config.MaxTextFileSizeBytes.ToString(CultureInfo.InvariantCulture),
        config.MaxUploadFileSizeBytes.ToString(CultureInfo.InvariantCulture),
        config.MaxDownloadFileSizeBytes.ToString(CultureInfo.InvariantCulture),
        config.MaxDirectoryEntries.ToString(CultureInfo.InvariantCulture),
        FileOpsRuntimeConfig.ToMultilineText(config.EditableExtensions));

    internal FileOpsRuntimeConfig ToConfig() => new()
    {
        AllowedRoots = FileOpsRuntimeConfig.ParseMultiValue(Roots),
        StartupPath = Startup,
        ReadOnlyPaths = FileOpsRuntimeConfig.ParseMultiValue(ReadOnly),
        AllowWriteOperations = Write,
        AllowDeleteOperations = Delete,
        AllowHiddenEntries = Hidden,
        MaxTextFileSizeBytes = Parse(TextLimit),
        MaxUploadFileSizeBytes = Parse(UploadLimit),
        MaxDownloadFileSizeBytes = Parse(DownloadLimit),
        MaxDirectoryEntries = (int)Math.Clamp(Parse(EntryLimit), int.MinValue, int.MaxValue),
        EditableExtensions = FileOpsRuntimeConfig.ParseMultiValue(Extensions)
    };

    private static long Parse(string text) => long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? value : 0;
}

/// <summary>Tracks raw policy edits, normalized preview evidence and the accepted host baseline.</summary>
public sealed class FileOpsPolicyDraft
{
    private FileOpsPolicyFields _baseline;
    internal FileOpsPolicyFields Fields { get; private set; }
    internal long Revision { get; private set; }
    internal FileOpsRuntimeConfig Draft { get; private set; }
    internal FileOpsRuntimeConfig Effective { get; private set; }
    internal FileOpsPolicyNormalization Normalization { get; private set; }
    internal IReadOnlySet<string> MissingRoots { get; private set; }
    internal bool IsDirty => _baseline != Fields;
    internal bool TextValid => Draft.MaxTextFileSizeBytes >= 4 * 1024;
    internal bool UploadValid => Draft.MaxUploadFileSizeBytes >= 64 * 1024;
    internal bool DownloadValid => Draft.MaxDownloadFileSizeBytes >= Math.Max(64 * 1024, Draft.MaxUploadFileSizeBytes);
    internal bool EntryLimitValid => Draft.MaxDirectoryEntries is >= 50 and <= 5_000;
    internal bool IsValid => TextValid && UploadValid && DownloadValid && EntryLimitValid && MissingRoots.Count == 0;

    internal int ChangedCount => new[]
    {
        Fields.Roots != _baseline.Roots, Fields.Startup != _baseline.Startup, Fields.ReadOnly != _baseline.ReadOnly,
        Fields.Write != _baseline.Write, Fields.Delete != _baseline.Delete, Fields.Hidden != _baseline.Hidden,
        Fields.TextLimit != _baseline.TextLimit, Fields.UploadLimit != _baseline.UploadLimit,
        Fields.DownloadLimit != _baseline.DownloadLimit, Fields.EntryLimit != _baseline.EntryLimit,
        Fields.Extensions != _baseline.Extensions
    }.Count(static value => value);

    internal FileOpsPolicyDraft(FileOpsRuntimeConfig config)
    {
        Fields = _baseline = FileOpsPolicyFields.From(config);
        (Draft, Effective, Normalization, MissingRoots) = Preview(Fields);
    }

    internal void Edit(FileOpsPolicyFields fields)
    {
        if (Fields == fields) return;
        Fields = fields;
        Revision++;
        (Draft, Effective, Normalization, MissingRoots) = Preview(fields);
    }

    internal void Accept(FileOpsRuntimeConfig config, long? replaceRevision = null)
    {
        var replace = !IsDirty || replaceRevision == Revision;
        _baseline = FileOpsPolicyFields.From(config);
        if (replace) Edit(_baseline);
    }

    internal void Discard() => Edit(_baseline);

    private static (FileOpsRuntimeConfig Draft, FileOpsRuntimeConfig Effective, FileOpsPolicyNormalization Normalization,
        IReadOnlySet<string> MissingRoots) Preview(FileOpsPolicyFields fields)
    {
        var draft = fields.ToConfig();
        var effective = draft.Clone();
        var normalization = effective.NormalizeWithEvidence();
        return (draft, effective, normalization, effective.AllowedRoots.Where(static root => !Directory.Exists(root)).ToHashSet());
    }
}
