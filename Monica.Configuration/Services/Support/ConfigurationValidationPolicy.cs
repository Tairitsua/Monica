using Monica.Configuration.Models;

namespace Monica.Configuration.Services.Support;

/// <summary>Captures the existing startup/options policy without resolving mutable options during validation.</summary>
internal sealed record ConfigurationValidationPolicy(ConfigurationRuntimeValidationBehavior Behavior);
