using System.Collections.Concurrent;
using System.Reflection;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Monica.ApplicationModel.Abstractions;
using Monica.ApplicationModel.Annotations;
using Monica.Configuration.Bootstrap;
using Monica.Core.Modularity.Abstractions;
using Monica.Modules;
using Monica.Reference.Api.Composition;
using Monica.Reference.Api.Configurations;
using Monica.Testing.Hosting;
using Platform.Protocol.Application;

namespace Test.Monica.Reference.Api.Composition;

internal sealed class ReferenceApiTestApplicationFactory : MonicaTestApplicationFactory<OptionsReferenceUI>, IDisposable
{
    internal string StoreDirectory { get; } = Path.Combine(Path.GetTempPath(), "Monica.Reference.Tests", Guid.NewGuid().ToString("N"));
    internal RecordingLogs Logs { get; } = new();

    protected override IEnumerable<Assembly> TypeDiscoveryAssemblies
    {
        get
        {
            var host = typeof(ReferenceApplicationComposition).Assembly;
            var provider = host.GetCustomAttribute<GeneratedApplicationManifestAttribute>()!.ProviderType;
            var manifest = (IApplicationManifest)Activator.CreateInstance(provider)!;
            return manifest.LocalAssemblies.Select(name => Assembly.Load(new AssemblyName(name)))
                .Append(host).Append(typeof(ReferenceRoles).Assembly).Distinct();
        }
    }

    protected override void ConfigureHost(WebApplicationBuilder builder)
    {
        // The real web executable owns the asset manifest copied through its project reference.
        builder.Environment.ApplicationName = typeof(ReferenceApplicationComposition).Assembly.GetName().Name!;
        builder.Configuration.AddJsonFile(Path.Combine(AppContext.BaseDirectory, "reference-appsettings.json"), optional: false);
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Application:AppId"] = "monica-ordering-reference",
            ["Application:AutonomousWork"] = "false",
            ["ReferenceUI:EnableDiagnostics"] = "true"
        });
        builder.Logging.AddProvider(Logs);
    }

    protected override void ConfigureMonica(IMonicaBuilder builder)
    {
        builder.ConfigureReferenceApplication(MonicaConfigurationInputPlan.Create(inputs =>
            inputs.UseFileConfigurationStore(options => options.RootDirectory = StoreDirectory)));
        builder.ConfigureModuleSystem(options => options.AutoAddMonicaHttpListener = false);
        builder.AddJobScheduler(options => options.WorkerPollInterval = TimeSpan.FromMilliseconds(10));
    }

    public void Dispose()
    {
        if (Directory.Exists(StoreDirectory)) Directory.Delete(StoreDirectory, recursive: true);
    }

    internal sealed class RecordingLogs : ILoggerProvider
    {
        internal ConcurrentQueue<(string Category, string Message)> Entries { get; } = new();
        public ILogger CreateLogger(string categoryName) => new Recorder(categoryName, Entries);
        public void Dispose() { }

        private sealed class Recorder(string category, ConcurrentQueue<(string Category, string Message)> entries) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel logLevel) => true;
            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
                Func<TState, Exception?, string> formatter) => entries.Enqueue((category, formatter(state, exception)));
        }
    }
}
