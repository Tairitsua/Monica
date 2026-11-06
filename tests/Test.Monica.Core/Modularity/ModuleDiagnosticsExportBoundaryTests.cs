using System.Text.Json;
using AwesomeAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Monica.Core;
using Monica.Core.Modularity.Abstractions;
using Monica.Core.Modularity.Diagnostics.Facades;
using Monica.Core.Modularity.Extensions;
using Monica.Core.Modularity.Models;
using Monica.Core.Results;
using Monica.Modules;
using Xunit;

namespace Test.Monica.Core.Modularity;

public sealed class ModuleDiagnosticsExportBoundaryTests
{
    private const string RAW_FAILURE_SENTINEL = "raw-export-failure-sentinel";
    private const string RAW_PATH_SENTINEL = @"D:\private\module-diagnostics-secret.txt";

    [Fact]
    public void CreateExport_WhenLiveDiagnosticsContainFailureAndAssemblyPath_ShouldOmitRawDetails()
    {
        var builder = Host.CreateApplicationBuilder();
        builder.AddMonica(monica =>
        {
            monica.ConfigureTypeDiscovery(options => options
                .ExcludeDefault()
                .Add(typeof(ModuleDiagnosticsExportBoundaryTests).Assembly));
            monica.AddModuleSystem();
            monica.AddModule<ExportFailureProbeModule, ExportFailureProbeOption>(options =>
            {
                options.FailureMessage = $"{RAW_FAILURE_SENTINEL}: {RAW_PATH_SENTINEL}";
            });
        });

        using var host = builder.Build();
        var application = host.Services.GetRequiredService<MonicaApplication>();
        application.Modules.DrainStartupWork();
        var rawFailure = application.Profiling.GetCompositionPerformance().StartupWorkItems
            .Single(work => work.Name == ExportFailureProbeModule.WORK_NAME)
            .ErrorMessage;
        var facade = host.Services.GetRequiredService<ModuleDiagnosticsFacade>();
        var inventory = facade.GetAssemblyInventory();
        var export = facade.CreateExport();

        rawFailure.Should().Contain(RAW_FAILURE_SENTINEL).And.Contain(RAW_PATH_SENTINEL);
        inventory.Status.Should().Be(ResultStatus.Ok);
        var assemblyPath = inventory.Data!.Assemblies
            .Single(assembly => assembly.Name == typeof(ModuleDiagnosticsExportBoundaryTests).Assembly.GetName().Name)
            .Location;
        assemblyPath.Should().NotBeNullOrWhiteSpace();
        export.Status.Should().Be(ResultStatus.Ok);
        export.Data!.Outcome.Should().Be(
            global::Monica.Core.Modularity.Diagnostics.Models.ModuleCompositionOutcome.Failed);

        var json = JsonSerializer.Serialize(export.Data);
        json.Should().NotContain(RAW_FAILURE_SENTINEL);
        json.Should().NotContain(JsonValue(RAW_PATH_SENTINEL));
        json.Should().NotContain(JsonValue(assemblyPath!));
        json.Should().NotContain("StackTrace");
        json.Should().NotContain("OptionTypeName");
    }

    private static string JsonValue(string value)
    {
        var serialized = JsonSerializer.Serialize(value);
        return serialized[1..^1];
    }
}

internal sealed class ExportFailureProbeModule : MonicaModule<ExportFailureProbeOption>
{
    internal const string WORK_NAME = "sanitized-export-failure";

    public override void ValidateOptions(ExportFailureProbeOption options, string? profileName)
    {
        UseCompositionLoggerFactory(NullLoggerFactory.Instance);
    }

    public override void ConfigureServices(ModuleContext<ExportFailureProbeOption> context)
    {
        ScheduleStartupWork(
            WORK_NAME,
            () => throw new InvalidOperationException(Option.FailureMessage),
            ModuleStartupWorkBarrier.NoBarrier);
    }
}

internal sealed class ExportFailureProbeOption : ModuleOptions<ExportFailureProbeModule>
{
    internal string FailureMessage { get; set; } = string.Empty;
}
