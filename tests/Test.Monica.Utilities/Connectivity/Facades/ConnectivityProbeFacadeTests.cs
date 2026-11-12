using AwesomeAssertions;
using Monica.Core.Results;
using Monica.Modules;
using Monica.Utilities.Connectivity.Models;
using Test.Monica.Utilities.Support;
using Xunit;

namespace Test.Monica.Utilities.Connectivity.Facades;

public sealed class ConnectivityProbeFacadeTests
{
    [Fact]
    public async Task ProbeAsync_WhenCallerCancelsAdmittedHttpRequest_ShouldReturnFailedEnvelopeAndDrainConnection()
    {
        await using var endpoint = await ControlledHttpEndpoint.StartAsync();
        var facade = UtilitiesTestServices.CreateConnectivityFacade(new ModuleUtilitiesOption
        {
            DefaultProbeTimeoutMilliseconds = 15000,
            MaximumProbeTimeoutMilliseconds = 20000
        });
        using var cancellation = new CancellationTokenSource();
        var pending = facade.ProbeAsync(new ConnectivityProbeRequest
        {
            Host = endpoint.Address.Host,
            Port = endpoint.Address.Port,
            ProbeKind = ConnectivityProbeKind.Http,
            Path = "/held",
            TimeoutMilliseconds = 15000
        }, cancellation.Token);
        try
        {
            await ControlledHttpEndpoint.ObserveAsync(endpoint.Admitted.Task);
        }
        finally
        {
            cancellation.Cancel();
        }

        var result = await pending.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        result.Status.Should().Be(ResultStatus.BadRequest);
        result.Message.Should().NotBeNullOrWhiteSpace();
        result.Data.Should().BeNull();
        await endpoint.ObserveCanceledConnectionDrainAsync();
    }
}
