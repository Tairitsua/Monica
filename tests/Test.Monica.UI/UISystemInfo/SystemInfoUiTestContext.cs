using System.Globalization;
using Bunit;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Localization;
using Monica.Testing.Localization;
using Monica.UI.Localization;
using MudBlazor;
using MudBlazor.Services;

namespace Test.Monica.UI.UISystemInfo;

internal sealed class SystemInfoUiTestContext : BunitContext
{
    internal IRenderedComponent<MudPopoverProvider> PopoverProvider { get; }
    internal IRenderedComponent<MudDialogProvider> DialogProvider { get; }

    internal SystemInfoUiTestContext(
        IStringLocalizer<SystemInfoResource>? localizer = null,
        Action<IServiceCollection>? configureServices = null)
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddMudServices();
        Services.AddOptions();
        Services.AddSingleton<global::Monica.UI.Shared.Services.MoTimeFormatter>();
        Services.AddSingleton(localizer ?? new EchoStringLocalizer<SystemInfoResource>());
        configureServices?.Invoke(Services);
        PopoverProvider = Render<MudPopoverProvider>();
        DialogProvider = Render<MudDialogProvider>();
    }
}

internal sealed class SystemInfoCultureScope : IDisposable
{
    private readonly CultureInfo _originalCulture = CultureInfo.CurrentCulture;
    private readonly CultureInfo _originalUiCulture = CultureInfo.CurrentUICulture;

    internal SystemInfoCultureScope(string cultureName)
    {
        var culture = CultureInfo.GetCultureInfo(cultureName);
        CultureInfo.CurrentCulture = culture;
        CultureInfo.CurrentUICulture = culture;
    }

    public void Dispose()
    {
        CultureInfo.CurrentCulture = _originalCulture;
        CultureInfo.CurrentUICulture = _originalUiCulture;
    }
}
