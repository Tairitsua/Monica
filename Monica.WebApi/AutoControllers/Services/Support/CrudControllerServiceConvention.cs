using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ActionConstraints;
using Microsoft.AspNetCore.Mvc.ApplicationModels;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Monica.ApplicationModel.Abstractions;
using Monica.Tool.Extensions;
using Monica.Tool.Helpers;
using Monica.WebApi.Annotations;
using Monica.WebApi.AutoControllers.Abstractions;
using Monica.WebApi.AutoControllers.Abstractions.Internal;
using Monica.WebApi.AutoControllers.Models;
using MvcApplicationModel = Microsoft.AspNetCore.Mvc.ApplicationModels.ApplicationModel;

namespace Monica.WebApi.AutoControllers.Services.Support;

/// <summary>
/// Convention applied only to auto-generated CRUD controllers.
/// </summary>
/// <param name="conventionalRouteBuilder">Builds conventional routes for generated controllers.</param>
/// <param name="httpMethodResolver">Resolves host-specific action-name and HTTP method conventions.</param>
/// <param name="logger">The logger instance.</param>
/// <param name="options">Auto CRUD controller configuration.</param>
/// <param name="applicationModel">Optional validated ownership used to expand the domain route token.</param>
public class CrudControllerServiceConvention(
    IConventionalRouteBuilder conventionalRouteBuilder,
    IConventionalHttpMethodResolver httpMethodResolver,
    ILogger<CrudControllerServiceConvention> logger,
    IOptions<CrudControllerOption> options,
    IApplicationModel? applicationModel = null)
    : IServiceConvention
{

    public CrudControllerOption CrudControllerOption => options.Value;
    public ILogger<CrudControllerServiceConvention> Logger => logger;

    protected IConventionalRouteBuilder ConventionalRouteBuilder { get; } = conventionalRouteBuilder;

    public void Apply(MvcApplicationModel application)
    {
        ApplyForControllers(application);
    }
    protected virtual void ApplyForControllers(MvcApplicationModel application)
    {
        foreach (var controller in GetControllers(application))
        {
            var controllerType = controller.ControllerType.AsType();

            if (!controllerType.IsImplementInterface<ICrudApplicationService>())
            {
                continue;
            }
            controller.ControllerName = controller.ControllerName.RemovePostFix(CrudControllerOption.CrudControllerPostfix);

            ConfigureCrudController(controller, new ConventionalControllerSetting());
        }
    }


    protected virtual IList<ControllerModel> GetControllers(MvcApplicationModel application)
    {
        return application.Controllers;
    }

    protected virtual void ConfigureCrudController(ControllerModel controller, ConventionalControllerSetting? configuration)
    {
        ConfigureApiExplorer(controller);
        ConfigureSelector(controller, configuration);
        ConfigureParameters(controller);
    }
    #region Controller API Visibility and Action Cleanup

    protected virtual void ConfigureApiExplorer(ControllerModel controller)
    {

        if (controller.ApiExplorer.IsVisible is not false)
        {
            RemoveDisableAction(controller.Actions, controller.ControllerType.GetInterface(nameof(ICrudDisableDelete)) != null);
            RemoveDuplicateActon(controller.Actions);
        }

        foreach (var action in controller.Actions)
        {
            ConfigureApiExplorer(action);
        }
    }
    protected virtual void ConfigureApiExplorer(ActionModel action)
    {
        if (action.ApiExplorer.IsVisible != null)
        {
            return;
        }
    }


    private static void RemoveDisableAction(ICollection<ActionModel> actionModels, bool disableDelete)
    {
        var removeList = new List<ActionModel>();
        foreach (var actionModel in actionModels)
        {
            if (actionModel.Parameters.Any(a => a.ParameterInfo.ParameterType == typeof(CrudDisableDto)))
            {
                removeList.Add(actionModel);
            }
            else if (actionModel.ActionName.Contains("Delete") && disableDelete)
            {
                removeList.Add(actionModel);
            }
        }


        foreach (var actionModel in removeList)
        {
            actionModels.Remove(actionModel);
        }
    }

    private static void RemoveDuplicateActon(ICollection<ActionModel> actionModels)
    {
        var removeList = new List<ActionModel>();
        foreach (var grouping in actionModels.GroupBy(p => p.ActionName).Where(p => p.Count() > 1))
        {

            removeList.AddRange(grouping.OrderByDescending(p =>
                ((OverrideServiceAttribute?) p.Attributes.FirstOrDefault(a =>
                    a.GetType() == typeof(OverrideServiceAttribute)))?.Order ?? int.MinValue).Skip(1));
        }

        foreach (var actionModel in removeList)
        {
            actionModels.Remove(actionModel);
        }

     
    }

    #endregion

   

    #region Controller Parameter Adjustments
    protected virtual void ConfigureParameters(ControllerModel controller)
    {
        /* Default binding system of Asp.Net Core for a parameter
         * 1. Form values
         * 2. Route values.
         * 3. Query string.
         */

        foreach (var action in controller.Actions)
        {
            foreach (var prm in action.Parameters)
            {
                if (prm.BindingInfo != null)
                {
                    continue;
                }
                if (!TypeHelper.IsPrimitiveExtended(prm.ParameterInfo.ParameterType, includeEnums: true))
                {
                    if (CanUseFormBodyBinding(action, prm))
                    {
                        prm.BindingInfo = BindingInfo.GetBindingInfo([new FromBodyAttribute()]);
                    }
                }
            }
        }
    }

    protected virtual bool CanUseFormBodyBinding(ActionModel action, ParameterModel parameter)
    {
        //We want to use "id" as path parameter, not body!
        if (parameter.ParameterName == "id")
        {
            return false;
        }

        foreach (var selector in action.Selectors)
        {
            foreach (var actionConstraint in selector.ActionConstraints)
            {
                var httpMethodActionConstraint = actionConstraint as HttpMethodActionConstraint;
                if (httpMethodActionConstraint == null)
                {
                    continue;
                }

                if (httpMethodActionConstraint.HttpMethods.All(hm => hm.IsIn("GET", "DELETE", "TRACE", "HEAD")))
                {
                    return false;
                }
            }
        }

        return true;
    }


    #endregion


    #region Controller and Action Selector Configuration

    protected virtual void ConfigureSelector(ControllerModel controller, ConventionalControllerSetting? configuration)
    {
        RemoveEmptySelectors(controller.Selectors);


        // Check whether the controller defines a RouteAttribute; if it does, use it instead of the global root path.
        var controllerRouteAttribute = controller.Attributes.OfType<RouteAttribute>().FirstOrDefault();
        // When a controller already has [Route], generate a relative path.

        // Configure the route prefix for the endpoint.
        var rootPath = controllerRouteAttribute == null ? options.Value.RoutePath : "";
        if (rootPath.Contains("{domain}", StringComparison.Ordinal))
        {
            var assembly = controller.ControllerType.Assembly;
            var domain = applicationModel?.FindRole(assembly)?.DomainId
                ?? applicationModel?.Domains.Values.SingleOrDefault(candidate => candidate.AssemblyName == assembly.GetName().Name)?.Id;
            if (domain is null)
                throw new InvalidOperationException($"CRUD route '{{domain}}' requires a declared application domain or role for controller '{controller.ControllerType.FullName}'.");
            rootPath = rootPath.Replace("{domain}", domain, StringComparison.Ordinal);
        }

        foreach (var action in controller.Actions)
        {
            ConfigureSelector(rootPath, controller.ControllerName, action, configuration);
        }
    }

    protected virtual void ConfigureSelector(string rootPath, string controllerName, ActionModel action, ConventionalControllerSetting? configuration)
    {
        RemoveEmptySelectors(action.Selectors);

        if (!action.Selectors.Any())
        {
            AddMoServiceSelector(rootPath, controllerName, action, configuration);
        }
        else
        {
            NormalizeSelectorRoutes(rootPath, controllerName, action, configuration);
        }
    }

    protected virtual void AddMoServiceSelector(string rootPath, string controllerName, ActionModel action, ConventionalControllerSetting? configuration)
    {
        var httpMethod = SelectHttpMethod(action, configuration);

        var abpServiceSelectorModel = new SelectorModel
        {
            AttributeRouteModel = CreateMoServiceAttributeRouteModel(rootPath, controllerName, action, httpMethod, configuration),
            ActionConstraints = { new HttpMethodActionConstraint([httpMethod]) }
        };

        action.Selectors.Add(abpServiceSelectorModel);
    }

    protected virtual void NormalizeSelectorRoutes(string rootPath, string controllerName, ActionModel action, ConventionalControllerSetting? configuration)
    {
        foreach (var selector in action.Selectors)
        {
            var httpMethod = selector.ActionConstraints
                .OfType<HttpMethodActionConstraint>()
                .FirstOrDefault()?
                .HttpMethods?
                .FirstOrDefault() ?? SelectHttpMethod(action, configuration);

            selector.AttributeRouteModel ??= CreateMoServiceAttributeRouteModel(rootPath, controllerName, action, httpMethod, configuration);

            if (!selector.ActionConstraints.OfType<HttpMethodActionConstraint>().Any())
            {
                selector.ActionConstraints.Add(new HttpMethodActionConstraint([httpMethod]));
            }
        }
    }

    /// <summary>
    /// Infers the HTTP method from the action name.
    /// </summary>
    /// <param name="action">The action model to inspect.</param>
    /// <param name="configuration">Optional conventional controller settings.</param>
    /// <returns>The inferred HTTP method.</returns>
    protected virtual string SelectHttpMethod(ActionModel action, ConventionalControllerSetting? configuration)
    {
        return httpMethodResolver.Resolve(action.ActionName);
    }

    /// <summary>
    /// Creates the route metadata used for the generated endpoint.
    /// </summary>
    /// <param name="rootPath">The root path prefix.</param>
    /// <param name="controllerName">The controller name.</param>
    /// <param name="action">The action model.</param>
    /// <param name="httpMethod">The resolved HTTP method.</param>
    /// <param name="configuration">Optional conventional controller settings.</param>
    /// <returns>The generated route model.</returns>
    protected virtual AttributeRouteModel CreateMoServiceAttributeRouteModel(string rootPath, string controllerName, ActionModel action, string httpMethod, ConventionalControllerSetting? configuration)
    {
        return new AttributeRouteModel(
            new RouteAttribute(
                ConventionalRouteBuilder.Build(rootPath, controllerName, action, httpMethod, configuration)
            )
        );
    }

    protected virtual void RemoveEmptySelectors(IList<SelectorModel> selectors)
    {
        selectors
            .Where(IsEmptySelector)
            .ToList()
            .ForEach(s => selectors.Remove(s));
    }

    protected virtual bool IsEmptySelector(SelectorModel selector)
    {
        return selector.AttributeRouteModel == null
               && selector.ActionConstraints.IsNullOrEmptySet()
               && selector.EndpointMetadata.IsNullOrEmptySet();
    }
    #endregion




}



//public static string GetMethodSignature(MethodInfo method)
//{
//    var parameters = method.GetParameters();
//    return
//        $"{method.Name}:{method.IsGenericMethod}:{parameters.Length}:{parameters.Select(p => p.ParameterType.Name).StringJoin(',')}";
//}
