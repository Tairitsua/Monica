using System.Collections;
using AwesomeAssertions;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Monica.Core;
using Monica.Core.Modularity.Extensions;
using Monica.Core.Results;
using Monica.Modules;
using Monica.Testing.Localization;
using Monica.UI.Localization;
using Monica.UI.UISystemInfo.Facades;
using Monica.UI.UISystemInfo.Models;
using Xunit;

namespace Test.Monica.UI.UISystemInfo.Facades;

public sealed class SystemInfoFacadeTests
{
    [Fact]
    public async Task GetSnapshot_WhenStartupTrackingCompletes_ShouldReturnExactOptInTiming()
    {
        var startup = MonicaStartup.Start();
        using var host = await StartHostAsync(startup);

        try
        {
            var application = host.Services.GetRequiredService<MonicaApplication>();
            var result = CreateFacade(host).GetSnapshot();

            result.Status.Should().Be(ResStatus.Ok);
            result.Message.Should().BeNull();
            var snapshot = result.Data;
            snapshot.Should().NotBeNull();
            var capturedStartup = snapshot!.Startup;
            capturedStartup.Should().NotBeNull();
            var applicationStartup = application.StartupTiming;
            applicationStartup.Should().NotBeNull();
            capturedStartup!.StartedAtUtc.Should().Be(applicationStartup!.StartedAtUtc);
            capturedStartup.ReadyAtUtc.Should().Be(applicationStartup.ReadyAtUtc!.Value);
            capturedStartup.DurationMs.Should().Be(applicationStartup.DurationMs!.Value);
        }
        finally
        {
            await host.StopAsync(TestContext.Current.CancellationToken);
        }
    }

    [Fact]
    public async Task GetSnapshot_WhenHostDidNotOptIn_ShouldOmitStartupTiming()
    {
        using var host = await StartHostAsync();

        try
        {
            var result = CreateFacade(host).GetSnapshot();

            result.Status.Should().Be(ResStatus.Ok);
            result.Message.Should().BeNull();
            var snapshot = result.Data;
            snapshot.Should().NotBeNull();
            snapshot!.Startup.Should().BeNull();
        }
        finally
        {
            await host.StopAsync(TestContext.Current.CancellationToken);
        }
    }

    [Fact]
    public async Task GetSnapshot_ShouldNormalizePublishedEndpointsAndExcludeEnvironmentVariableCollections()
    {
        using var host = await StartHostAsync();
        var addresses = new ServerAddressesFeature();
        addresses.Addresses.Add("  https://localhost:7093  ");
        addresses.Addresses.Add("HTTPS://LOCALHOST:7093");
        addresses.Addresses.Add("  named-pipe  ");
        addresses.Addresses.Add("   ");
        var server = new TestServer();
        var facade = CreateFacade(host, server);
        server.Features.Set<IServerAddressesFeature>(addresses);

        try
        {
            var result = facade.GetSnapshot();

            result.Status.Should().Be(ResStatus.Ok);
            result.Message.Should().BeNull();
            var snapshot = result.Data;
            snapshot.Should().NotBeNull();
            snapshot!.Endpoints.Should().HaveCount(2);
            snapshot.Endpoints[0].Should().Be(new SystemInfoEndpoint
            {
                Address = "https://localhost:7093",
                Scheme = "https"
            });
            snapshot.Endpoints[1].Should().Be(new SystemInfoEndpoint
            {
                Address = "named-pipe",
                Scheme = string.Empty
            });

            var approvedContract = new Dictionary<Type, string[]>
            {
                [typeof(SystemInfoSnapshot)] =
                [
                    nameof(SystemInfoSnapshot.CapturedAtUtc),
                    nameof(SystemInfoSnapshot.Application),
                    nameof(SystemInfoSnapshot.Process),
                    nameof(SystemInfoSnapshot.Host),
                    nameof(SystemInfoSnapshot.Artifact),
                    nameof(SystemInfoSnapshot.Startup),
                    nameof(SystemInfoSnapshot.Endpoints)
                ],
                [typeof(SystemInfoApplication)] =
                [
                    nameof(SystemInfoApplication.Name),
                    nameof(SystemInfoApplication.Id),
                    nameof(SystemInfoApplication.Version),
                    nameof(SystemInfoApplication.EnvironmentName)
                ],
                [typeof(SystemInfoProcess)] =
                [
                    nameof(SystemInfoProcess.StartedAtUtc),
                    nameof(SystemInfoProcess.RuntimeVersion),
                    nameof(SystemInfoProcess.Architecture),
                    nameof(SystemInfoProcess.ProcessId),
                    nameof(SystemInfoProcess.WorkingSetBytes),
                    nameof(SystemInfoProcess.Is64BitOperatingSystem),
                    nameof(SystemInfoProcess.Is64BitProcess),
                    nameof(SystemInfoProcess.IsPrivileged),
                    nameof(SystemInfoProcess.PageSizeBytes),
                    nameof(SystemInfoProcess.IsInteractive),
                    nameof(SystemInfoProcess.ProcessPath)
                ],
                [typeof(SystemInfoHost)] =
                [
                    nameof(SystemInfoHost.MachineName),
                    nameof(SystemInfoHost.UserName),
                    nameof(SystemInfoHost.UserDomainName),
                    nameof(SystemInfoHost.OperatingSystem),
                    nameof(SystemInfoHost.CurrentDirectory),
                    nameof(SystemInfoHost.TimeZone)
                ],
                [typeof(SystemInfoArtifact)] =
                [
                    nameof(SystemInfoArtifact.ProductName),
                    nameof(SystemInfoArtifact.ProductVersion),
                    nameof(SystemInfoArtifact.FileVersion),
                    nameof(SystemInfoArtifact.CompanyName),
                    nameof(SystemInfoArtifact.LegalCopyright),
                    nameof(SystemInfoArtifact.ArtifactTimestampUtc)
                ],
                [typeof(SystemInfoApplicationStartup)] =
                [
                    nameof(SystemInfoApplicationStartup.StartedAtUtc),
                    nameof(SystemInfoApplicationStartup.ReadyAtUtc),
                    nameof(SystemInfoApplicationStartup.DurationMs)
                ],
                [typeof(SystemInfoEndpoint)] =
                [
                    nameof(SystemInfoEndpoint.Address),
                    nameof(SystemInfoEndpoint.Scheme)
                ],
                [typeof(SystemTimeZone)] =
                [
                    nameof(SystemTimeZone.Id),
                    nameof(SystemTimeZone.UtcOffset)
                ]
            };

            foreach (var (contractType, approvedProperties) in approvedContract)
            {
                var properties = contractType.GetProperties();
                properties.Select(static property => property.Name)
                    .Should().BeEquivalentTo(approvedProperties);
                properties.Should().NotContain(property => IsDictionary(property.PropertyType));
            }
        }
        finally
        {
            await host.StopAsync(TestContext.Current.CancellationToken);
        }
    }

