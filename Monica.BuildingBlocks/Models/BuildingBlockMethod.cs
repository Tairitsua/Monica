using System.Reflection;
using Monica.Tool.Extensions;

namespace Monica.BuildingBlocks.Models;

/// <summary>
/// Building block method metadata model
/// </summary>
public class BuildingBlockMethod
{
    /// <summary>
    /// method definition information
    /// </summary>
    public required MethodInfo MethodInfo { get; set; }
    
    /// <summary>
    /// method name
    /// </summary>
    public string MethodName => MethodInfo.Name;
    
    /// <summary>
    /// Method description (obtained from XML annotation, or empty if none)
    /// </summary>
    public string? Description { get; set; }
    
    /// <summary>
    /// The number of exceptions that have been generated (subject to improvement, default is empty)
    /// </summary>
    public int? ExceptionCount { get; set; }
    
    /// <summary>
    /// Average method time consumption statistics (milliseconds) (subject to improvement, default is empty)
    /// </summary>
    public double? AverageExecutionTimeMs { get; set; }
    
    /// <summary>
    /// method signature
    /// </summary>
    public string MethodSignature => GetMethodSignature();
    
    /// <summary>
    /// Method parameter information
    /// </summary>
    public ParameterInfo[] Parameters => MethodInfo.GetParameters();
    
    /// <summary>
    /// Return type
    /// </summary>
    public Type ReturnType => MethodInfo.ReturnType;
    
    /// <summary>
    /// Get method signature string
    /// </summary>
    /// <returns></returns>
    private string GetMethodSignature()
    {
        var parameters = Parameters;
        var parameterStrings = parameters.Select(p => $"{p.ParameterType.GetCleanName()} {p.Name}");
        return $"{ReturnType.GetCleanName()} {MethodName}({string.Join(", ", parameterStrings)})";
    }
    
    public override string ToString()
    {
        return $"{MethodSignature} - {Description ?? "No description."}";
    }
}
