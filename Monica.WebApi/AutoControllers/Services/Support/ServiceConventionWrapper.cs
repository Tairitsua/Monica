using Microsoft.AspNetCore.Mvc.ApplicationModels;
using Microsoft.Extensions.DependencyInjection;
using Monica.WebApi.AutoControllers.Abstractions.Internal;
using MvcApplicationModel = Microsoft.AspNetCore.Mvc.ApplicationModels.ApplicationModel;

namespace Monica.WebApi.AutoControllers.Services.Support;

public class ServiceConventionWrapper(IServiceProvider services) : IApplicationModelConvention
{
    private readonly Lazy<IServiceConvention> _convention = new(services.GetRequiredService<IServiceConvention>);

    public void Apply(MvcApplicationModel application)
    {
        _convention.Value.Apply(application);
    }
}
