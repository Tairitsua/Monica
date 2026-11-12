using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AwesomeAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Localization;
using Monica.Core.JsonSerialization.Abstractions;
using Monica.Core.Modularity.Abstractions;
using Monica.Core.Results;
using Monica.EventBus.Abstractions;
using Monica.EventBus.Models;
using Monica.Framework.UI.Localization;
using Monica.Framework.UI.UIEventBus.Models;
using Monica.Modules;
using Monica.Testing.Hosting;
using Xunit;

namespace Test.Monica.EventBus.Modules;

/// <summary>
/// Verifies EventBus UI HTTP projection through the production module graph without explicit result-envelope registration.
/// </summary>
public sealed class ModuleEventBusUITests
{
    private const string TOPIC_NAME = "eventbus-ui.http-contract";

    [Fact]
    public async Task AddEventBusUI_WhenMinimalApisAreEnabled_ShouldProjectReadsAndExactLifecycleWithoutExplicitResultEnvelope()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var application = await new EventBusUITestApplicationFactory().CreateAsync(
            cancellationToken: cancellationToken);
        await using var scope = application.CreateScope(cancellationToken);
        var bus = scope.Resolve<ILocalEventBus>();
        await using var subscription = await bus.SubscribeAsync<HttpContractEvent>(
            static (_, _) => Task.CompletedTask, TOPIC_NAME);
        using var client = CreateClient(application);
        var serializerOptions = application.Services.GetRequiredService<IJsonSerializerOptionsProvider>().SerializerOptions;

        var list = await GetResultAsync<List<SubscriptionViewModel>>(
            client, "/eventbus-ui/subscriptions", serializerOptions, cancellationToken);
        var row = list.Data.Should().ContainSingle().Which;
        row.SubscriptionId.Should().Be(subscription.Id.ToString());
        row.EventType.Should().Be($"{typeof(ModuleEventBusUITests).FullName}.{nameof(HttpContractEvent)}");
        row.TopicName.Should().Be(TOPIC_NAME);
        row.ServiceKey.Should().BeNull();
        row.Scope.Should().Be(EventSubscriptionScope.Local);
        row.State.Should().Be(EventSubscriptionState.Active);
        row.IsActionHandler.Should().BeTrue();
        row.TopicStatus.Should().BeNull();

        var detail = await GetResultAsync<SubscriptionViewModel>(
            client, $"/eventbus-ui/subscriptions/{subscription.Id}", serializerOptions, cancellationToken);
        detail.Data.Should().NotBeNull();
        detail.Data!.SubscriptionId.Should().Be(row.SubscriptionId);
        detail.Data.TopicName.Should().Be(TOPIC_NAME);

        var statistics = await GetResultAsync<SubscriptionStatistics>(
            client, "/eventbus-ui/statistics", serializerOptions, cancellationToken);
        statistics.Data.Should().NotBeNull();
        statistics.Data!.TotalSubscriptions.Should().Be(1);
        statistics.Data.ActiveSubscriptions.Should().Be(1);
        statistics.Data.LocalSubscriptions.Should().Be(1);
        statistics.Data.DistributedSubscriptions.Should().Be(0);
        statistics.Data.ActionHandlerCount.Should().Be(1);

        var topicStatuses = await GetResultAsync<List<TopicSubscriptionStatus>>(
            client, "/eventbus-ui/topic-status", serializerOptions, cancellationToken);
        topicStatuses.Data.Should().BeEmpty();

        using (var response = await client.PostAsync(
                   $"/eventbus-ui/subscriptions/{subscription.Id}/deactivate", null, cancellationToken))
        {
            await AssertSuccessfulCommandAsync(response, serializerOptions, cancellationToken);
        }
        bus.Subscriptions.GetById(subscription.Id)!.State.Should().Be(EventSubscriptionState.Inactive);

        using (var response = await client.PostAsync(
                   $"/eventbus-ui/subscriptions/{subscription.Id}/activate", null, cancellationToken))
        {
            await AssertSuccessfulCommandAsync(response, serializerOptions, cancellationToken);
        }
        bus.Subscriptions.GetById(subscription.Id)!.State.Should().Be(EventSubscriptionState.Active);

