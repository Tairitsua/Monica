using Microsoft.Extensions.Logging;
using Monica.Core.Mediator;
using Monica.Core.TypeDiscovery.Models;
using Monica.ProjectUnits.Services.Support;
using Monica.Modules;
using Monica.Tool.Extensions;
using Monica.Application.Abstractions;
using Monica.WebApi.Abstractions;

namespace Monica.ProjectUnits.Models;

/// <summary>
/// Describes an application service, its request contract, and its architectural dependencies.
/// </summary>
public class UnitApplicationService : ProjectUnit
{
    internal UnitApplicationService(BusinessTypeShape shape, ProjectUnitCatalog catalog)
        : base(shape, EProjectUnitType.ApplicationService, catalog, MediatorExecutionPoints.Request)
    {
    }

    /// <summary>
    /// Gets or sets whether generated HTTP exposure should be disabled when endpoint generation is implemented.
    /// </summary>
    [Obsolete("Not implemented. Automatic HTTP endpoint generation is required before this flag can take effect.")]
    public bool IsDisabled { get; set; }

    /// <summary>
    /// Gets or sets whether the application service represents a write operation.
    /// </summary>
    public bool IsCommand { get; set; }

    /// <summary>
    /// Gets whether the application service represents a read operation.
    /// </summary>
    public bool IsQuery => IsCommand == false;

    /// <summary>
    /// Gets or sets the request type accepted by a generic application service.
    /// </summary>
    public Type? RequestType { get; set; }

    /// <summary>
    /// Gets or sets the payload type for a typed result wrapper, the explicit result type for a custom application
    /// service, or <see langword="null"/> for a response-less result wrapper.
    /// </summary>
    public Type? ResponseType { get; set; }

    protected override bool ShouldAnalyzeConstructorDependencies => true;

    protected override ProjectUnitNamingRule? DefaultConventionOption()
    {
        return new ProjectUnitNamingRule
        {
            Contains = "Handler"
        };
    }

    internal static ProjectUnit Create(BusinessTypeShape shape, ProjectUnitCatalog catalog)
    {
        var unit = new UnitApplicationService(shape, catalog)
        {
            IsCommand = shape.Type.Name.StartsWith("Command", StringComparison.Ordinal)
        };
        unit.CheckNameConventionMode();

        var applicationServiceBase = shape.BaseTypes.FirstOrDefault(type =>
            type.IsGenericType
            && (type.GetGenericTypeDefinition() == typeof(ApplicationService<,>)
                || type.GetGenericTypeDefinition() == typeof(ApplicationService<>)))
            ?? shape.BaseTypes.FirstOrDefault(type =>
                type.IsGenericType && type.GetGenericTypeDefinition() == typeof(CustomApplicationService<,>));
        if (applicationServiceBase is not null)
        {
            var args = applicationServiceBase.GetGenericArguments();
            unit.RequestType = args[0];
            unit.ResponseType = args.Length == 2 ? args[1] : null;
        }

        unit.InitializeMethods<ApplicationService>();
        return unit;
    }

    public override void DoingConnect()
    {
        if (RequestType is not null)
        {
            if (Catalog.FindByFullName(RequestType.FullName) is not { } requestUnit)
            {
                var alertMessage =
                    $"Application-service project unit {this} could not associate request type " +
                    $"{RequestType.GetCleanFullName()} because no request project unit was discovered. Ensure the " +
                    $"request implements a supported {nameof(IResultRequest)} contract and is included in the host's " +
                    "business-type discovery.";
                Alerts.Add(new ProjectUnitAlert
                {
                    Level = EAlertLevel.Warning,
                    Message = alertMessage,
                    Source = "RequestTypeAssociation"
                });
                Logger.LogWarning("{RequestTypeAssociationAlert}", alertMessage);
            }
            else
            {
                DeclareRelevance(requestUnit, true);
                requestUnit.DeclareRelevance(this);
            }
        }

        base.DoingConnect();
    }
}
