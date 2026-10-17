using System.Globalization;
using System.Reflection;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.AspNetCore.Mvc.ModelBinding.Validation;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.Routing.Patterns;

namespace Monica.WebApi.AutoControllers.ModelBinding;

/// <summary>
/// Binds an AutoController request from its declared payload source and then overlays values owned
/// by route placeholders before ASP.NET Core performs outer model validation.
/// </summary>
/// <remarks>
/// Payload binding is delegated to ASP.NET Core's configured model binders and input formatters.
/// Route values are bound independently and take precedence over conflicting query, body, or form
/// values. Positional record requests are reconstructed through their MVC-bound constructor so
/// immutable request contracts are supported without requiring mutable route properties.
/// </remarks>
/// <param name="metadataProvider">Provides MVC metadata for request and route-property types.</param>
/// <param name="modelBinderFactory">Creates the built-in binders for each declared source.</param>
public sealed class ApiEndpointRequestModelBinder(
    IModelMetadataProvider metadataProvider,
    IModelBinderFactory modelBinderFactory) : IModelBinder
{
    /// <inheritdoc />
    public async Task BindModelAsync(ModelBindingContext bindingContext)
    {
        ArgumentNullException.ThrowIfNull(bindingContext);

        var payloadSource = ResolvePayloadSource(bindingContext.BindingSource);
        var requestMetadata = metadataProvider.GetMetadataForType(bindingContext.ModelType);
        var payloadBindingInfo = new BindingInfo { BindingSource = payloadSource };
        var payloadBinder = modelBinderFactory.CreateBinder(new ModelBinderFactoryContext
        {
            Metadata = requestMetadata,
            BindingInfo = payloadBindingInfo
        });
        var payloadContext = DefaultModelBindingContext.CreateBindingContext(
            bindingContext.ActionContext,
            bindingContext.ValueProvider,
            requestMetadata,
            payloadBindingInfo,
            bindingContext.ModelName);
        payloadContext.ValidationState = bindingContext.ValidationState;

        await payloadBinder.BindModelAsync(payloadContext);
        if (!payloadContext.Result.IsModelSet || payloadContext.Result.Model is null)
        {
            bindingContext.Result = payloadContext.Result;
            return;
        }

        // The generated receiver owns the resolved route. Neutral contracts need no HTTP attribute at runtime.
        var route = bindingContext.ActionContext.ActionDescriptor.AttributeRouteInfo?.Template ?? string.Empty;
        var routeValues = await BindRouteValuesAsync(bindingContext, requestMetadata, route);
        if (routeValues is null)
        {
            bindingContext.Result = ModelBindingResult.Failed();
            return;
        }

        if (!TryOverlayRouteValues(
                bindingContext,
                requestMetadata,
                payloadContext.Result.Model,
                routeValues,
                out var request))
        {
            bindingContext.Result = ModelBindingResult.Failed();
            return;
        }

        bindingContext.ValidationState[request] = new ValidationStateEntry
        {
            Metadata = requestMetadata
        };
        bindingContext.Result = ModelBindingResult.Success(request);
    }

    private static BindingSource ResolvePayloadSource(BindingSource? source)
    {
        if (source == BindingSource.Query)
        {
            return BindingSource.Query;
        }

        if (source == BindingSource.Body)
        {
            return BindingSource.Body;
        }

        if (source == BindingSource.Form)
        {
            return BindingSource.Form;
        }

        throw new InvalidOperationException(
            $"The composite request binder requires a Query, Body, or Form binding source, but received '{source?.DisplayName ?? "none"}'.");
    }

    private async Task<Dictionary<string, RoutePropertyValue>?> BindRouteValuesAsync(
        ModelBindingContext bindingContext,
        ModelMetadata requestMetadata,
        string route)
    {
        var routeValueProvider = new RouteValueProvider(
            BindingSource.Path,
            bindingContext.ActionContext.RouteData.Values,
            CultureInfo.InvariantCulture);
        var values = new Dictionary<string, RoutePropertyValue>(StringComparer.OrdinalIgnoreCase);

        foreach (var placeholder in RoutePatternFactory.Parse(route).Parameters
                     .Select(static parameter => parameter.Name)
                     .Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!bindingContext.ActionContext.RouteData.Values.ContainsKey(placeholder))
            {
                continue;
            }

            var property = requestMetadata.Properties.FirstOrDefault(metadata =>
                string.Equals(metadata.PropertyName, placeholder, StringComparison.OrdinalIgnoreCase));
            if (property is null)
            {
                bindingContext.ModelState.TryAddModelError(
                    bindingContext.ModelName,
                    $"Route placeholder '{placeholder}' does not match a public request property.");
                return null;
            }

            ClearPayloadModelState(bindingContext, property);
            var routeBindingInfo = new BindingInfo { BindingSource = BindingSource.Path };
            var routeBinder = modelBinderFactory.CreateBinder(new ModelBinderFactoryContext
            {
                Metadata = property,
                BindingInfo = routeBindingInfo
            });
            var routeContext = DefaultModelBindingContext.CreateBindingContext(
                bindingContext.ActionContext,
                routeValueProvider,
                property,
                routeBindingInfo,
                placeholder);
            routeContext.ValidationState = bindingContext.ValidationState;

            await routeBinder.BindModelAsync(routeContext);
            if (!routeContext.Result.IsModelSet)
            {
                return null;
            }

            values[placeholder] = new RoutePropertyValue(property, routeContext.Result.Model);
        }

        return values;
    }

    private static void ClearPayloadModelState(ModelBindingContext bindingContext, ModelMetadata property)
    {
        var propertyName = property.BinderModelName ?? property.PropertyName;
        if (string.IsNullOrEmpty(propertyName))
        {
            return;
        }

        bindingContext.ModelState.Remove(propertyName);
        bindingContext.ModelState.Remove(ModelNames.CreatePropertyModelName(
            bindingContext.ModelName,
            propertyName));
    }

    private static bool TryOverlayRouteValues(
        ModelBindingContext bindingContext,
        ModelMetadata requestMetadata,
        object payload,
        IReadOnlyDictionary<string, RoutePropertyValue> routeValues,
        out object request)
    {
        request = payload;
        if (routeValues.Count == 0)
        {
            return true;
        }

        var constructor = requestMetadata.BoundConstructor;
        if (constructor?.BoundConstructorInvoker is not null)
        {
            if (!TryReconstructRecord(bindingContext, requestMetadata, constructor, payload, routeValues, out request))
            {
                return false;
            }
        }

        foreach (var (placeholder, routeValue) in routeValues)
        {
            if (routeValue.Metadata.PropertySetter is not null)
            {
                try
                {
                    routeValue.Metadata.PropertySetter(request, routeValue.Value);
                }
                catch (Exception exception)
                {
                    AddOverlayError(bindingContext, placeholder, exception);
                    return false;
                }

                continue;
            }

            if (constructor is null || !ConstructorOwnsProperty(constructor, routeValue.Metadata))
            {
                bindingContext.ModelState.TryAddModelError(
                    placeholder,
                    $"Route-bound request property '{routeValue.Metadata.PropertyName}' must be writable or mapped to the request's bound constructor.");
                return false;
            }
        }

        return true;
    }

    private static bool TryReconstructRecord(
        ModelBindingContext bindingContext,
        ModelMetadata requestMetadata,
        ModelMetadata constructor,
        object payload,
        IReadOnlyDictionary<string, RoutePropertyValue> routeValues,
        out object request)
    {
        var parameters = constructor.BoundConstructorParameters;
        var arguments = new object?[parameters?.Count ?? 0];
        if (parameters is null)
        {
            request = payload;
            return true;
        }

        for (var index = 0; index < parameters.Count; index++)
        {
            var parameter = parameters[index];
            var parameterName = parameter.ParameterName;
            if (parameterName is not null && routeValues.TryGetValue(parameterName, out var routeValue))
            {
                arguments[index] = routeValue.Value;
                continue;
            }

            var property = requestMetadata.Properties.FirstOrDefault(candidate =>
                string.Equals(candidate.PropertyName, parameterName, StringComparison.OrdinalIgnoreCase) &&
                candidate.ModelType == parameter.ModelType);
            var propertyGetter = property?.PropertyGetter;
            if (propertyGetter is null)
            {
                request = payload;
                bindingContext.ModelState.TryAddModelError(
                    bindingContext.ModelName,
                    $"Cannot reconstruct immutable request '{requestMetadata.ModelType}' because constructor parameter '{parameterName}' has no readable property.");
                return false;
            }

            arguments[index] = propertyGetter(payload);
        }

        try
        {
            request = constructor.BoundConstructorInvoker!(arguments);
            CopyNonConstructorProperties(requestMetadata, constructor, payload, request, routeValues);
            return true;
        }
        catch (Exception exception)
        {
            request = payload;
            AddOverlayError(bindingContext, bindingContext.ModelName, exception);
            return false;
        }
    }

    private static void CopyNonConstructorProperties(
        ModelMetadata requestMetadata,
        ModelMetadata constructor,
        object payload,
        object request,
        IReadOnlyDictionary<string, RoutePropertyValue> routeValues)
    {
        foreach (var property in requestMetadata.Properties)
        {
            if (property.PropertyName is null ||
                routeValues.ContainsKey(property.PropertyName) ||
                ConstructorOwnsProperty(constructor, property) ||
                property.PropertyGetter is null ||
                property.PropertySetter is null)
            {
                continue;
            }

            property.PropertySetter(request, property.PropertyGetter(payload));
        }
    }

    private static bool ConstructorOwnsProperty(ModelMetadata constructor, ModelMetadata property)
    {
        return constructor.BoundConstructorParameters?.Any(parameter =>
                   string.Equals(parameter.ParameterName, property.PropertyName, StringComparison.OrdinalIgnoreCase) &&
                   parameter.ModelType == property.ModelType)
               == true;
    }

    private static void AddOverlayError(
        ModelBindingContext bindingContext,
        string modelName,
        Exception exception)
    {
        bindingContext.ModelState.TryAddModelError(
            modelName,
            exception is TargetInvocationException { InnerException: not null }
                ? exception.InnerException
                : exception,
            bindingContext.ModelMetadata);
    }

    private readonly record struct RoutePropertyValue(ModelMetadata Metadata, object? Value);
}
