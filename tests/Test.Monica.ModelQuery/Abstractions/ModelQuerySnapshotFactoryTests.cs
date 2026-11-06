using AwesomeAssertions;
using Microsoft.Extensions.DependencyInjection;
using Monica.ModelQuery.Abstractions;
using Monica.Core.Modularity.Abstractions;
using Monica.Modules;
using Monica.Testing.Hosting;
using Xunit;

namespace Test.Monica.ModelQuery.Abstractions;

public sealed class ModelQueryTestApplicationFactory : MonicaTestApplicationFactory<ModuleModelQuery>
{
    protected override void ConfigureMonica(IMonicaBuilder builder)
    {
        builder.AddModelQuery();
    }
}

public sealed class ModelQuerySnapshotFactoryTests(ModelQueryTestApplicationFactory applicationFactory)
    : IClassFixture<ModelQueryTestApplicationFactory>
{
    [Fact]
    public async Task PreloadSnapshot_WhenModelHasNotBeenResolved_ShouldBuildAndPublishOneSnapshot()
    {
        await using var application = await applicationFactory.CreateAsync(
            cancellationToken: TestContext.Current.CancellationToken);
        var factory = application.Services.GetRequiredService<IModelQuerySnapshotFactory>();

        factory.GetSnapshots().Should().BeEmpty();

        var first = factory.PreloadSnapshot<PreloadedModel>();
        var second = factory.PreloadSnapshot<PreloadedModel>();

        second.Should().BeSameAs(first);
        first.Model.FullTypeName.Should().Be(typeof(PreloadedModel).FullName);
        first.Fields.Select(field => field.ReflectionName).Should().Contain([nameof(PreloadedModel.Name), nameof(PreloadedModel.Count)]);
        factory.GetSnapshots().Should().ContainSingle().Which.Should().BeSameAs(first);
    }

    private sealed class PreloadedModel
    {
        public string Name { get; init; } = string.Empty;

        public int Count { get; init; }
    }
}