    private static async Task<IHost> StartHostAsync(MonicaStartup? startup = null)
    {
        var builder = Host.CreateApplicationBuilder();

        if (startup is null)
        {
            builder.AddMonica(static monica =>
                monica.ConfigureTypeDiscovery(static options => options.ExcludeDefault()));
        }
        else
        {
            builder.AddMonica(startup, static monica =>
                monica.ConfigureTypeDiscovery(static options => options.ExcludeDefault()));
        }

        var host = builder.Build();
        await host.StartAsync(TestContext.Current.CancellationToken);
        return host;
    }

    private static SystemInfoFacade CreateFacade(IHost host, IServer? server = null) => new(
        server ?? new TestServer(),
        host.Services.GetRequiredService<MonicaApplication>(),
        Options.Create(new ModuleShellUIOption
        {
            AppName = "Flight Service",
            AppId = "DEV-SERVICE-FLIGHT-API",
            AppVersion = "v1.0.3"
        }),
        new global::Monica.UI.Shared.Services.MoTimeFormatter(
            Options.Create(new ModuleShellUIOption()), Options.Create(new ModuleClockOption())),
        host.Services.GetRequiredService<IHostEnvironment>(),
        host.Services.GetRequiredService<IHostApplicationLifetime>(),
        Options.Create(new ModuleSystemInfoUIOption()),
        new EchoStringLocalizer<SystemInfoResource>(),
        NullLoggerFactory.Instance);

    private static bool IsDictionary(Type type) =>
        typeof(IDictionary).IsAssignableFrom(type)
        || type.GetInterfaces()
            .Prepend(type)
            .Any(static candidate => candidate.IsGenericType
                                     && IsDictionaryDefinition(candidate.GetGenericTypeDefinition()));

    private static bool IsDictionaryDefinition(Type definition) =>
        definition == typeof(IDictionary<,>)
        || definition == typeof(IReadOnlyDictionary<,>);

    private sealed class TestServer : IServer
    {
        public IFeatureCollection Features { get; } = new FeatureCollection();

        public Task StartAsync<TContext>(
            IHttpApplication<TContext> application,
            CancellationToken cancellationToken) where TContext : notnull => Task.CompletedTask;

        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public void Dispose()
        {
        }
    }
}
