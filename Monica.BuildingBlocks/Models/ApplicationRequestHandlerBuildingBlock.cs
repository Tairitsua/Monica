using Microsoft.Extensions.Logging;
using Monica.Core.Mediator;
using Monica.Core.TypeDiscovery.Models;
using Monica.BuildingBlocks.Services.Support;
using Monica.Modules;
using Monica.Tool.Extensions;
using Monica.Application.Abstractions;

namespace Monica.BuildingBlocks.Models;

/// <summary>
/// Describes one application request handler, its exact mediator contract, and its architectural dependencies.
/// </summary>
public class ApplicationRequestHandlerBuildingBlock : BuildingBlock
{
    internal ApplicationRequestHandlerBuildingBlock(BusinessTypeShape shape, BuildingBlockCatalog catalog)
        : base(shape, BuildingBlockKind.ApplicationRequestHandler, catalog, MediatorExecutionPoints.Request)
    {
    }

    /// <summary>
    /// Gets or sets whether generated HTTP exposure should be disabled when endpoint generation is implemented.
    /// </summary>
    [Obsolete("Not implemented. Automatic HTTP endpoint generation is required before this flag can take effect.")]
    public bool IsDisabled { get; set; }

    /// <summary>
    /// Gets or sets whether the application request handler represents a write operation.
    /// </summary>
    public bool IsCommand { get; set; }

    /// <summary>
    /// Gets whether the application request handler represents a read operation.
    /// </summary>
    public bool IsQuery => IsCommand == false;

    /// <summary>
    /// Gets the request type accepted by the application request handler.
    /// </summary>
    public Type RequestType { get; private set; } = null!;

    /// <summary>
    /// Gets the exact full mediator response declared by the handler, including any result envelope.
    /// </summary>
    public Type ResponseType { get; private set; } = null!;

    protected override bool ShouldAnalyzeConstructorDependencies => true;

    protected override BuildingBlockNamingRule? DefaultConventionOption()
    {
        return new BuildingBlockNamingRule
        {
            Contains = "Handler"
        };
    }

    internal static BuildingBlock Create(
        BusinessTypeShape shape,
        BuildingBlockCatalog catalog,
        Type handlerBase)
    {
        var unit = new ApplicationRequestHandlerBuildingBlock(shape, catalog)
        {
            IsCommand = shape.Type.Name.StartsWith("Command", StringComparison.Ordinal),
            RequestType = handlerBase.GetGenericArguments()[0],
            ResponseType = handlerBase.GetGenericArguments()[1]
        };
        unit.CheckMode();

        unit.InitializeMethods<ApplicationService>();
        return unit;
    }

    public override void DoingConnect()
    {
        if (Catalog.FindByFullName(RequestType.FullName) is not { } requestUnit)
        {
            var alertMessage =
                $"Application-request-handler building block {this} could not associate request type " +
                $"{RequestType.GetCleanFullName()} because no request building block was discovered. Ensure the " +
                $"request implements a supported {nameof(IResultRequest)} or IRequest<TResponse> contract and is included in the host's " +
                "business-type discovery.";
            Alerts.Add(new BuildingBlockAlert
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

        base.DoingConnect();
    }
}
