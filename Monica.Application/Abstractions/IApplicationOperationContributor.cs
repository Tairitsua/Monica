namespace Monica.Application.Abstractions;

/// <summary>Copies trusted application-owned caller context, such as tenant selection, into a fresh backend scope.</summary>
/// <remarks>Register in the caller scope. Do not retain the supplied provider, change registrations, or overwrite caller identity.</remarks>
public interface IApplicationOperationContributor
{
    /// <summary>Initializes context before the mediator resolves a handler. Exceptions abort the operation.</summary>
    Task InitializeAsync(IServiceProvider operationServices, CancellationToken cancellationToken);
}
