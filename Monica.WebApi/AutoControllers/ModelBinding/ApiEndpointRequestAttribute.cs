using Microsoft.AspNetCore.Mvc;
using Monica.Application.Annotations;

namespace Monica.WebApi.AutoControllers.ModelBinding;

/// <summary>
/// Selects Monica's composite request binder while retaining the endpoint payload source for
/// ASP.NET Core API exploration and model-binding conventions.
/// </summary>
/// <remarks>
/// This attribute is emitted by the AutoController generator. Application code declares binding
/// through <see cref="ApiEndpointAttribute.Binding"/> instead of applying this attribute directly.
/// </remarks>
[AttributeUsage(AttributeTargets.Parameter, AllowMultiple = false, Inherited = false)]
public sealed class ApiEndpointRequestAttribute : ModelBinderAttribute
{
    /// <summary>
    /// Initializes a new instance for an endpoint's resolved payload binding.
    /// </summary>
    /// <param name="binding">
    /// The resolved payload source. <see cref="ApiRequestBinding.Auto"/> is not valid because the
    /// generator resolves it from the endpoint's HTTP method before emitting the controller.
    /// </param>
    /// <exception cref="ArgumentOutOfRangeException">
    /// Thrown when <paramref name="binding"/> is <see cref="ApiRequestBinding.Auto"/> or an undefined value.
    /// </exception>
    public ApiEndpointRequestAttribute(ApiRequestBinding binding)
        : base(typeof(ApiEndpointRequestModelBinder))
    {
        Binding = binding;
        BindingSource = binding switch
        {
            ApiRequestBinding.Query => Microsoft.AspNetCore.Mvc.ModelBinding.BindingSource.Query,
            ApiRequestBinding.Body => Microsoft.AspNetCore.Mvc.ModelBinding.BindingSource.Body,
            ApiRequestBinding.Form => Microsoft.AspNetCore.Mvc.ModelBinding.BindingSource.Form,
            _ => throw new ArgumentOutOfRangeException(
                nameof(binding),
                binding,
                "The composite request binder requires a resolved Query, Body, or Form binding.")
        };
    }

    /// <summary>
    /// Gets the endpoint payload source used by the composite binder.
    /// </summary>
    public ApiRequestBinding Binding { get; }

}
