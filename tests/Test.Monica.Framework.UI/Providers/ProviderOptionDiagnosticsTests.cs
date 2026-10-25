using System.Collections.Immutable;
using System.Globalization;
using System.Security.Claims;
using AngleSharp.Dom;
using AwesomeAssertions;
using Bunit;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Options;
using Monica.Core.Modularity.Diagnostics.Models;
using Monica.Core.Modularity.Models;
using Monica.EventBus.Abstractions;
using Monica.Framework.UI.Localization;
using Monica.Framework.UI.UIEventBus.Components;
using Monica.Framework.UI.UIEventBus.Dialogs;
using Monica.Framework.UI.UIEventBus.Models;
using Monica.Modules;
using Monica.StateStore.Abstractions;
using Monica.StateStore.UI.Localization;
using Monica.StateStore.UI.Models;
using Monica.StateStore.UI.UIStateStore.Components;
using Monica.Testing.Localization;
using Monica.UI.Localization;
using Monica.UI.Shell.Support;
using Monica.UI.UIModuleSystem.Components;
using MudBlazor.Services;
using Xunit;

namespace Test.Monica.Framework.UI.Providers;

public sealed class ProviderOptionDiagnosticsTests
{
    [Fact]
    public async Task EventBus_provider_card_WhenDiagnosticsTargetExists_ShouldShowConfigurationCapability()
    {
        await using var context = CreateContext();
        var provider = new EventBusProviderInfo
        {
            ProviderType = EventBusProviderKind.Dapr,
            IsDistributed = true,
            ImplementationType = "DaprEventBusProvider",
            OptionDiagnosticsTarget = new ModuleOptionDiagnosticsTarget(
                ModuleKey.FromModuleType(typeof(ModuleEventBus)),
                ModuleOptionProfileSelector.NamedOrDefault("orders"))
        };

        var cut = context.Render<EventBusProviderCard>(parameters => parameters
            .Add(component => component.Provider, provider));

        cut.Markup.Should().Contain("Providers:Capabilities:HasOptions");
        provider.OptionDiagnosticsTarget!.ProfileSelector.Mode
            .Should().Be(ModuleOptionProfileSelectionMode.NamedOrDefault);
    }

    [Fact]
    public async Task EventBus_provider_card_WhenDiagnosticsTargetIsAbsent_ShouldHideConfigurationCapability()
    {
        await using var context = CreateContext();
        var provider = new EventBusProviderInfo
        {
            ProviderType = EventBusProviderKind.Unknown,
            IsDistributed = true,
            ImplementationType = "CustomEventBusProvider"
        };

        var cut = context.Render<EventBusProviderCard>(parameters => parameters
            .Add(component => component.Provider, provider));

        cut.Markup.Should().NotContain("Providers:Capabilities:HasOptions");
    }

    [Fact]
    public async Task StateStore_provider_card_WhenDiagnosticsTargetExists_ShouldShowConfigurationCapability()
    {
        await using var context = CreateContext();
        var provider = new StateStoreProviderInfo
        {
            ProviderType = EStateStoreProviderType.Redis,
            IsDistributed = true,
            ImplementationType = "RedisStateStore",
            OptionDiagnosticsTarget = new ModuleOptionDiagnosticsTarget(
                ModuleKey.FromModuleType(typeof(ModuleStateStore)),
                ModuleOptionProfileSelector.Default)
        };

        var cut = context.Render<StateStoreProviderCard>(parameters => parameters
            .Add(component => component.Provider, provider));

        cut.Markup.Should().Contain("ProviderCard:Labels:HasConfiguration");
        provider.OptionDiagnosticsTarget!.ProfileSelector.Mode
            .Should().Be(ModuleOptionProfileSelectionMode.Default);
    }

