using System.Security.Claims;
using AwesomeAssertions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Monica.Modules;
using Monica.UI.Shell.Support;
using Xunit;

namespace Test.Monica.UI.Shell.Support;

public sealed class OperationalPageAccessEvaluatorTests
{
    [Fact]
    public async Task IsAuthorizedAsync_WhenEnvironmentIsDevelopment_ShouldAllowWithoutAuthorizationServices()
    {
        using var services = new ServiceCollection().BuildServiceProvider();
        var evaluator = CreateEvaluator(Environments.Development, new ModuleShellUIOption(), services);

        var isAuthorized = await evaluator.IsAuthorizedAsync(
            authorizationPolicyOverride: "missing-policy",
            TestContext.Current.CancellationToken);

        isAuthorized.Should().BeTrue();
    }

    [Theory]
    [InlineData("Staging")]
    [InlineData("Production")]
    public async Task IsAuthorizedAsync_WhenAuthorizationBypassIsEnabled_ShouldBypassEveryPolicy(
        string environmentName)
    {
        using var services = new ServiceCollection().BuildServiceProvider();
        var shellOptions = new ModuleShellUIOption();
        shellOptions.OperationalPageAccess.AuthorizationPolicy = "missing-global-policy";
        shellOptions.OperationalPageAccess.BypassAuthorization = true;
        var evaluator = CreateEvaluator(environmentName, shellOptions, services);

        var isAuthorized = await evaluator.IsAuthorizedAsync(
            authorizationPolicyOverride: "missing-page-policy",
            TestContext.Current.CancellationToken);

        isAuthorized.Should().BeTrue();
    }

    [Fact]
    public async Task IsAuthorizedAsync_WhenProductionHasNoEffectivePolicy_ShouldDeny()
    {
        using var services = new ServiceCollection().BuildServiceProvider();
        var evaluator = CreateEvaluator(Environments.Production, new ModuleShellUIOption(), services);

        var isAuthorized = await evaluator.IsAuthorizedAsync(
            cancellationToken: TestContext.Current.CancellationToken);

        isAuthorized.Should().BeFalse();
    }

    [Fact]
    public async Task IsAuthorizedAsync_WhenGlobalPolicyIsSatisfied_ShouldAllowCurrentCircuit()
    {
        var services = CreateAuthorizationServices(
            authenticated: true,
            options => options.AddPolicy("operations", policy => policy.RequireAuthenticatedUser()));
        await using var provider = services.BuildServiceProvider();
        var shellOptions = new ModuleShellUIOption();
        shellOptions.OperationalPageAccess.AuthorizationPolicy = "operations";
        var evaluator = CreateEvaluator(Environments.Production, shellOptions, provider);

        var isAuthorized = await evaluator.IsAuthorizedAsync(
            cancellationToken: TestContext.Current.CancellationToken);

        isAuthorized.Should().BeTrue();
    }

    [Fact]
    public async Task IsAuthorizedAsync_WhenGlobalPolicyIsNotSatisfied_ShouldDenyCurrentCircuit()
    {
        var services = CreateAuthorizationServices(
            authenticated: false,
            options => options.AddPolicy("operations", policy => policy.RequireAuthenticatedUser()));
        await using var provider = services.BuildServiceProvider();
        var shellOptions = new ModuleShellUIOption();
        shellOptions.OperationalPageAccess.AuthorizationPolicy = "operations";
        var evaluator = CreateEvaluator(Environments.Production, shellOptions, provider);

        var isAuthorized = await evaluator.IsAuthorizedAsync(
            cancellationToken: TestContext.Current.CancellationToken);

        isAuthorized.Should().BeFalse();
    }

    [Fact]
    public async Task TypedPolicy_WhenPageOverrideIsConfigured_ShouldPreferOverrideOverGlobalPolicy()
    {
        var services = CreateAuthorizationServices(
            authenticated: true,
            options =>
            {
                options.AddPolicy("global-deny", policy => policy.RequireAssertion(_ => false));
                options.AddPolicy("page-allow", policy => policy.RequireAssertion(_ => true));
            });
        await using var provider = services.BuildServiceProvider();
        var shellOptions = new ModuleShellUIOption();
        shellOptions.OperationalPageAccess.AuthorizationPolicy = "global-deny";
        var evaluator = CreateEvaluator(Environments.Production, shellOptions, provider);
        var policy = new OperationalPageAccessPolicy<TestOperationalPageOptions>(
            evaluator,
            Options.Create(new TestOperationalPageOptions
            {
                AuthorizationPolicyOverride = "page-allow"
            }));

        (await evaluator.IsAuthorizedAsync(cancellationToken: TestContext.Current.CancellationToken))
            .Should().BeFalse();
        (await policy.IsAuthorizedAsync(TestContext.Current.CancellationToken)).Should().BeTrue();
    }

