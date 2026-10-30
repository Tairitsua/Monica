using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Monica.Framework.ChainTracing.Providers.EntityFrameworkCore;

namespace Monica.Framework.ChainTracing.Extensions;

/// <summary>Explicit attachment for manually constructed or third-party physical DbContext options.</summary>
public static class ChainTracingDbContextOptionsExtensions
{
    /// <summary>Adds connection, command, and reader tracing once to these options.</summary>
    /// <remarks>
    /// The host must select UseEntityFrameworkCoreTracing on its ChainTracing registration first.
    /// Standard EF DI registrations attach automatically. Call this only in adapters which construct options
    /// outside that pipeline. Interceptors are host-owned and never capture scoped application services.
    /// This method preserves all existing interceptors, provider settings, connections, and transaction ownership.
    /// </remarks>
    /// <exception cref="InvalidOperationException">The host has not selected EF Core chain tracing.</exception>
    public static DbContextOptionsBuilder UseChainTracing(this DbContextOptionsBuilder options,
        IServiceProvider services)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(services);
        var command = services.GetService<ChainTracingDbCommandInterceptor>()
            ?? throw new InvalidOperationException("Select AddChainTracing().UseEntityFrameworkCoreTracing() on the host before attaching database tracing.");
        var connection = services.GetRequiredService<ChainTracingDbConnectionInterceptor>();
        var existing = options.Options.FindExtension<CoreOptionsExtension>()?.Interceptors?.ToArray() ?? [];
        var additions = new List<IInterceptor>(2);
        if (!existing.Contains(connection)) additions.Add(connection);
        if (!existing.Contains(command)) additions.Add(command);
        if (additions.Count != 0) options.AddInterceptors(additions);
        return options;
    }

    /// <summary>Adds host-owned database tracing while retaining the typed options builder.</summary>
    /// <remarks>Uses the same prerequisites, idempotence, and ownership as the non-generic overload.</remarks>
    public static DbContextOptionsBuilder<TContext> UseChainTracing<TContext>(
        this DbContextOptionsBuilder<TContext> options, IServiceProvider services) where TContext : DbContext
    {
        UseChainTracing((DbContextOptionsBuilder)options, services);
        return options;
    }
}
