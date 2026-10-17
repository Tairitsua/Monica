using System.Reflection;
using Monica.Application.Annotations;
using Monica.Core.Mediator;

namespace Monica.Application.Invocation;

/// <summary>Applies published Query semantics independently of HTTP method or receiver publication.</summary>
internal sealed class ApplicationQueryConvention : IReadOnlyRequestConvention
{
    public bool IsReadOnly(Type requestType) =>
        requestType.IsDefined(typeof(ApiEndpointAttribute), inherit: false) &&
        requestType.Name.StartsWith("Query", StringComparison.Ordinal) && requestType.Name.Length > "Query".Length;
}
