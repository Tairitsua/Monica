using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;
using Monica.Core.Modularity.Abstractions;
using Monica.Core.TypeDiscovery.Abstractions;
using Monica.Framework.Seeder.Abstractions;
using Monica.Modules;
using Monica.Testing.Hosting;
using Xunit;

namespace Test.Monica.Framework.Seeder;

public sealed class SeederActivationTests
{
    [Fact]
    public async Task Startup_ShouldRunOnlySelectedSeedersInDependencyOrder()
    {
        await using var application = await new ActivationFactory(includePrerequisite: true)
            .CreateAsync(cancellationToken: TestContext.Current.CancellationToken);
        var state = application.Services.GetRequiredService<ISeederState>();
        await WaitFor.UntilAsync(_ => Task.FromResult(state.GetSnapshot().IsCompleted),
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal([nameof(PrerequisiteSeeder), nameof(SelectedSeeder)],
            application.Services.GetRequiredService<Probe>().Calls.ToArray());
        Assert.Equal(2, state.GetSnapshot().Seeders.Length);
        Assert.False(application.Services.GetRequiredService<IServiceProviderIsService>()
            .IsService(typeof(InactiveSeeder)));
    }

    [Fact]
    public async Task Startup_WhenSelectedSeederRequiresInactivePrerequisite_ShouldRejectComposition()
    {
        var error = await Assert.ThrowsAnyAsync<Exception>(async () =>
        {
            await using var application = await new ActivationFactory(includePrerequisite: false)
                .CreateAsync(cancellationToken: TestContext.Current.CancellationToken);
        });

        Assert.Contains(nameof(PrerequisiteSeeder), error.ToString());
        Assert.Contains("undiscovered seeder", error.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    private sealed class ActivationFactory(bool includePrerequisite) : MonicaTestApplicationFactory<SeederActivationTests>
    {
        protected override void ConfigureMonica(IMonicaBuilder builder)
        {
            builder.AddModule<ActivationModule, ActivationOptions>(options => options.IncludePrerequisite = includePrerequisite);
            builder.AddSeeder();
        }

        protected override void ConfigureServices(IServiceCollection services) => services.AddSingleton<Probe>();
    }

    public sealed class ActivationModule : MonicaModule<ActivationOptions>
    {
        public override void ConfigureBuilder(ModuleBuilderContext<ActivationOptions> context) =>
            Application.SetTypeActivationPolicy(new Policy(Option.IncludePrerequisite));
    }

    public sealed class ActivationOptions : ModuleOptions<ActivationModule>
    {
        public bool IncludePrerequisite { get; set; }
    }

    private sealed class Policy(bool includePrerequisite) : ITypeActivationPolicy
    {
        public bool IsEnabled(Type type, TypeActivationKind kind) => kind != TypeActivationKind.HostedService ||
            !typeof(ISeeder).IsAssignableFrom(type) || type == typeof(SelectedSeeder) ||
            includePrerequisite && type == typeof(PrerequisiteSeeder);
        public void EnsureCanExecuteRequest(Type requestType) { }
        public string? GetWorkloadOwner(Type implementationType) => null;
    }

    public sealed class Probe
    {
        public ConcurrentQueue<string> Calls { get; } = new();
    }

    public sealed class PrerequisiteSeeder(Probe probe) : ISeeder
    {
        public Task SeedAsync(CancellationToken cancellationToken)
        {
            probe.Calls.Enqueue(nameof(PrerequisiteSeeder));
            return Task.CompletedTask;
        }
    }

    [global::Monica.Framework.Seeder.Annotations.SeederDependsOn<PrerequisiteSeeder>]
    public sealed class SelectedSeeder(Probe probe) : ISeeder
    {
        public Task SeedAsync(CancellationToken cancellationToken)
        {
            probe.Calls.Enqueue(nameof(SelectedSeeder));
            return Task.CompletedTask;
        }
    }

    public sealed class InactiveSeeder : ISeeder
    {
        public InactiveSeeder() => throw new InvalidOperationException("Inactive startup work must not be constructed.");
        public Task SeedAsync(CancellationToken cancellationToken) => throw new InvalidOperationException();
    }
}
