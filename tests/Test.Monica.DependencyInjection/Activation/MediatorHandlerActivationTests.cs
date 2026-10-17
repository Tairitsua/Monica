using AwesomeAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Monica.Core.Mediator;
using Monica.Core.Modularity.Extensions;
using Monica.Modules;
using Monica.Application.Abstractions;
using Monica.WebApi.Abstractions;
using Xunit;

namespace Test.Monica.DependencyInjection.Activation;

public sealed class MediatorHandlerActivationTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Send_WhenApplicationServiceUsesMapper_ShouldUseMonicaActivation(
        bool registerMediatorFirst)
    {
        var builder = Host.CreateApplicationBuilder();
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

            monica.AddObjectMapping();
        });
        using var host = builder.Build();
        using var scope = host.Services.CreateScope();

        var response = await scope.ServiceProvider
            .GetRequiredService<IMediator>()
            .Send(new MappingRequest("mapped"), TestContext.Current.CancellationToken);

        response.Should().Be("mapped");
    }

    private sealed record MappingRequest(string Value) : IRequest<string>;

    private sealed class MappingHandler : CustomApplicationService<MappingRequest, string>
    {
        public override Task<string> Handle(
            MappingRequest request,
            CancellationToken cancellationToken)
        {
            var destination = Mapper.Map<MappingDestination>(new MappingSource(request.Value));
            return Task.FromResult(destination.Value);
        }
    }

    private sealed record MappingSource(string Value);

    private sealed class MappingDestination
    {
        public string Value { get; init; } = string.Empty;
    }
}
