using Monica.Core.Execution.Mvc;
using Monica.Core.TypeDiscovery.Models;
using Monica.ProjectUnits.Services.Support;
using Monica.Application.Abstractions;
using Monica.WebApi.Abstractions;
using Monica.WebApi.AutoControllers.Abstractions;

namespace Monica.ProjectUnits.Models;

/// <summary>
/// CRUD application services that participate in automatic controller generation (implement
/// <see cref="ICrudApplicationService"/>).
/// </summary>
/// <remarks>
/// These services also derive from <see cref="ApplicationService"/>, but they are modelled as their own project-unit
/// type so their naming convention is owned by the project-unit system rather than the web layer. The web layer
/// (Monica.WebApi) holds no naming rule for them; configure the convention through
/// <c>ModuleProjectUnitsOption.ConventionOptions.Dict[EProjectUnitType.CrudApplicationService]</c>.
/// </remarks>
public class UnitCrudApplicationService : ProjectUnit
{
    internal UnitCrudApplicationService(BusinessTypeShape shape, ProjectUnitCatalog catalog)
        : base(shape, EProjectUnitType.CrudApplicationService, catalog, MvcExecutionPoints.Action)
    {
    }

    protected override bool ShouldAnalyzeConstructorDependencies => true;

    internal static ProjectUnit Create(BusinessTypeShape shape, ProjectUnitCatalog catalog)
    {
        var unit = new UnitCrudApplicationService(shape, catalog);
        unit.CheckNameConventionMode();
        unit.InitializeMethods<ApplicationService>();
        return unit;
    }
}