        using (var response = await client.DeleteAsync(
                   $"/eventbus-ui/subscriptions/{subscription.Id}", cancellationToken))
        {
            await AssertSuccessfulCommandAsync(response, serializerOptions, cancellationToken);
        }
        bus.Subscriptions.GetById(subscription.Id).Should().BeNull();
        var remaining = await GetResultAsync<List<SubscriptionViewModel>>(
            client, "/eventbus-ui/subscriptions", serializerOptions, cancellationToken);
        remaining.Data.Should().BeEmpty();
    }

    [Fact]
    public async Task AddEventBusUI_WhenLifecycleIdDoesNotExist_ShouldProjectBadRequestEnvelopesWithoutChangingRegistry()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var application = await new EventBusUITestApplicationFactory().CreateAsync(
            cancellationToken: cancellationToken);
        using var client = CreateClient(application);
        var serializerOptions = application.Services.GetRequiredService<IJsonSerializerOptionsProvider>().SerializerOptions;
        var expectedMessage = GetEnglishMissingSubscriptionMessage(application);
        const string MISSING_ID = "6e924a0c315f4af4b3de8ef365179603";

        foreach (var (method, suffix) in new[]
                 {
                     (HttpMethod.Post, "/activate"),
                     (HttpMethod.Post, "/deactivate"),
                     (HttpMethod.Delete, string.Empty)
                 })
        {
            using var request = new HttpRequestMessage(method, $"/eventbus-ui/subscriptions/{MISSING_ID}{suffix}");
            using var response = await client.SendAsync(request, cancellationToken);
            response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
            response.Content.Headers.ContentType?.MediaType.Should().Be("application/json");
            var result = await response.Content.ReadFromJsonAsync<Result>(serializerOptions, cancellationToken);
            result.Should().NotBeNull();
            result!.Status.Should().Be(ResultStatus.BadRequest);
            result.Message.Should().Be(expectedMessage);
            result.TryGetError(serializerOptions, out var error).Should().BeTrue();
            error!.Code.Should().Be(ResultErrorCodes.OperationFailed);
            error.TraceId.Should().NotBeNullOrWhiteSpace();
        }

        var list = await GetResultAsync<List<SubscriptionViewModel>>(
            client, "/eventbus-ui/subscriptions", serializerOptions, cancellationToken);
        list.Data.Should().BeEmpty();
    }

    private static HttpClient CreateClient(MonicaTestApplication application) =>
        ((TestServer)application.Services.GetRequiredService<IServer>()).CreateClient();

    private static async Task<Result<T>> GetResultAsync<T>(
        HttpClient client, string path, JsonSerializerOptions serializerOptions, CancellationToken cancellationToken)
    {
        using var response = await client.GetAsync(path, cancellationToken);
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Content.Headers.ContentType?.MediaType.Should().Be("application/json");
        var result = await response.Content.ReadFromJsonAsync<Result<T>>(serializerOptions, cancellationToken);
        result.Should().NotBeNull();
        result!.Status.Should().Be(ResultStatus.Ok);
        result.TryGetError(serializerOptions, out _).Should().BeFalse();
        return result;
    }

    private static async Task AssertSuccessfulCommandAsync(
        HttpResponseMessage response, JsonSerializerOptions serializerOptions, CancellationToken cancellationToken)
    {
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Content.Headers.ContentType?.MediaType.Should().Be("application/json");
        var result = await response.Content.ReadFromJsonAsync<Result>(serializerOptions, cancellationToken);
        result.Should().NotBeNull();
        result!.Status.Should().Be(ResultStatus.Ok);
        result.TryGetError(serializerOptions, out _).Should().BeFalse();
    }

    private static string GetEnglishMissingSubscriptionMessage(MonicaTestApplication application)
    {
        var previousCulture = CultureInfo.CurrentUICulture;
        try
        {
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("en-US");
            return application.Services.GetRequiredService<IStringLocalizer<EventBusResource>>()
                ["Services:Common:SubscriptionNotFound"].Value;
        }
        finally
        {
            CultureInfo.CurrentUICulture = previousCulture;
        }
    }

    private sealed class HttpContractEvent;

    private sealed class EventBusUITestApplicationFactory : MonicaTestApplicationFactory<ModuleEventBusUI>
    {
        protected override void ConfigureHost(WebApplicationBuilder builder) =>
            builder.Environment.ApplicationName = typeof(ModuleEventBusUI).Assembly.GetName().Name!;

        protected override void ConfigureMonica(IMonicaBuilder builder)
        {
            builder.ConfigureModuleSystem(options =>
            {
                options.AutoAddMonicaHttpListener = false;
                options.EnableMinimalApiByDefault = false;
            });
            builder.AddLocalization(options => options.DefaultCulture = "en-US");
            builder.AddEventBus(options => options.DisableAutoDiscovery = true).UseNoOpDistributedEventBus();
            builder.AddEventBusUI(options => options.EnableMinimalApi = true);
        }
    }
}
