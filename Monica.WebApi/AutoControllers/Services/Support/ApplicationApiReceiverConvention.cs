using Microsoft.AspNetCore.Mvc.ApplicationModels;
using Monica.ApplicationModel.Abstractions;
using Monica.ApplicationModel.Annotations;
using MvcApplicationModel = Microsoft.AspNetCore.Mvc.ApplicationModels.ApplicationModel;

namespace Monica.WebApi.AutoControllers.Services.Support;

/// <summary>Selects individual generated actions before MVC creates their endpoint descriptors.</summary>
internal sealed class ApplicationApiReceiverConvention(IApplicationModel? model) : IApplicationModelConvention
{
    public void Apply(MvcApplicationModel application)
    {
        foreach (var controller in application.Controllers.ToArray())
        {
            if (model?.FindRole(controller.ControllerType.Assembly) is { } owner && !model.IsRoleActive(owner.Id))
            {
                application.Controllers.Remove(controller);
                continue;
            }
            foreach (var action in controller.Actions.ToArray())
            {
                if (action.Attributes.OfType<ApplicationApiReceiverAttribute>().SingleOrDefault() is not { } receiver) continue;
                if (model is null)
                    throw new InvalidOperationException("Generated application receivers require AddApplicationModel before endpoints are mapped.");
                var operation = model.GetOperation(receiver.RequestType);
                if (!model.IsRoleActive(operation.RequiredRole ?? operation.RoleId))
                    controller.Actions.Remove(action);
            }
        }
    }
}
