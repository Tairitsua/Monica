namespace Monica.Invocation.Abstractions;

/// <summary>Carries optional HTTP headers for adapters that explicitly forward caller transport metadata.</summary>
/// <remarks>Header values are untrusted input and do not establish caller identity or authority.</remarks>
public interface IHasApiHttpInfo
{
    /// <summary>Gets or sets the optional headers. No dictionary is allocated until headers are supplied.</summary>
    public Dictionary<string, string?>? Headers { get; set; }

    /// <summary>Adds a header only when its name is absent, preserving the dictionary's comparer.</summary>
    public bool TryAddHeader(string name, string? value = null)
    {
        Headers ??= new Dictionary<string, string?>();
        return Headers.TryAdd(name, value);
    }

    /// <summary>Returns whether a header with the given name is present.</summary>
    public bool IsHeaderExist(string name)
    {
        return Headers?.ContainsKey(name) is true;
    }

    /// <summary>Reads a header and reports whether its name is present.</summary>
    public bool TryGetHeader(string name, out string? value)
    {
        value = null;
        return Headers?.TryGetValue(name, out value) is true;
    }
    /// <summary>Reads a header, returning null when absent or explicitly set to null.</summary>
    public string? GetHeaderValue(string name)
    {
        if (Headers?.TryGetValue(name, out var value) is true) return value;
        return null;
    }
}