    [Fact]
    public async Task StateStore_provider_card_WhenDiagnosticsTargetIsAbsent_ShouldHideConfigurationCapability()
    {
        await using var context = CreateContext();
        var provider = new StateStoreProviderInfo
        {
            ProviderType = EStateStoreProviderType.Memory,
            ImplementationType = "MemoryCacheProvider"
        };

        var cut = context.Render<StateStoreProviderCard>(parameters => parameters
            .Add(component => component.Provider, provider));

        cut.Markup.Should().NotContain("ProviderCard:Labels:HasConfiguration");
    }

    [Fact]
    public async Task EventBus_provider_dialog_WhenConfigurationTabHasNotOpened_ShouldNotInvokeDiagnosticsBoundary()
    {
        await using var context = CreateContext();
        var surface = context.Render<MudBlazor.MudDialogProvider>();
        _ = context.Render<EventBusProviderDetailDialog>(parameters => parameters
            .Add(component => component.IsVisible, true)
            .Add(component => component.Provider, CreateEventBusProvider()));
        surface.WaitForAssertion(() => FindTab(surface, "Providers:Detail:Tabs:Options").Should().NotBeNull());

        surface.Markup.Should().NotContain("Providers:Detail:Options:LoadFailed");

        FindTab(surface, "Providers:Detail:Tabs:Options").Click();

        surface.WaitForAssertion(() =>
            surface.Markup.Should().Contain("Providers:Detail:Options:LoadFailed"));
    }

    [Fact]
    public async Task EventBus_provider_dialog_WhenAccessIsDenied_ShouldNotReachFacadeResolution()
    {
        await using var context = CreateContext(Environments.Production);
        var surface = context.Render<MudBlazor.MudDialogProvider>();
        _ = context.Render<EventBusProviderDetailDialog>(parameters => parameters
            .Add(component => component.IsVisible, true)
            .Add(component => component.Provider, CreateEventBusProvider()));
        surface.WaitForAssertion(() => FindTab(surface, "Providers:Detail:Tabs:Options").Should().NotBeNull());

        FindTab(surface, "Providers:Detail:Tabs:Options").Click();

        surface.WaitForAssertion(() =>
        {
            surface.Markup.Should().Contain("Providers:Detail:Options:AccessDenied");
            surface.Markup.Should().NotContain("Providers:Detail:Options:LoadFailed");
        });
    }

    [Fact]
    public async Task EventBus_provider_dialog_WhenOperationalDebugModeIsEnabled_ShouldBypassProductionPolicy()
    {
        var shellOptions = new ModuleShellUIOption();
        shellOptions.OperationalPageAccess.DebugMode = true;
        await using var context = CreateContext(Environments.Production, shellOptions);
        var surface = context.Render<MudBlazor.MudDialogProvider>();
        _ = context.Render<EventBusProviderDetailDialog>(parameters => parameters
            .Add(component => component.IsVisible, true)
            .Add(component => component.Provider, CreateEventBusProvider()));
        surface.WaitForAssertion(() => FindTab(surface, "Providers:Detail:Tabs:Options").Should().NotBeNull());

        FindTab(surface, "Providers:Detail:Tabs:Options").Click();

        surface.WaitForAssertion(() =>
        {
            surface.Markup.Should().Contain("Providers:Detail:Options:LoadFailed");
            surface.Markup.Should().NotContain("Providers:Detail:Options:AccessDenied");
        });
    }

    [Fact]
    public async Task EventBus_provider_dialog_WhenLeavingConfigurationTab_ShouldClearAndReauthorizeOnReturn()
    {
        var authorization = new CountingAuthorizationService();
        await using var context = CreateContextWithPolicy(authorization);
        var surface = context.Render<MudBlazor.MudDialogProvider>();
        _ = context.Render<EventBusProviderDetailDialog>(parameters => parameters
            .Add(component => component.IsVisible, true)
            .Add(component => component.Provider, CreateEventBusProvider()));
        surface.WaitForAssertion(() => FindTab(surface, "Providers:Detail:Tabs:Options").Should().NotBeNull());

        FindTab(surface, "Providers:Detail:Tabs:Options").Click();
        surface.WaitForAssertion(() => authorization.CallCount.Should().Be(1));

        FindTab(surface, "Providers:Detail:Tabs:Info").Click();
        FindTab(surface, "Providers:Detail:Tabs:Options").Click();

        surface.WaitForAssertion(() => authorization.CallCount.Should().Be(2));
    }

