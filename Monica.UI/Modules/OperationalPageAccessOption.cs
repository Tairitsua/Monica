namespace Monica.Modules;

/// <summary>
/// Configures the shared access boundary for Monica operational pages and protected diagnostics surfaces.
/// </summary>
public sealed class OperationalPageAccessOption
{
    /// <summary>
    /// Gets or sets the default host authorization policy evaluated outside the Development environment.
    /// The default is <see langword="null"/>. Individual operational pages may override this value. A missing or blank
    /// effective policy denies access outside Development unless <see cref="DebugMode"/> is enabled.
    /// </summary>
    public string? AuthorizationPolicy { get; set; }

    /// <summary>
    /// Gets or sets whether all Monica operational access checks bypass authorization in every environment.
    /// The default is <see langword="false"/>.
    /// </summary>
    /// <remarks>
    /// This is an authorization bypass intended for explicitly trusted diagnostic hosts. It is independent from
    /// <see cref="ModuleUIHostOption.EnableDebug"/>, which controls detailed Blazor and SignalR errors.
    /// </remarks>
    public bool DebugMode { get; set; }

    internal string? ResolveAuthorizationPolicy(string? authorizationPolicyOverride)
    {
        return Normalize(authorizationPolicyOverride) ?? Normalize(AuthorizationPolicy);
    }

    private static string? Normalize(string? policyName) =>
        string.IsNullOrWhiteSpace(policyName) ? null : policyName.Trim();
}
