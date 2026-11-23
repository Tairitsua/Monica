using AwesomeAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Monica.Application.Abstractions;
using Monica.Core.Mediator;
using Monica.Core.Modularity.Extensions;
using Monica.Modules;
using Xunit;

namespace Test.Monica.DependencyInjection.Activation;

public sealed class MediatorHandlerActivationTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Send_WhenApplicationRequestHandlerUsesCachedServices_ShouldResolveDependencyFromCurrentScope(
        bool registerMediatorFirst)
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Services.AddScoped(_ => new ScopedDependency());
        builder.AddMonica(monica =>
        {
            monica.ConfigureTypeDiscovery(options => options
                .ExcludeDefault()
                .Add(typeof(MediatorHandlerActivationTests).Assembly));

            if (registerMediatorFirst)
            {
                monica.AddMediator();
                monica.AddDependencyInjection();
            }
            else
            {
                monica.AddDependencyInjection();
                monica.AddMediator();
            }
        });
        using var host = builder.Build();
        using var scope = host.Services.CreateScope();

        var response = await scope.ServiceProvider
            .GetRequiredService<IMediator>()
            .Send(new ScopedDependencyRequest(), TestContext.Current.CancellationToken);

        response.Should().BeSameAs(scope.ServiceProvider.GetRequiredService<ScopedDependency>());
    }

    private sealed record ScopedDependencyRequest : IRequest<ScopedDependency>;

    private sealed class ScopedDependencyHandler : ApplicationRequestHandler<ScopedDependencyRequest, ScopedDependency>
    {
        public override Task<ScopedDependency> Handle(
            ScopedDependencyRequest request,
            CancellationToken cancellationToken)
        {
            return Task.FromResult(CachedServiceProvider.GetRequiredService<ScopedDependency>());
        }
    }

    private sealed class ScopedDependency;
}
