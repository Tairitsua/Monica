using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Monica.Configuration.Abstractions;
using Monica.Configuration.Exceptions;
using Monica.Configuration.Services.Support;
using Monica.Configuration.Models;

namespace Monica.Configuration.Services;

/// <summary>
/// Connects the module-owned Microsoft configuration provider to the final application service provider.
/// </summary>
internal sealed class MonicaConfigurationProviderActivationHostedService(
    MonicaConfigurationProviderActivationCoordinator activationCoordinator,
    IConfigurationRuntimeValidationService validationService,
    ConfigurationValidationPolicy policy,
    ILogger<MonicaConfigurationProviderActivationHostedService> logger)
    : IHostedService
{
    /// <inheritdoc />
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        await activationCoordinator.ActivateAsync(cancellationToken);
        var report = validationService.GetReport();
        if (report.IsValid) return;
        if (policy.Behavior == ConfigurationRuntimeValidationBehavior.FailFast)
            throw new ConfigurationRuntimeValidationException(report);
        logger.LogWarning("{ConfigurationRuntimeValidationDiagnostic}",
            ConfigurationRuntimeValidationMessageFormatter.FormatDiagnosticReport(report));
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }
}
