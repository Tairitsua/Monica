using Bunit;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Localization;
using Monica.Core.Results;
using Monica.Framework.UI.Localization;
using Monica.Framework.UI.UIProjectUnits.Services;
using Monica.ProjectUnits.Models;
using Monica.Testing.Localization;
using MudBlazor;
using MudBlazor.Services;

namespace Test.Monica.Framework.UI.ProjectUnits;

internal sealed class ProjectUnitsUiTestContext : BunitContext
{
    internal IRenderedComponent<MudDialogProvider> DialogProvider { get; }

    internal ProjectUnitsUiTestContext(IProjectUnitsUiDataSource dataSource)
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddMudServices();
        Services.AddOptions<global::Monica.Modules.ModuleShellUIOption>();
        Services.AddOptions<global::Monica.Modules.ModuleClockOption>();
        Services.AddSingleton<global::Monica.UI.Shared.Services.MoTimeFormatter>();
        Services.AddSingleton<IStringLocalizer<ProjectUnitsResource>, EchoStringLocalizer<ProjectUnitsResource>>();
        Services.AddSingleton(dataSource);
        _ = Render<MudPopoverProvider>();
        DialogProvider = Render<MudDialogProvider>();
    }
}

internal sealed class StubProjectUnitsUiDataSource(
    Res<ProjectUnitDashboardSnapshot> dashboardResult,
    Res<ProjectUnitDetail>? detailResult = null)
    : IProjectUnitsUiDataSource
{
    public Task<Res<ProjectUnitDashboardSnapshot>> GetDashboardAsync()
    {
        return Task.FromResult(dashboardResult);
    }

    public Task<Res<ProjectUnitDetail>> GetDetailAsync(
        string key,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return detailResult is not null
            ? Task.FromResult(detailResult)
            : Task.FromResult<Res<ProjectUnitDetail>>(Res.Fail("No detail configured."));
    }
}
