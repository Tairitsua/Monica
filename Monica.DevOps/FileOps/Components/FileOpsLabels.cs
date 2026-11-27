using System.Globalization;
using Microsoft.Extensions.Localization;
using Monica.DevOps.FileOps.State;
using Monica.DevOps.Localization;
using Monica.UI.Shared.Components.Visual;

namespace Monica.DevOps.FileOps.Components;

internal static class FileOpsLabels
{
    private static readonly string[] ByteUnits = ["B", "KiB", "MiB", "GiB", "TiB", "PiB", "EiB"];

    internal static string Bytes(long bytes)
    {
        var unit = 0;
        var size = (decimal)bytes;
        while (size >= 1024 && unit < ByteUnits.Length - 1)
        {
            size /= 1024;
            unit++;
        }
        return $"{size.ToString("0.##", CultureInfo.CurrentCulture)} {ByteUnits[unit]}";
    }

    internal static LocalizedString Capability(this IStringLocalizer<FileOpsResource> localizer, FileOpsCapability capability) =>
        localizer[capability switch
        {
            FileOpsCapability.Dir => "Design:CapDir",
            FileOpsCapability.Editable => "Design:CapEditable",
            FileOpsCapability.Preview => "Design:CapPreviewOnly",
            FileOpsCapability.Binary => "Design:CapBinary",
            FileOpsCapability.Blocked => "Design:CapBlocked",
            _ => "Design:CapAll"
        }];

    internal static LocalizedString Sort(this IStringLocalizer<FileOpsResource> localizer, FileOpsSortField field) =>
        localizer[field switch
        {
            FileOpsSortField.Type => "Design:ColType",
            FileOpsSortField.Size => "Design:ColSize",
            FileOpsSortField.Modified => "Design:ColModified",
            _ => "Design:ColName"
        }];
}
