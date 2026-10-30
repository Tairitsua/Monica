using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Monica.Framework.ChainTracing.Extensions;

namespace Monica.Framework.ChainTracing.Providers.EntityFrameworkCore;

/// <summary>Host-owned EF options contribution, also used by standard pooled and factory registrations.</summary>
internal sealed class ChainTracingDbContextOptionsConfiguration<TContext> : IDbContextOptionsConfiguration<TContext>
    where TContext : DbContext
{
    public void Configure(IServiceProvider serviceProvider, DbContextOptionsBuilder optionsBuilder)
        => optionsBuilder.UseChainTracing(serviceProvider);
}
