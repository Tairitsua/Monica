using System.ComponentModel;
using Microsoft.Extensions.DependencyInjection;
using Monica.Core;
using Monica.Core.Domains.Abstractions;
using Monica.Core.Domains.Models;
using Monica.Core.Modularity.Abstractions;
using Monica.Modules;
using Monica.Testing.Hosting;
using Xunit;

namespace Test.Monica.Core.Domains;

public sealed class DomainCatalogTests
{
    [Fact]
    public async Task Hosts_ShareOneImmutableCatalogAcrossConsumersAndKeepLabelsIndependent()
    {
        await using var first = await new Factory(options => options.AddEnum<BusinessDomains>()).CreateAsync(cancellationToken: TestContext.Current.CancellationToken);
        await using var second = await new Factory(options => options.Domains.Add(new DomainInfo { Id = "Ordering", DisplayName = "Second host" }))
            .CreateAsync(cancellationToken: TestContext.Current.CancellationToken);
        var catalog = first.Services.GetRequiredService<IDomainCatalog>();
        Assert.Same(catalog, first.Services.GetRequiredService<IDomainCatalog>());
        Assert.Equal("Orders", catalog.GetDisplayName("Ordering"));
        Assert.Equal("Second host", second.Services.GetRequiredService<IDomainCatalog>().GetDisplayName("Ordering"));
        Assert.Equal(new[] { "Ordering", "Remote", "SignBit" }, catalog.Domains.Keys.Order().ToArray());
        Assert.Equal("Unlisted", catalog.GetDisplayName("Unlisted"));
        Assert.False(catalog.Domains.ContainsKey("Unlisted"));
        using var scope = first.Services.CreateScope();
        Assert.Same(catalog, scope.ServiceProvider.GetRequiredService<IDomainCatalog>());
    }

    [Fact]
    public async Task DuplicateMetadata_FailsHostComposition()
    {
        await Assert.ThrowsAnyAsync<Exception>(() => new Factory(options =>
        {
            options.Domains.Add(new DomainInfo { Id = "Ordering" });
            options.Domains.Add(new DomainInfo { Id = "Ordering", DisplayName = "Conflicting" });
        }).CreateAsync(cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact]
    public void DisplayName_DefaultsToLogicalApplicationRatherThanSharedArtifact()
    {
        var options = new MonicaApplicationOptions { AppId = "orders", ProjectName = "Product.Host" };
        Assert.Equal("orders", options.ResolveAppName(fallback: "Product.Host"));
        options.AppName = "Orders API";
        Assert.Equal("Orders API", options.ResolveAppName());
        Assert.Equal("Explicit label", options.ResolveAppName("Explicit label"));
    }

    [Flags]
    private enum BusinessDomains
    {
        None = 0,
        [Description("Orders")] Ordering = 1,
        SignBit = int.MinValue,
        All = Ordering | SignBit
    }

    private sealed class Factory(Action<ModuleDomainCatalogOption> configure) : MonicaTestApplicationFactory<DomainCatalogTests>
    {
        protected override void ConfigureMonica(IMonicaBuilder builder) => builder.AddDomainCatalog(configure);
        protected override void ConfigureServices(IServiceCollection services)
        {
            base.ConfigureServices(services);
            services.AddSingleton<IDomainCatalogSource, Source>();
        }
    }

    private sealed class Source : IDomainCatalogSource
    {
        public IEnumerable<string> GetDomainIds() => ["Ordering", "Remote"];
    }
}
