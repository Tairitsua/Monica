namespace Monica.ApplicationModel.Annotations;

/// <summary>Declares one role assembly's logical role and owning business domain.</summary>
/// <param name="roleId">The stable role identifier; dotted names do not imply activation inheritance.</param>
/// <param name="domain">The existing product domain identifier, as a string or named enum value.</param>
[AttributeUsage(AttributeTargets.Assembly)]
public sealed class ApplicationRoleAttribute(string roleId, object domain) : Attribute
{
    /// <summary>Gets the stable role identifier.</summary>
    public string RoleId { get; } = roleId;
    /// <summary>Gets the owning business domain.</summary>
    public string DomainId { get; } = DomainIdentifier.Normalize(domain);

    /// <summary>Overrides stable durable-work ownership when preserving an existing scheduler namespace.</summary>
    /// <remarks>Null uses RoleId. This value does not change when a role moves between logical applications.</remarks>
    public string? WorkloadOwner { get; set; }

    /// <summary>Names this assembly's optional concrete application registration with a public parameterless constructor.</summary>
    public Type? Registration { get; set; }
}
