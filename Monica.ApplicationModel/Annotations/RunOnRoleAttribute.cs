namespace Monica.ApplicationModel.Annotations;

/// <summary>Requires an application request to execute on a host actively owning the specified role.</summary>
/// <param name="roleId">The role owning the required runtime resources or execution responsibility.</param>
/// <remarks>Ordinary requests need no annotation and remain locally callable when their implementation is included, independently of ingress ownership.</remarks>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct, Inherited = false)]
public sealed class RunOnRoleAttribute(string roleId) : Attribute
{
    /// <summary>Gets the required role identifier.</summary>
    public string RoleId { get; } = roleId;
}
