using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.DependencyInjection;
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
    IServiceProvider serviceProvider,
    IConfigurationDefinitionRegistry definitions,
    IConfigurationRuntimeValidationService validationService,
    ConfigurationValidationPolicy policy,
    ILogger<MonicaConfigurationProviderActivationHostedService> logger)
    : IHostedService
{
    /// <inheritdoc />
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (MonicaConfigurationProviderActivationCoordinator.ShouldActivate(definitions, serviceProvider))
            await serviceProvider.GetRequiredService<MonicaConfigurationProviderActivationCoordinator>().ActivateAsync(cancellationToken);
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