    [Fact]
    public async Task IsAuthorizedAsync_WhenOverrideIsBlank_ShouldInheritGlobalPolicy()
    {
        var services = CreateAuthorizationServices(
            authenticated: true,
            options => options.AddPolicy("operations", policy => policy.RequireAuthenticatedUser()));
        await using var provider = services.BuildServiceProvider();
        var shellOptions = new ModuleShellUIOption();
        shellOptions.OperationalPageAccess.AuthorizationPolicy = "  operations  ";
        var evaluator = CreateEvaluator(Environments.Production, shellOptions, provider);

        var isAuthorized = await evaluator.IsAuthorizedAsync(
            authorizationPolicyOverride: "   ",
            TestContext.Current.CancellationToken);

        isAuthorized.Should().BeTrue();
    }

    [Fact]
    public async Task IsAuthorizedAsync_WhenPolicyIsMissingOrServicesAreUnavailable_ShouldFailClosed()
    {
        var missingPolicyServices = CreateAuthorizationServices(authenticated: true, _ => { });
        await using var missingPolicyProvider = missingPolicyServices.BuildServiceProvider();
        var shellOptions = new ModuleShellUIOption();
        shellOptions.OperationalPageAccess.AuthorizationPolicy = "missing-policy";
        var missingPolicyEvaluator = CreateEvaluator(
            Environments.Production,
            shellOptions,
            missingPolicyProvider);

        (await missingPolicyEvaluator.IsAuthorizedAsync(
                cancellationToken: TestContext.Current.CancellationToken))
            .Should().BeFalse();

        using var missingServicesProvider = new ServiceCollection().BuildServiceProvider();
        var missingServicesEvaluator = CreateEvaluator(
            Environments.Production,
            shellOptions,
            missingServicesProvider);

        (await missingServicesEvaluator.IsAuthorizedAsync(
                cancellationToken: TestContext.Current.CancellationToken))
            .Should().BeFalse();
    }

    [Fact]
    public async Task IsAuthorizedAsync_WhenAuthorizationInfrastructureThrows_ShouldFailClosed()
    {
        var services = CreateAuthorizationServices(authenticated: true, _ => { });
        services.AddSingleton<IAuthorizationPolicyProvider, ThrowingPolicyProvider>();
        await using var provider = services.BuildServiceProvider();
        var shellOptions = new ModuleShellUIOption();
        shellOptions.OperationalPageAccess.AuthorizationPolicy = "operations";
        var evaluator = CreateEvaluator(Environments.Production, shellOptions, provider);

        var isAuthorized = await evaluator.IsAuthorizedAsync(
            cancellationToken: TestContext.Current.CancellationToken);

        isAuthorized.Should().BeFalse();
    }

    [Fact]
    public async Task IsAuthorizedAsync_WhenCancellationIsRequested_ShouldPropagateCancellation()
    {
        using var services = new ServiceCollection().BuildServiceProvider();
        var evaluator = CreateEvaluator(Environments.Production, new ModuleShellUIOption(), services);
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        var act = () => evaluator.IsAuthorizedAsync(cancellationToken: cancellation.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    private static OperationalPageAccessEvaluator CreateEvaluator(
        string environmentName,
        ModuleShellUIOption shellOptions,
        IServiceProvider services) => new(
        new TestHostEnvironment(environmentName),
        Options.Create(shellOptions),
        services);

    private static ServiceCollection CreateAuthorizationServices(
        bool authenticated,
        Action<AuthorizationOptions> configure)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddAuthorizationCore(configure);
        services.AddSingleton<AuthenticationStateProvider>(
            new FixedAuthenticationStateProvider(authenticated));
        return services;
    }

    private sealed class TestOperationalPageOptions : IOperationalPageAccessOptions
    {
        public string? AuthorizationPolicyOverride { get; init; }
    }

    private sealed class ThrowingPolicyProvider : IAuthorizationPolicyProvider
    {
        public Task<AuthorizationPolicy?> GetPolicyAsync(string policyName) =>
            throw new InvalidOperationException("Policy provider failed.");

        public Task<AuthorizationPolicy> GetDefaultPolicyAsync() =>
            Task.FromResult(new AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build());

        public Task<AuthorizationPolicy?> GetFallbackPolicyAsync() =>
            Task.FromResult<AuthorizationPolicy?>(null);
    }
}

internal sealed class TestHostEnvironment(string environmentName) : IHostEnvironment
{
    public string EnvironmentName { get; set; } = environmentName;
    public string ApplicationName { get; set; } = "Test.Monica.UI";
    public string ContentRootPath { get; set; } = "/test";
    public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
}

internal sealed class FixedAuthenticationStateProvider(bool authenticated) : AuthenticationStateProvider
{
    public override Task<AuthenticationState> GetAuthenticationStateAsync()
    {
        var identity = authenticated
            ? new ClaimsIdentity([new Claim(ClaimTypes.Name, "diagnostics-reviewer")], "test")
            : new ClaimsIdentity();
        return Task.FromResult(new AuthenticationState(new ClaimsPrincipal(identity)));
    }
}
