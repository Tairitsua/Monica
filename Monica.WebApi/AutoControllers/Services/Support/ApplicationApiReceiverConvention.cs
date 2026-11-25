using Microsoft.AspNetCore.Mvc.ApplicationModels;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.Extensions.DependencyInjection;
using Monica.ApplicationModel.Abstractions;
using Monica.ApplicationModel.Annotations;
using Monica.Core.ApplicationRoles;
using Monica.Core.Results;
using Monica.Core.Results.Services;
using MvcApplicationModel = Microsoft.AspNetCore.Mvc.ApplicationModels.ApplicationModel;

namespace Monica.WebApi.AutoControllers.Services.Support;

/// <summary>Selects individual generated actions before MVC creates their endpoint descriptors.</summary>
internal sealed class ApplicationApiReceiverConvention(IApplicationModel? model, IRoleHealthStateProvider? health) : IApplicationModelConvention
{
    public void Apply(MvcApplicationModel application)
    {
        foreach (var controller in application.Controllers.ToArray())
        {
            var owner = model?.FindRole(controller.ControllerType.Assembly);
            if (owner is not null && !model!.IsRoleActive(owner.Id))
            {
                application.Controllers.Remove(controller);
                continue;
            }
            foreach (var action in controller.Actions.ToArray())
            {
                var roleId = owner?.Id;
                if (action.Attributes.OfType<ApplicationApiReceiverAttribute>().SingleOrDefault() is { } receiver)
                {
                    if (model is null)
                        throw new InvalidOperationException("Generated application receivers require AddApplicationModel before endpoints are mapped.");
                    var operation = model.GetOperation(receiver.RequestType);
                    roleId = operation.RequiredRole ?? operation.RoleId;
                    if (!model.IsRoleActive(roleId))
                    {
                        controller.Actions.Remove(action);
                        continue;
                    }
                }
                if (roleId is not null && health is not null)
                    action.Filters.Add(new RoleAdmissionFilter(health, roleId));
            }
        }
    }

    // Resource filters run after authorization and before binding the request or activating its controller.
    private sealed class RoleAdmissionFilter(IRoleHealthStateProvider health, string roleId) : IResourceFilter
    {
        public void OnResourceExecuting(ResourceExecutingContext context)
        {
            if (health.IsRoleFaulted(roleId))
            {
                context.HttpContext.RequestServices.GetService<IRoleQuarantineRequestMetrics>()?.RecordHttpRejection(roleId);
                context.Result = ResultHttpProjection.ToMvcResult(Result.Failure(
                    RoleUnavailableException.MessageForRole(roleId), ResultStatus.ServiceUnavailable));
            }
        }

        public void OnResourceExecuted(ResourceExecutedContext context) { }
    }
}
