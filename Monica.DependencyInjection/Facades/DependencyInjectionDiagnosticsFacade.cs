using Microsoft.Extensions.Logging;
using Microsoft.Extensions.DependencyInjection;
using Monica.Core.Extensions;
using Monica.Core.Results;
using Monica.DependencyInjection.Models;
using Monica.DependencyInjection.Services;

namespace Monica.DependencyInjection.Facades;

/// <summary>
/// Query-oriented facade for dependency-injection diagnostics consumers such as UI pages.
/// </summary>
public sealed class DependencyInjectionDiagnosticsFacade(
    IServiceProvider serviceProvider,
    ILogger<DependencyInjectionDiagnosticsFacade> logger)
{
    /// <summary>
    /// Gets the finalized dependency-injection diagnostics snapshot.
    /// </summary>
    public Task<Result<DependencyInjectionDiagnosticsSnapshot>> GetSnapshotAsync()
    {
        try
        {
            var diagnosticsService = serviceProvider.GetRequiredService<DependencyInjectionDiagnosticsService>();
            return Task.FromResult<Result<DependencyInjectionDiagnosticsSnapshot>>(Result.Success(diagnosticsService.GetSnapshot()));
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to build the dependency-injection diagnostics snapshot.");
            return Task.FromResult<Result<DependencyInjectionDiagnosticsSnapshot>>(Result.Failure<DependencyInjectionDiagnosticsSnapshot>(
                $"Failed to build the dependency-injection diagnostics snapshot: {ex.GetMessageRecursively()}"));
        }
    }
}
