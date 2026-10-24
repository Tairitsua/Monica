using AwesomeAssertions;
using Monica.Configuration.Abstractions;
using Monica.Configuration.Exceptions;
using Monica.Configuration.Facades;
using Monica.Configuration.Models;
using Monica.Configuration.Services;
using Monica.Configuration.Services.Support;
using Monica.Core.Results;
using NSubstitute;
using Xunit;

namespace Test.Monica.Configuration.Services;

public sealed class ConfigurationUnifiedVersionServiceTests
{
    [Fact]
    public async Task DeleteVersionAsync_WhenManual_ShouldRejectBeforeMaintenanceWrite()
    {
        var store = Substitute.For<IConfigurationUnifiedVersionStore>();
        // This boundary must reject before touching version contents or rollback collaborators.
        var service = new ConfigurationUnifiedVersionService(store, null!, null!, new ConfigurationRuntimeSnapshotLock(),
            new ConfigurationRuntimeContext { AutonomousWork = false });

        var delete = () => service.DeleteVersionAsync(1, TestContext.Current.CancellationToken);

        await delete.Should().ThrowAsync<ConfigurationValidationFailedException>().WithMessage("*read-only in Manual mode*");
        store.ReceivedCalls().Should().BeEmpty();
    }

    private const long TARGET_VERSION = 7;

    [Fact]
    public async Task RollbackToVersionAsync_WhenPreviewFingerprintIsStale_ShouldThrowConcurrencyConflict()
    {
        var (service, applyService) = CreateService();

        var act = () => service.RollbackToVersionAsync(CreateStaleRequest(), CancellationToken.None);

        await act.Should()
            .ThrowAsync<ConfigurationConcurrencyConflictException>()
            .WithMessage("*preview is stale*");
        await applyService.DidNotReceive()
            .ApplyAsync(Arg.Any<ConfigurationMutationGroupApplyRequest>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RollbackUnifiedVersionAsync_WhenPreviewFingerprintIsStale_ShouldReturnConflict()
    {
        var (service, applyService) = CreateService();
        var facade = CreateFacade(service);

        var result = await facade.RollbackUnifiedVersionAsync(CreateStaleRequest());

        result.IsFailed(out var error, out _).Should().BeTrue();
        error!.Status.Should().Be(ResStatus.Conflict);
        error.Message.Should().Contain("reviewed configuration changed").And.Contain("new preview");
        await applyService.DidNotReceive()
            .ApplyAsync(Arg.Any<ConfigurationMutationGroupApplyRequest>(), Arg.Any<CancellationToken>());
    }

    private static (ConfigurationUnifiedVersionService Service, IConfigurationMutationGroupApplyService ApplyService)
        CreateService()
    {
        var snapshot = new ConfigurationUnifiedVersionSnapshot
        {
            Summary = new ConfigurationUnifiedVersionSummary { Version = TARGET_VERSION }
        };
        var versionStore = Substitute.For<IConfigurationUnifiedVersionStore>();
        versionStore
            .GetVersionAsync(TARGET_VERSION, Arg.Any<CancellationToken>())
            .Returns(snapshot);
        var applyService = Substitute.For<IConfigurationMutationGroupApplyService>();

        // An empty snapshot never resolves definition collaborators; it still exercises the production fingerprint
        // computation and stale-preview guard without requiring persistence or configuration-provider integration.
        var previewFactory = new ConfigurationUnifiedVersionRollbackPreviewFactory(null!, null!, null!, null!, applyService);
        var service = new ConfigurationUnifiedVersionService(
            versionStore,
            applyService,
            previewFactory,
            new ConfigurationRuntimeSnapshotLock());
        return (service, applyService);
    }

    private static ConfigurationUnifiedVersionRollbackRequest CreateStaleRequest()
    {
        return new ConfigurationUnifiedVersionRollbackRequest
        {
            Version = TARGET_VERSION,
            PreviewFingerprint = "sha256:stale"
        };
    }

    private static ConfigurationFacade CreateFacade(IConfigurationUnifiedVersionService unifiedVersionService)
    {
        var provider = Substitute.For<IServiceProvider>();
        provider.GetService(typeof(IConfigurationUnifiedVersionService)).Returns(unifiedVersionService);
        return new ConfigurationFacade(provider);
    }
}
