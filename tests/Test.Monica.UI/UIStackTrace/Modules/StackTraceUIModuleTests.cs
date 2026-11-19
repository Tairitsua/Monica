using System.Globalization;
using AwesomeAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Localization;
using Monica.Core.Localization.Abstractions;
using Monica.Core.Modularity;
using Monica.Core.Modularity.Abstractions;
using Monica.Modules;
using Monica.Testing.Hosting;
using Monica.UI.Localization;
using Monica.UI.UIStackTrace.Support;
using Xunit;

namespace Test.Monica.UI.UIStackTrace.Modules;

public sealed class StackTraceUIModuleTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AddStackTraceUI_WhenDirectOrTransitive_ShouldActivateLocalizedResourcesWithoutShellOrResourceAssemblyScan(bool transitive)
    {
        await using var application = await new StackTraceTestApplicationFactory(transitive).CreateAsync(
            cancellationToken: TestContext.Current.CancellationToken);

        application.ModuleSnapshots.Should().Contain(snapshot => snapshot.ModuleType == typeof(ModuleStackTraceUI))
            .And.Contain(snapshot => snapshot.ModuleType == typeof(ModuleLocalization));
        var english = ResolveTranslations(application.Services, "en-US");
        var chinese = ResolveTranslations(application.Services, "zh-CN");
        english.Should().NotBeEmpty();
        chinese.Should().NotBeEmpty().And.NotBeEquivalentTo(english);
        application.Services.GetServices<StackTraceParser>().Should().ContainSingle();
        application.ModuleSnapshots.Should().NotContain(snapshot => snapshot.ModuleType == typeof(ModuleShellUI));
    }

    [Fact]
    public async Task AddStackTraceUI_WhenHostsAndScopesDiffer_ShouldOwnParserAndResourceCachePerHost()
    {
        var factory = new StackTraceTestApplicationFactory(false);
        await using var first = await factory.CreateAsync(cancellationToken: TestContext.Current.CancellationToken);
        await using var second = await factory.CreateAsync(cancellationToken: TestContext.Current.CancellationToken);
        using var firstScope = first.Services.CreateScope();
        using var nextScope = first.Services.CreateScope();
        var parser = firstScope.ServiceProvider.GetRequiredService<StackTraceParser>();

        nextScope.ServiceProvider.GetRequiredService<StackTraceParser>().Should().BeSameAs(parser);
        second.Services.GetRequiredService<StackTraceParser>().Should().NotBeSameAs(parser);

        var firstResources = first.Services.GetRequiredService<IStringLocalizerFactory>().Create(typeof(SharedResource));
        first.Services.GetRequiredService<ILocalizationCatalog>().For<SharedResource>().Should().BeSameAs(firstResources);
        second.Services.GetRequiredService<IStringLocalizerFactory>().Create(typeof(SharedResource)).Should().NotBeSameAs(firstResources);
    }

    private static IReadOnlyList<string> ResolveTranslations(IServiceProvider services, string culture)
    {
        var previousCulture = CultureInfo.CurrentUICulture;
        try
        {
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo(culture);
            return services.GetRequiredService<IStringLocalizer<SharedResource>>()
                .GetAllStrings(includeParentCultures: false).Select(static value => value.Value).ToArray();
        }
        finally
        {
            CultureInfo.CurrentUICulture = previousCulture;
        }
    }

    private sealed class StackTraceTestApplicationFactory(bool transitive) : MonicaTestApplicationFactory<StackTraceUIModuleTests>
    {
        protected override void ConfigureMonica(IMonicaBuilder builder)
        {
            if (transitive) builder.AddModule<StackTraceConsumerModule, StackTraceConsumerModuleOption>();
            else builder.AddStackTraceUI();
        }
    }

    public sealed class StackTraceConsumerModule : MonicaModule<StackTraceConsumerModuleOption>
    {
        public override void Describe(ModuleDescriptor module) => module.Require<ModuleStackTraceUI>();
    }

    public sealed class StackTraceConsumerModuleOption : ModuleOptions<StackTraceConsumerModule>
    {
    }
}
