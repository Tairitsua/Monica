using System.Text;
using AwesomeAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Monica.Core.Modularity.Abstractions;
using Monica.Framework.UI.UISwagger.Models;
using Monica.Modules;
using Monica.Testing.Hosting;
using Monica.UI.Theming;
using Swashbuckle.AspNetCore.SwaggerUI;
using Test.Monica.Framework.UI.UISwagger.Support;
using Xunit;

namespace Test.Monica.Framework.UI.UISwagger.Composition;

public sealed class SwaggerUiCompositionTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Create_WhenRegisteredDirectlyOrTransitivelyWithoutShell_ShouldProvideStandaloneBootstrap(bool transitive)
    {
        var factory = new SwaggerUiTestApplicationFactory(transitive: transitive);
        await using var application = await factory.CreateAsync(cancellationToken: TestContext.Current.CancellationToken);
        using var client = CreateClient(application);

        var html = await client.GetStringAsync("/swagger/index.html", TestContext.Current.CancellationToken);
        using var configuration = SwaggerUiIndexRendererTests.ReadConfiguration(html);

        application.Application.Modules.IsRegistered(typeof(ModuleShellUI)).Should().BeFalse();
        application.Application.Modules.IsRegistered(typeof(ModuleLocalization)).Should().BeTrue();
        configuration.RootElement.GetProperty("buttons").GetArrayLength().Should().Be(0);
        configuration.RootElement.GetProperty("defaults").GetProperty("themeKind").GetInt32().Should().Be(0);
        configuration.RootElement.GetProperty("defaults").GetProperty("isDarkMode").GetBoolean().Should().BeFalse();
        html.Should().Contain("/_content/Monica.Framework.UI/UISwagger/custom.js");
    }

    [Fact]
    public async Task Create_WhenShellIsComposed_ShouldUseItsDefaultsAndEnabledOrderedNavigation()
    {
        var factory = new SwaggerUiTestApplicationFactory(
            includeShell: true,
            configureUi: options => options.NavigationButtons.AddRange(
            [
                new SwaggerNavigationButton { Name = "Second", Path = "second", Order = 10 },
                new SwaggerNavigationButton { Name = "Hidden", Path = "hidden", Enabled = false, Order = -1 },
                new SwaggerNavigationButton { Name = "First", Path = "~/first", Order = 0 }
            ]));
        await using var application = await factory.CreateAsync(cancellationToken: TestContext.Current.CancellationToken);
        using var client = CreateClient(application);

        var html = await client.GetStringAsync("/swagger/index.html", TestContext.Current.CancellationToken);
        using var configuration = SwaggerUiIndexRendererTests.ReadConfiguration(html);

        configuration.RootElement.GetProperty("defaults").GetProperty("themeKind").GetInt32()
            .Should().Be((int)MonicaThemeKind.Grove);
        configuration.RootElement.GetProperty("defaults").GetProperty("isDarkMode").GetBoolean().Should().BeTrue();
        configuration.RootElement.GetProperty("buttons").EnumerateArray()
            .Select(static button => button.GetProperty("name").GetString()).Should().Equal("First", "Second");
    }

    [Fact]
    public async Task Render_WhenCultureCookieChanges_ShouldResolveActualResourceLabelsPerRequestAndVaryHtmlCache()
    {
        var factory = new SwaggerUiTestApplicationFactory();
        await using var application = await factory.CreateAsync(cancellationToken: TestContext.Current.CancellationToken);
        using var client = CreateClient(application);
        using var chineseResponse = await client.GetAsync("/swagger/index.html", TestContext.Current.CancellationToken);
        using var chinese = SwaggerUiIndexRendererTests.ReadConfiguration(
            await chineseResponse.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        using var request = new HttpRequestMessage(HttpMethod.Get, "/swagger/index.html");
        request.Headers.Add("Cookie", ".AspNetCore.Culture=c=en-US|uic=en-US");
        using var englishResponse = await client.SendAsync(request, TestContext.Current.CancellationToken);
        using var english = SwaggerUiIndexRendererTests.ReadConfiguration(
            await englishResponse.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));

        chineseResponse.EnsureSuccessStatusCode();
        englishResponse.EnsureSuccessStatusCode();
        chinese.RootElement.GetProperty("culture").GetString().Should().Be("zh-CN");
        english.RootElement.GetProperty("culture").GetString().Should().Be("en-US");
        chinese.RootElement.GetProperty("labels").GetProperty("close").GetString().Should().Be("关闭");
        english.RootElement.GetProperty("labels").GetProperty("close").GetString().Should().Be("Close");
        englishResponse.Headers.Vary.Should().Contain("Cookie");
        factory.EffectiveOptions!.HeadContent.Should().Be("<meta name=host-content />");
    }

    [Theory]
    [InlineData("swagger", "../swagger/v1/swagger.json")]
    [InlineData("reference/api", "../../swagger/v1/swagger.json")]
    [InlineData("", "./swagger/v1/swagger.json")]
    public async Task Configure_WhenHostChangesFinalUiPrefixAndMountsApplication_ShouldResolveTheRealDocumentRoute(
        string finalPrefix,
        string expectedDocumentUrl)
    {
        var factory = new SwaggerUiTestApplicationFactory(finalPrefix: finalPrefix, mounted: true);
        await using var application = await factory.CreateAsync(cancellationToken: TestContext.Current.CancellationToken);
        using var client = CreateClient(application);
        var indexPath = $"/review-mount/{(finalPrefix.Length == 0 ? "" : finalPrefix + "/")}index.html";

        var html = await client.GetStringAsync(indexPath, TestContext.Current.CancellationToken);
        using var configuration = SwaggerUiIndexRendererTests.ReadConfiguration(html);
        var document = factory.EffectiveOptions!.ConfigObject.Urls.First();
        var documentUri = new Uri(new Uri(client.BaseAddress!, indexPath), document.Url);
        using var response = await client.GetAsync(documentUri, TestContext.Current.CancellationToken);

        document.Url.Should().Be(expectedDocumentUrl);
        documentUri.AbsolutePath.Should().Be("/review-mount/swagger/v1/swagger.json");
        response.EnsureSuccessStatusCode();
        html.Should().Contain("/review-mount/_content/Monica.Framework.UI/UISwagger/custom.js");
        configuration.RootElement.GetProperty("storage").GetProperty("moduleUrl").GetString()
            .Should().Be("/review-mount/_content/Monica.UI/js/mo-browser-storage.js");
    }

    [Fact]
    public async Task Configure_WhenHostSuppliesIndexAndRequestOptions_ShouldPreserveThemAndExternalDocumentUrl()
    {
        var factoryCalls = 0;
        const string interceptor = "function (request) { return request; }";
        var factory = new SwaggerUiTestApplicationFactory(hostSetup: options =>
        {
            options.IndexStream = () =>
            {
                factoryCalls++;
                return new MemoryStream(Encoding.UTF8.GetBytes(
                    "<html><head>%(HeadContent)</head><body>host-index:%(DocumentTitle)</body></html>"));
            };
            options.ConfigObject.Urls.First().Url = "https://consumer.example.test/openapi.json";
            options.Interceptors.RequestInterceptorFunction = interceptor;
            options.OAuthConfigObject.ClientId = "host-client";
            options.CacheLifetime = TimeSpan.FromMinutes(3);
        });
        await using var application = await factory.CreateAsync(cancellationToken: TestContext.Current.CancellationToken);
        using var client = CreateClient(application);

        using var response = await client.GetAsync("/swagger/index.html", TestContext.Current.CancellationToken);
        var html = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        using var configuration = SwaggerUiIndexRendererTests.ReadConfiguration(html);

        response.EnsureSuccessStatusCode();
        factoryCalls.Should().Be(1);
        configuration.RootElement.GetProperty("buttons").GetArrayLength().Should().Be(0);
        html.Should().Contain("host-index:").And.Contain("<meta name=host-content />");
        factory.EffectiveOptions!.ConfigObject.Urls.First().Url.Should().Be("https://consumer.example.test/openapi.json");
        factory.EffectiveOptions.Interceptors.RequestInterceptorFunction.Should().Be(interceptor);
        factory.EffectiveOptions.OAuthConfigObject.ClientId.Should().Be("host-client");
        response.Headers.CacheControl!.MaxAge.Should().Be(TimeSpan.FromMinutes(3));
    }

    [Fact]
    public async Task Create_WhenOnlyNativeSwaggerIsRegistered_ShouldKeepLocalizationAndShellOptional()
    {
        var factory = new SwaggerUiTestApplicationFactory(enhance: false);
        await using var application = await factory.CreateAsync(cancellationToken: TestContext.Current.CancellationToken);
        using var client = CreateClient(application);

        var html = await client.GetStringAsync("/swagger/index.html", TestContext.Current.CancellationToken);

        application.Application.Modules.IsRegistered(typeof(ModuleLocalization)).Should().BeFalse();
        application.Application.Modules.IsRegistered(typeof(ModuleShellUI)).Should().BeFalse();
        html.Should().NotContain("id=\"mo-swagger-config\"");
    }

    private static HttpClient CreateClient(MonicaTestApplication application) =>
        application.Services.GetRequiredService<IServer>().Should().BeOfType<TestServer>().Which.CreateClient();

    private sealed class SwaggerUiTestApplicationFactory(
        bool transitive = false,
        bool includeShell = false,
        string finalPrefix = "swagger",
        bool mounted = false,
        bool enhance = true,
        Action<ModuleSwaggerUIOption>? configureUi = null,
        Action<SwaggerUIOptions>? hostSetup = null)
        : MonicaTestApplicationFactory<ModuleSwaggerUI>
    {
        internal SwaggerUIOptions? EffectiveOptions { get; private set; }

        protected override void ConfigureHost(WebApplicationBuilder builder)
        {
            builder.Environment.ApplicationName = typeof(ModuleSwaggerUI).Assembly.GetName().Name!;
            builder.Services.AddEndpointsApiExplorer();
            if (mounted)
            {
                builder.Services.AddSingleton<IStartupFilter>(new MountedApplicationStartupFilter());
            }
        }

        protected override void ConfigureMonica(IMonicaBuilder builder)
        {
            builder.ConfigureModuleSystem(static options => options.AutoAddMonicaHttpListener = false);
            builder.AddSwagger(options =>
            {
                options.DisableXmlDocumentation = true;
                options.RoutePrefix = "initial-prefix";
                options.ExtendSwaggerUIAction = ui =>
                {
                    ui.RoutePrefix = finalPrefix;
                    ui.HeadContent = "<meta name=host-content />";
                    hostSetup?.Invoke(ui);
                    EffectiveOptions = ui;
                };
            });

            if (!enhance)
            {
                return;
            }

            if (transitive)
            {
                builder.AddModule<SwaggerUiConsumerModule, SwaggerUiConsumerModuleOption>();
            }
            else
            {
                builder.AddSwaggerUI(configureUi);
            }

            // Registration order deliberately places localization after Swagger's registration.
            // The production optional ordering edge must place its middleware before native Swagger.
            builder.AddLocalization(static options => options.DefaultCulture = "zh-CN");
            if (includeShell)
            {
                builder.AddUIShell();
                builder.AddUI(static options =>
                {
                    options.DefaultTheme = MonicaThemeKind.Grove;
                    options.DefaultDarkMode = true;
                });
            }
        }
    }

    private sealed class MountedApplicationStartupFilter : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            app.UsePathBase("/review-mount");
            next(app);
        };
    }
}

public sealed class SwaggerUiConsumerModule : MonicaModule<SwaggerUiConsumerModuleOption>
{
    public override void Describe(ModuleDescriptor module) => module.Require<ModuleSwaggerUI, ModuleSwaggerUIOption>();
}

public sealed class SwaggerUiConsumerModuleOption : ModuleOptions<SwaggerUiConsumerModule>;