    [Fact]
    public async Task StateStore_provider_dialog_WhenConfigurationTabHasNotOpened_ShouldLoadOnlyAfterExplicitSelection()
    {
        await using var context = CreateContext();
        var surface = context.Render<MudBlazor.MudDialogProvider>();
        _ = context.Render<StateStoreProviderDetailDialog>(parameters => parameters
            .Add(component => component.IsVisible, true)
            .Add(component => component.Provider, CreateStateStoreProvider()));
        surface.WaitForAssertion(() => FindTab(surface, "ProviderDetail:Tabs:Options").Should().NotBeNull());

        surface.Markup.Should().NotContain("ProviderDetail:Messages:ConfigurationLoadFailed");

        FindTab(surface, "ProviderDetail:Tabs:Options").Click();

        surface.WaitForAssertion(() =>
            surface.Markup.Should().Contain("ProviderDetail:Messages:ConfigurationLoadFailed"));
    }

    [Fact]
    public async Task StateStore_provider_dialog_WhenAccessIsDenied_ShouldNotReachFacadeResolution()
    {
        await using var context = CreateContext(Environments.Production);
        var surface = context.Render<MudBlazor.MudDialogProvider>();
        _ = context.Render<StateStoreProviderDetailDialog>(parameters => parameters
            .Add(component => component.IsVisible, true)
            .Add(component => component.Provider, CreateStateStoreProvider()));
        surface.WaitForAssertion(() => FindTab(surface, "ProviderDetail:Tabs:Options").Should().NotBeNull());

        FindTab(surface, "ProviderDetail:Tabs:Options").Click();

        surface.WaitForAssertion(() =>
        {
            surface.Markup.Should().Contain("ProviderDetail:Messages:ConfigurationAccessDenied");
            surface.Markup.Should().NotContain("ProviderDetail:Messages:ConfigurationLoadFailed");
        });
    }

    [Fact]
    public async Task StateStore_provider_dialog_WhenLeavingConfigurationTab_ShouldClearAndReauthorizeOnReturn()
    {
        var authorization = new CountingAuthorizationService();
        await using var context = CreateContextWithPolicy(authorization);
        var surface = context.Render<MudBlazor.MudDialogProvider>();
        _ = context.Render<StateStoreProviderDetailDialog>(parameters => parameters
            .Add(component => component.IsVisible, true)
            .Add(component => component.Provider, CreateStateStoreProvider()));
        surface.WaitForAssertion(() => FindTab(surface, "ProviderDetail:Tabs:Options").Should().NotBeNull());

        FindTab(surface, "ProviderDetail:Tabs:Options").Click();
        surface.WaitForAssertion(() => authorization.CallCount.Should().Be(1));

        FindTab(surface, "ProviderDetail:Tabs:Overview").Click();
        FindTab(surface, "ProviderDetail:Tabs:Options").Click();

        surface.WaitForAssertion(() => authorization.CallCount.Should().Be(2));
    }

    [Fact]
    public async Task Option_diagnostics_panel_WhenRedactedOrRevealed_ShouldRenderTheMatchingSecurityState()
    {
        await using var context = CreateContext();
        var moduleKey = ModuleKey.FromModuleType(typeof(ModuleStateStore));
        var redacted = CreateDiagnostics(
            moduleKey,
            ModuleOptionDiagnosticsExposureMode.Redacted,
            ModuleOptionDiagnosticValueKind.Presence,
            value: null);
        var revealed = CreateDiagnostics(
            moduleKey,
            ModuleOptionDiagnosticsExposureMode.RevealSensitive,
            ModuleOptionDiagnosticValueKind.Value,
            value: "local-debug-secret");

        var protectedPanel = context.Render<ModuleOptionDiagnosticsPanel>(parameters => parameters
            .Add(component => component.Diagnostics, redacted));
        protectedPanel.Markup.Should().Contain("ModuleDrawer:Options:Present");
        protectedPanel.Markup.Should().NotContain("local-debug-secret");

        var revealedPanel = context.Render<ModuleOptionDiagnosticsPanel>(parameters => parameters
            .Add(component => component.Diagnostics, revealed));
        revealedPanel.Markup.Should().Contain("ModuleDrawer:Options:RevealPolicyTitle");
        revealedPanel.Markup.Should().Contain("local-debug-secret");
    }

