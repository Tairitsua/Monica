using Monica.Core.TypeDiscovery.Models;
using Monica.Modules;
using Monica.ProjectUnits.Services.Support;
using Monica.Application.Abstractions;
using Monica.WebApi.Abstractions;

namespace Monica.ProjectUnits.Models;

/// <summary>
/// Domain services
/// </summary>
public class UnitDomainService : ProjectUnit
{
    internal UnitDomainService(BusinessTypeShape shape, ProjectUnitCatalog catalog)
        : base(shape, EProjectUnitType.DomainService, catalog)
    {
    }

    protected override bool ShouldAnalyzeConstructorDependencies => true;
    protected override ProjectUnitNamingRule? DefaultConventionOption()
    {
        return new ProjectUnitNamingRule
        {
            Prefix = "Domain"
        };
    }

    internal static ProjectUnit Create(BusinessTypeShape shape, ProjectUnitCatalog catalog)
    {
        var unit = new UnitDomainService(shape, catalog);
        unit.CheckNameConventionMode();
        unit.InitializeMethods<DomainService>();
        return unit;
    }
}
