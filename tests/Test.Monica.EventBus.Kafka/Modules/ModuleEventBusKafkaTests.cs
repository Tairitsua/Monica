using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AwesomeAssertions;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Monica.Core.JsonSerialization.Abstractions;
using Monica.Core.Modularity.Abstractions;
using Monica.Core.Results;
using Monica.EventBus.Kafka.Models;
using Monica.Modules;
using Monica.Testing.Hosting;
using Xunit;

namespace Test.Monica.EventBus.Kafka.Modules;

/// <summary>
/// Verifies Kafka HTTP envelopes through host-owned composition without explicit result-envelope registration.
/// </summary>
public sealed class ModuleEventBusKafkaTests
{
    private const string EXTERNAL_CLUSTER_ID = "externally-managed/上海?revision=2026#inspection%25";

    [Fact]
    public async Task AddEventBusKafka_WhenHostsHaveDifferentVisibleClusters_ShouldProjectIsolatedIntegrationEnvelopes()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var emptyApplication = await new KafkaApiTestApplicationFactory().CreateAsync(
            cancellationToken: cancellationToken);
        await using var externalApplication = await new KafkaApiTestApplicationFactory(includeExternalCluster: true)
            .CreateAsync(cancellationToken: cancellationToken);
        using var emptyClient = CreateTestServerClient(emptyApplication);
        using var externalClient = CreateTestServerClient(externalApplication);

        using var emptyResponse = await emptyClient.GetAsync("/eventbus-kafka/integration", cancellationToken);
        using var externalResponse = await externalClient.GetAsync("/eventbus-kafka/integration", cancellationToken);

        emptyResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        var emptyEnvelope = await ReadEnvelopeAsync<KafkaIntegrationSnapshot>(
            emptyApplication, emptyResponse, cancellationToken);
        emptyEnvelope.Status.Should().Be(ResultStatus.Ok);
        emptyEnvelope.Message.Should().BeNullOrEmpty();
        emptyEnvelope.Data.Should().NotBeNull();
        var emptySnapshot = emptyEnvelope.Data!;
        emptySnapshot.Mode.Should().Be(KafkaIntegrationMode.NotConfigured);
        emptySnapshot.PrimaryClusterId.Should().BeNull();
        emptySnapshot.Capabilities.Should().Be(KafkaConsoleCapabilities.None);

        externalResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        var externalEnvelope = await ReadEnvelopeAsync<KafkaIntegrationSnapshot>(
            externalApplication, externalResponse, cancellationToken);
        externalEnvelope.Status.Should().Be(ResultStatus.Ok);
        externalEnvelope.Message.Should().BeNullOrEmpty();
        externalEnvelope.Data.Should().NotBeNull();
        var externalSnapshot = externalEnvelope.Data!;
        externalSnapshot.Mode.Should().Be(KafkaIntegrationMode.Unknown);
        externalSnapshot.PrimaryClusterId.Should().Be(EXTERNAL_CLUSTER_ID);
        externalSnapshot.Capabilities.Should().Be(KafkaConsoleCapabilities.None);
    }

    [Fact]
    public async Task AddEventBusKafka_WhenTopicCredentialsAreExternal_ShouldProjectFailureEnvelopeBeforeBrokerAccess()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var application = await new KafkaApiTestApplicationFactory(includeExternalCluster: true)
            .CreateAsync(cancellationToken: cancellationToken);
        using var client = CreateTestServerClient(application);

        using var response = await client.PostAsJsonAsync("/eventbus-kafka/topics/messages",
            new KafkaTopicMessagesRequest
            {
                ClusterId = EXTERNAL_CLUSTER_ID,
                TopicName = "module-test.preview",
                MaxMessages = 1
            }, cancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var envelope = await ReadEnvelopeAsync<KafkaTopicMessageBatch>(application, response, cancellationToken);
        envelope.Status.Should().Be(ResultStatus.BadRequest);
        envelope.Message.Should().NotBeNullOrWhiteSpace();
        envelope.Message.Should().Contain(EXTERNAL_CLUSTER_ID)
            .And.Contain("direct admin access is not configured");
        envelope.Data.Should().BeNull();
        var serializerOptions = application.Services.GetRequiredService<IJsonSerializerOptionsProvider>().SerializerOptions;
        envelope.TryGetError(serializerOptions, out var error).Should().BeTrue();
        error.Should().NotBeNull();
        var declaredError = error!;
        declaredError.Code.Should().Be(ResultErrorCodes.OperationFailed);
        declaredError.TraceId.Should().NotBeNullOrWhiteSpace();
    }

    private static HttpClient CreateTestServerClient(MonicaTestApplication application) =>
        ((TestServer)application.Services.GetRequiredService<IServer>()).CreateClient();

    private static async Task<Result<T>> ReadEnvelopeAsync<T>(
        MonicaTestApplication application,
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        var serializerOptions = application.Services.GetRequiredService<IJsonSerializerOptionsProvider>().SerializerOptions;
        var envelope = JsonSerializer.Deserialize<Result<T>>(body, serializerOptions);
        envelope.Should().NotBeNull();
        return envelope!;
    }

    private sealed class KafkaApiTestApplicationFactory(bool includeExternalCluster = false)
        : MonicaTestApplicationFactory<ModuleEventBusKafka>
    {
        protected override void ConfigureMonica(IMonicaBuilder builder)
        {
            builder.ConfigureModuleSystem(options =>
            {
                options.AutoAddMonicaHttpListener = false;
                options.EnableMinimalApiByDefault = false;
            });
            var kafka = builder.AddEventBusKafka(options => options.EnableMinimalApi = true);
            if (includeExternalCluster)
            {
                kafka.AddConfiguredCluster(new KafkaClusterConfig
                {
                    ClusterId = EXTERNAL_CLUSTER_ID,
                    DisplayName = "External credentials",
                    CredentialsManagedExternally = true
                });
            }
        }
    }
}
