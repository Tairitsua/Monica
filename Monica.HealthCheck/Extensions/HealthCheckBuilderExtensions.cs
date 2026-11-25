using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Monica.HealthCheck.Services;

namespace Monica.HealthCheck.Extensions;

/// <summary>
/// Provides consistent registration helpers for Monica liveness and readiness checks.
/// </summary>
public static class HealthCheckBuilderExtensions
{
    /// <summary>
    /// Registers a typed liveness check and adds the stable <c>live</c> and <c>monica</c> tags.
    /// </summary>
    /// <typeparam name="THealthCheck">The health-check implementation activated by ASP.NET Core.</typeparam>
    /// <param name="builder">The host health-check builder.</param>
    /// <param name="name">A unique, stable registration name.</param>
    /// <param name="failureStatus">Optional status used when the check throws.</param>
    /// <param name="tags">Optional additional classification tags.</param>
    /// <param name="timeout">Optional execution timeout for this check.</param>
    /// <returns>The same builder for additional registrations.</returns>
    public static IHealthChecksBuilder AddMonicaLivenessCheck<THealthCheck>(
        this IHealthChecksBuilder builder,
        string name,
        HealthStatus? failureStatus = null,
        IEnumerable<string>? tags = null,
        TimeSpan? timeout = null)
        where THealthCheck : class, IHealthCheck
    {
        return AddMonicaCheck<THealthCheck>(
            builder,
            name,
            HealthCheckTags.Live,
            failureStatus,
            tags,
            timeout);
    }

    /// <summary>
    /// Registers a typed readiness check and adds the stable <c>ready</c> and <c>monica</c> tags.
    /// </summary>
    /// <remarks>With an application model, Monica resolves ownership from the check type and its registration owner.
    /// Assigned quarantined roles are short-circuited before constructing the check. Optional multi-role checks
    /// may cap failures at Degraded according to the deployment policy. A check explicitly tagged for liveness as
    /// well keeps a separate native live entry and its original failure semantics.</remarks>
    /// <typeparam name="THealthCheck">The health-check implementation activated by ASP.NET Core.</typeparam>
    /// <param name="builder">The host health-check builder.</param>
    /// <param name="name">A unique, stable registration name.</param>
    /// <param name="failureStatus">Optional status used when the check throws.</param>
    /// <param name="tags">Optional additional classification tags.</param>
    /// <param name="timeout">Optional execution timeout for this check.</param>
    /// <returns>The same builder for additional registrations.</returns>
    public static IHealthChecksBuilder AddMonicaReadinessCheck<THealthCheck>(
        this IHealthChecksBuilder builder,
        string name,
        HealthStatus? failureStatus = null,
        IEnumerable<string>? tags = null,
        TimeSpan? timeout = null)
        where THealthCheck : class, IHealthCheck
    {
        return AddMonicaCheck<THealthCheck>(
            builder,
            name,
            HealthCheckTags.Ready,
            failureStatus,
            tags,
            timeout);
    }

    private static IHealthChecksBuilder AddMonicaCheck<THealthCheck>(
        IHealthChecksBuilder builder,
        string name,
        string scopeTag,
        HealthStatus? failureStatus,
        IEnumerable<string>? tags,
        TimeSpan? timeout)
        where THealthCheck : class, IHealthCheck
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        var normalizedTags = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            scopeTag,
            HealthCheckTags.Monica
        };

        if (tags is not null)
        {
            foreach (var tag in tags.Where(static tag => !string.IsNullOrWhiteSpace(tag)))
            {
                normalizedTags.Add(tag.Trim());
            }
        }

        return builder.Add(new HealthCheckRegistration(name, new TypedHealthCheckFactory(typeof(THealthCheck)).Create,
            failureStatus, normalizedTags, timeout));
    }
}
