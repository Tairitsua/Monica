namespace Monica.BuildingBlocks.Models;

/// <summary>
/// Enumeration value information Dto
/// </summary>
public class DtoEnumValue
{
    /// <summary>
    /// index
    /// </summary>
    public int Index { get; set; }

    /// <summary>
    /// name
    /// </summary>
    public string Name { get; set; } = null!;

    /// <summary>
    /// describe
    /// </summary>
    public string? Description { get; set; }
}

/// <summary>
/// Enumeration information Dto
/// </summary>
public class DtoEnumInfo
{
    /// <summary>
    /// enum name
    /// </summary>
    public string Name { get; set; } = null!;

    /// <summary>
    /// List of enumeration values
    /// </summary>
    public List<DtoEnumValue> Values { get; set; } = [];
}

/// <summary>
/// Assembly enumeration information Dto
/// </summary>
public class DtoAssemblyEnumInfo
{
    /// <summary>
    /// Assembly source
    /// </summary>
    public string? From { get; set; }

    /// <summary>
    /// enumeration list
    /// </summary>
    public List<DtoEnumInfo> Enums { get; set; } = [];
}