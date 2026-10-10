using Monica.Configuration.Exceptions;
using Monica.Configuration.Models;

namespace Monica.Configuration.Abstractions;

/// <summary>
/// Explicitly validates a supplied instance of a locally discovered managed configuration owner.
/// </summary>
/// <remarks>
/// Register the Configuration module and complete its local type discovery before resolving this singleton service.
/// Owners must be concrete, closed classes with a public parameterless instance constructor. The generic type must
/// exactly identify the registered owner, and the supplied object must have that exact runtime type; a base type,
/// derived instance, or published metadata alone does not grant executable authority.
/// Validation shares the native schema and pure synchronous <c>IValidatableObject</c> pipeline. Object rules must
/// depend only on the supplied value and cannot resolve services or perform asynchronous work. This service neither
/// resolves or creates Microsoft options nor rebinds configuration, applies Configure/PostConfigure callbacks,
/// enforces the runtime validation policy, or records an observed options-creation attempt.
/// </remarks>
public interface IConfigurationOptionsInstanceValidationService
{
    /// <summary>Evaluates the complete native contract of the exact supplied local owner instance.</summary>
    /// <typeparam name="TOptions">The exact CLR type registered by local managed configuration discovery.</typeparam>
    /// <param name="options">The non-null owner instance to evaluate without rebinding or modification.</param>
    /// <returns>
    /// A display-safe report with <see cref="ConfigurationValidationScope.ActualOptions"/> scope and the local owner
    /// revision. Ordinary validation violations return an invalid report under either runtime policy. The caller
    /// owns any decision to reject its operation; explicit checks do not change observed creation diagnostics.
    /// </returns>
    /// <exception cref="ArgumentNullException"><paramref name="options"/> is null.</exception>
    /// <exception cref="ConfigurationValidationExecutionException">
    /// Local ownership or the exact object contract cannot be established, or binding-independent object traversal
    /// or validation execution fails. The safe fault excludes user exception text and configuration values.
    /// </exception>
    ConfigurationValidationReport Validate<TOptions>(TOptions options) where TOptions : class;
}