    [Fact]
    public void StateStore_provider_labels_WhenCultureChanges_ShouldComeFromTheUILocalizer()
    {
        var provider = new StateStoreProviderInfo { ProviderType = EStateStoreProviderType.Memory };
        IStringLocalizer english = new DictionaryStringLocalizer(new Dictionary<string, string>
        {
            ["KeyExplorer:Labels:DefaultProvider"] = "Default",
            ["ProviderDetail:ProviderTypes:Memory"] = "In-memory"
        });
        IStringLocalizer chinese = new DictionaryStringLocalizer(new Dictionary<string, string>
        {
            ["KeyExplorer:Labels:DefaultProvider"] = "默认",
            ["ProviderDetail:ProviderTypes:Memory"] = "内存缓存"
        });

        english.GetProviderDisplayName(provider).Should().Be("Default");
        english.GetProviderTypeText(provider.ProviderType).Should().Be("In-memory");
        chinese.GetProviderDisplayName(provider).Should().Be("默认");
        chinese.GetProviderTypeText(provider.ProviderType).Should().Be("内存缓存");
    }

    private static EventBusProviderInfo CreateEventBusProvider() => new()
    {
        ProviderType = EventBusProviderKind.Dapr,
        IsDistributed = true,
        ImplementationType = "DaprEventBusProvider",
        OptionDiagnosticsTarget = new ModuleOptionDiagnosticsTarget(
            ModuleKey.FromModuleType(typeof(ModuleEventBus)),
            ModuleOptionProfileSelector.Default)
    };

    private static StateStoreProviderInfo CreateStateStoreProvider() => new()
    {
        ProviderType = EStateStoreProviderType.Redis,
        IsDistributed = true,
        ImplementationType = "RedisStateStore",
        OptionDiagnosticsTarget = new ModuleOptionDiagnosticsTarget(
            ModuleKey.FromModuleType(typeof(ModuleStateStore)),
            ModuleOptionProfileSelector.Default)
    };

    private static ModuleOptionDiagnostics CreateDiagnostics(
        ModuleKey moduleKey,
        ModuleOptionDiagnosticsExposureMode exposureMode,
        ModuleOptionDiagnosticValueKind kind,
        string? value)
    {
        return new ModuleOptionDiagnostics
        {
            ModuleKey = moduleKey,
            OptionTypeName = "SampleProviderOptions",
            ExposureMode = exposureMode,
            IsFinalized = true,
            SensitiveEntryCount = 1,
            ContainsRevealedSensitiveValues = exposureMode == ModuleOptionDiagnosticsExposureMode.RevealSensitive,
            Entries = ImmutableArray.Create(new ModuleOptionDiagnosticEntry
            {
                Name = "ApiKey",
                Path = "ApiKey",
                TypeName = "System.String",
                Kind = kind,
                Value = value,
                IsPresent = kind == ModuleOptionDiagnosticValueKind.Presence,
                IsSensitive = true
            })
        };
    }

    private static IElement FindTab<TComponent>(IRenderedComponent<TComponent> cut, string text)
        where TComponent : Microsoft.AspNetCore.Components.IComponent
    {
        return cut.FindAll("[role='tab']")
            .Single(tab => tab.TextContent.Contains(text, StringComparison.Ordinal));
    }

