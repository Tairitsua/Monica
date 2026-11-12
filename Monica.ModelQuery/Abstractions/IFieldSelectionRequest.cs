using Monica.ModelQuery.Models;
using Monica.ModelQuery.Exceptions;

namespace Monica.ModelQuery.Abstractions;

/// <summary>Optional, mutually exclusive field inclusion and exclusion parameters.</summary>
public interface IFieldSelectionRequest
{
    /// <summary>Query names to include; null or empty means no inclusion was requested.</summary>
    string[]? IncludeFields { get; set; }
    /// <summary>Query names to exclude; null or empty means no exclusion was requested.</summary>
    string[]? ExcludeFields { get; set; }
}

/// <summary>Validates and converts request field selection parameters.</summary>
public static class FieldSelectionRequestExtensions
{
    /// <summary>Gets the requested selection, or null when both arrays are absent or empty.</summary>
    /// <exception cref="ModelQueryNormalizationException">Both selections are populated or a supplied field name is blank.</exception>
    public static FieldSelection? GetFieldSelection(this IFieldSelectionRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var includes = request.IncludeFields is { Length: > 0 };
        var excludes = request.ExcludeFields is { Length: > 0 };
        if (includes && excludes)
            throw new ModelQueryNormalizationException("IncludeFields and ExcludeFields cannot both be supplied.");
        if ((request.IncludeFields?.Any(string.IsNullOrWhiteSpace) ?? false) ||
            (request.ExcludeFields?.Any(string.IsNullOrWhiteSpace) ?? false))
            throw new ModelQueryNormalizationException("Field names must not be blank.");
        return includes ? FieldSelection.Include(request.IncludeFields!)
            : excludes ? FieldSelection.Exclude(request.ExcludeFields!) : null;
    }
}
