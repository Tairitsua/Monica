using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Monica.Core.ExceptionHandling.Abstractions;
using Monica.Core.ExceptionHandling.Services;
using Monica.Core.Modularity;
using Monica.Core.Modularity.Abstractions;
using Monica.Tool.Extensions;
using Monica.Core.Results;

// ReSharper disable once CheckNamespace
namespace Monica.Modules;

public static class ModuleExceptionHandlingBuilderExtensions
{
    extension(IMonicaBuilder builder)
    {
        /// <summary>
        /// Configures the exception handling module.
        /// </summary>
        public ModuleRegistration<ModuleExceptionHandling, ModuleExceptionHandlingOption> AddExceptionHandling(
            Action<ModuleExceptionHandlingOption>? action = null)
        {
            return builder.AddModule<ModuleExceptionHandling, ModuleExceptionHandlingOption>(action);
        }
    }

    extension(ModuleRegistration<ModuleExceptionHandling, ModuleExceptionHandlingOption> registration)
    {
        public ModuleRegistration<ModuleExceptionHandling, ModuleExceptionHandlingOption> AddExceptionMapper<TMapper>()
            where TMapper : class, IExceptionResponseMapper
        {
            return registration.Configure(options => options.AddExceptionMapper<TMapper>());
        }
    }
}

public class ModuleExceptionHandling : MonicaModule<ModuleExceptionHandlingOption>, IWebHostRequiredModule
{
    /// <inheritdoc />
    public override void Describe(ModuleDescriptor module)
    {
        module.Require<ModuleResultEnvelope, ModuleResultEnvelopeOption>();
    }
    /// <summary>
    /// Adds ASP.NET Core exception handling and structured request-rejection responses.
    /// </summary>
    public override void ConfigureApplicationBuilder(WebModuleContext<ModuleExceptionHandlingOption> context)
    {
        // Minimal API body binding returns 413 and 415 directly instead of throwing, so translate only those
        // otherwise-empty framework responses into the same result envelope used for thrown binding failures.
        context.ApplicationBuilder.UseStatusCodePages(async statusCodeContext =>
        {
            // Preserve the existing narrow empty-413/415 policy. Routing can reject content types using a
            // synthetic 415 endpoint before the original endpoint's Monica metadata becomes available.
            var response = statusCodeContext.HttpContext.Response;
            var rejection = response.StatusCode switch
            {
                StatusCodes.Status413PayloadTooLarge => Result.Failure(
                    "The request payload is too large.",
                    ResultStatus.PayloadTooLarge),
                StatusCodes.Status415UnsupportedMediaType => Result.Failure(
                    "The request content type is not supported.",
                    ResultStatus.UnsupportedMediaType),
                _ => null
            };

            if (rejection is not null)
            {
                rejection.SetError(new ResultError(ResultErrorCodes.InvalidRequest,
                    ResultTraceId.Capture(statusCodeContext.HttpContext)));
                await response.WriteAsJsonAsync(rejection, statusCodeContext.HttpContext.RequestAborted);
            }
        });
        context.ApplicationBuilder.UseExceptionHandler();
    }

    public override void ConfigureServices(ModuleContext<ModuleExceptionHandlingOption> context)
    {
        var services = context.Services;
        services.AddHttpContextAccessor();
        // The default IProblemDetailsService selects the first writer that supports this endpoint.
        services.Insert(0, ServiceDescriptor.Singleton<IProblemDetailsWriter, MonicaValidationProblemDetailsWriter>());
        services.AddProblemDetails();
        services.AddSingleton<IExceptionHandlerService, ExceptionHandlerService>();
        services.AddSingleton<IRequestRejectionFactory, RequestRejectionFactory>();
        services.AddTransient<IExceptionResponseMapper, BadHttpRequestExceptionMapper>();
        foreach (var mapperType in Option.ExceptionMapperTypes)
        {
            services.AddTransient(typeof(IExceptionResponseMapper), mapperType);
        }

        services.AddExceptionHandler<AspNetCoreExceptionHandler>();

        // Minimal API binding otherwise writes an empty 400 response outside Development before endpoint code runs.
        services.Configure<RouteHandlerOptions>(options => options.ThrowOnBadRequest = true);
        services.Configure<MvcOptions>(RequestBindingMessages.Configure);

        services.PostConfigure<ApiBehaviorOptions>(options =>
        {
            options.InvalidModelStateResponseFactory = context =>
                Monica.Core.Results.Services.ResultHttpProjection.ToMvcResult(context.HttpContext.RequestServices
                    .GetRequiredService<IRequestRejectionFactory>().FromModelState(context).ToResult());
        });
    }
}

public class ModuleExceptionHandlingOption : ModuleOptions<ModuleExceptionHandling>
{
    private readonly HashSet<Type> _exceptionMapperTypes = [];

    internal IReadOnlyCollection<Type> ExceptionMapperTypes => _exceptionMapperTypes;

    /// <summary>
    /// Adds a response mapper that participates in exception-to-envelope conversion.
    /// </summary>
    public void AddExceptionMapper<TMapper>() where TMapper : class, IExceptionResponseMapper
    {
        _exceptionMapperTypes.Add(typeof(TMapper));
    }
}