    private static BunitContext CreateContext(
        string environmentName = "Development",
        ModuleShellUIOption? shellOptions = null)
    {
        var context = new BunitContext();
        context.JSInterop.Mode = JSRuntimeMode.Loose;
        context.Services.AddMudServices();
        context.Services.AddSingleton<IHostEnvironment>(new ProviderTestHostEnvironment(environmentName));
        context.Services.AddSingleton<IOptions<ModuleShellUIOption>>(
            Options.Create(shellOptions ?? new ModuleShellUIOption()));
        context.Services.AddScoped<OperationalPageAccessEvaluator>();
        context.Services.AddOptions<global::Monica.Modules.ModuleShellUIOption>();
        context.Services.AddOptions<global::Monica.Modules.ModuleClockOption>();
        context.Services.AddSingleton<global::Monica.UI.Shared.Services.MoTimeFormatter>();
        context.Services.AddSingleton<IStringLocalizer<EventBusResource>, EchoStringLocalizer<EventBusResource>>();
        context.Services.AddSingleton<IStringLocalizer<StateStoreResource>, EchoStringLocalizer<StateStoreResource>>();
        context.Services.AddSingleton<IStringLocalizer<ModuleSystemResource>, EchoStringLocalizer<ModuleSystemResource>>();
        return context;
    }

    private static BunitContext CreateContextWithPolicy(CountingAuthorizationService authorization)
    {
        var shellOptions = new ModuleShellUIOption();
        shellOptions.OperationalPageAccess.AuthorizationPolicy = "module-diagnostics";
        var context = CreateContext(Environments.Production, shellOptions);
        context.Services.AddAuthorizationCore(options => options.AddPolicy(
            "module-diagnostics",
            policy => policy.RequireAssertion(_ => true)));
        context.Services.AddSingleton<AuthenticationStateProvider>(new ProviderAuthenticationStateProvider());
        context.Services.AddSingleton<IAuthorizationService>(authorization);
        return context;
    }

    private sealed class ProviderTestHostEnvironment(string environmentName) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = environmentName;
        public string ApplicationName { get; set; } = "Test.Monica.Framework.UI";
        public string ContentRootPath { get; set; } = "/test";
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }

    private sealed class ProviderAuthenticationStateProvider : AuthenticationStateProvider
    {
        public override Task<AuthenticationState> GetAuthenticationStateAsync()
        {
            var identity = new ClaimsIdentity(
                [new Claim(ClaimTypes.Name, "diagnostics-reviewer")],
                "test");
            return Task.FromResult(new AuthenticationState(new ClaimsPrincipal(identity)));
        }
    }

    private sealed class CountingAuthorizationService : IAuthorizationService
    {
        public int CallCount { get; private set; }

        public Task<AuthorizationResult> AuthorizeAsync(
            ClaimsPrincipal user,
            object? resource,
            IEnumerable<IAuthorizationRequirement> requirements)
        {
            CallCount++;
            return Task.FromResult(AuthorizationResult.Success());
        }

        public Task<AuthorizationResult> AuthorizeAsync(
            ClaimsPrincipal user,
            object? resource,
            string policyName)
        {
            CallCount++;
            return Task.FromResult(AuthorizationResult.Success());
        }
    }

    private sealed class DictionaryStringLocalizer(IReadOnlyDictionary<string, string> values) : IStringLocalizer
    {
        public LocalizedString this[string name] => new(
            name,
            values.TryGetValue(name, out var value) ? value : name,
            !values.ContainsKey(name));

        public LocalizedString this[string name, params object[] arguments]
        {
            get
            {
                var template = values.TryGetValue(name, out var value) ? value : name;
                return new LocalizedString(
                    name,
                    string.Format(CultureInfo.InvariantCulture, template, arguments),
                    !values.ContainsKey(name));
            }
        }

        public IEnumerable<LocalizedString> GetAllStrings(bool includeParentCultures) =>
            values.Select(pair => new LocalizedString(pair.Key, pair.Value, false));

        public IStringLocalizer WithCulture(CultureInfo culture) => this;
    }
}
