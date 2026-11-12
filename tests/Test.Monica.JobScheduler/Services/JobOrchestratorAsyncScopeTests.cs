using AwesomeAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Monica.Core.Execution;
using Monica.Core.Modularity.Extensions;
using Monica.JobScheduler;
using Monica.JobScheduler.Abstractions;
using Monica.JobScheduler.Models;
using Monica.JobScheduler.Models.Definitions;
using Monica.JobScheduler.Models.Execution;
using Monica.JobScheduler.Services;
using Monica.Modules;
using NSubstitute;
using Xunit;

namespace Test.Monica.JobScheduler.Services;

public sealed class JobOrchestratorAsyncScopeTests
{
    private const string SCHEDULER_SCOPE = "async-scope-tests";

    [Fact]
    public async Task ExecuteAsync_WhenAttemptScopeHasAsyncDisposables_ShouldAwaitTheirDisposal()
    {
        var trace = new List<string>();
        using var host = BuildExecutionHost(trace);
        var definition = CreateLocalDefinition(typeof(AsyncOnlyDisposableRecurringJob));
        var registry = new JobRegistry([definition], NullLogger<JobRegistry>.Instance);
        var orchestrator = new JobOrchestrator(
            host.Services.GetRequiredService<IServiceScopeFactory>(),
            new JobExecutor(),
            registry,
            Substitute.For<IJobSchedulerStore>(),
            Options.Create(new ModuleJobSchedulerOption()),
            NullLogger<JobOrchestrator>.Instance);

        var result = await orchestrator.ExecuteAsync(
            CreateLease(definition.Declaration.JobKey),
            TestContext.Current.CancellationToken);

        result.Outcome.Should().Be(JobAttemptOutcome.Succeeded);
        trace.Should().ContainInOrder("behavior:enter", "job:execute", "behavior:exit");
        trace.Should().Contain("job:disposed");
        trace.Should().Contain("behavior:disposed");
    }

    [Fact]
    public async Task ExecuteAsync_WhenUserCodeFails_ShouldReturnFailedOutcome()
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Services.AddScoped<FailingRecurringJob>();
        builder.AddMonica(monica => monica.AddExecutionPipeline());
        using var host = builder.Build();
        var definition = CreateLocalDefinition(typeof(FailingRecurringJob));
        var orchestrator = new JobOrchestrator(
            host.Services.GetRequiredService<IServiceScopeFactory>(),
            new JobExecutor(),
            new JobRegistry([definition], NullLogger<JobRegistry>.Instance),
            Substitute.For<IJobSchedulerStore>(),
            Options.Create(new ModuleJobSchedulerOption()),
            NullLogger<JobOrchestrator>.Instance);

        var result = await orchestrator.ExecuteAsync(
            CreateLease(definition.Declaration.JobKey),
            TestContext.Current.CancellationToken);

        result.Outcome.Should().Be(JobAttemptOutcome.Failed);
        result.Message.Should().Contain("job failed");
    }

    private static IHost BuildExecutionHost(List<string> trace)
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Services
            .AddSingleton(trace)
            .AddScoped<AsyncOnlyDisposableRecurringJob>();
        builder.AddMonica(monica => monica
            .AddExecutionPipeline()
            .AddBehavior<AsyncOnlyDisposableJobBehavior>(
                descriptorFilter: static descriptor =>
                    descriptor.Point == JobSchedulerExecutionPoints.RecurringAttempt,
                lifetime: ServiceLifetime.Scoped));
        return builder.Build();
    }

    private static LocalJobDefinition CreateLocalDefinition(Type jobType)
    {
        return new LocalJobDefinition
        {
            JobClrType = jobType,
            Declaration = new JobDeclaration
            {
                JobKey = jobType.FullName!,
                JobName = jobType.Name,
                JobType = JobType.Recurring,
                MaxConcurrency = 1,
                MaxExecutionTimeout = TimeSpan.FromMinutes(1)
            }
        };
    }

    private static JobExecutionLease CreateLease(string jobKey)
    {
        var template = new JobExecutionTemplate
        {
            SchedulerScopeKey = SCHEDULER_SCOPE,
            OwnerKey = "owner-a",
            JobKey = jobKey,
            JobName = jobKey,
            JobType = JobType.Recurring,
            MaxConcurrency = 1,
            MaxExecutionTimeout = TimeSpan.FromMinutes(1)
        };
        return new JobExecutionLease
        {
            GrantedAtUtc = DateTimeOffset.UtcNow,
            Execution = new JobExecutionInstance
            {
                InstanceId = "execution-1",
                Template = template,
                AvailableAtUtc = DateTimeOffset.UtcNow,
                State = JobExecutionState.Running,
                CreatedAtUtc = DateTimeOffset.UtcNow
            },
            LeaseKey = new JobLeaseKey
            {
                SchedulerScopeKey = SCHEDULER_SCOPE,
                InstanceId = "execution-1",
                WorkerInstanceId = "worker-instance",
                LeaseToken = "lease-token"
            }
        };
    }

    private sealed class FailingRecurringJob : IRecurringJob
    {
        public Task ExecuteAsync(CancellationToken cancellationToken) =>
            Task.FromException(new InvalidOperationException("job failed"));
    }

    private sealed class AsyncOnlyDisposableRecurringJob(List<string> trace) :
        IRecurringJob,
        IAsyncDisposable
    {
        public Task ExecuteAsync(CancellationToken cancellationToken)
        {
            trace.Add("job:execute");
            return Task.CompletedTask;
        }

        public async ValueTask DisposeAsync()
        {
            await Task.Yield();
            trace.Add("job:disposed");
        }
    }

    private sealed class AsyncOnlyDisposableJobBehavior(List<string> trace) :
        IExecutionBehavior<ExecutionUnit, ExecutionUnit>,
        IAsyncDisposable
    {
        public async Task<ExecutionUnit> ExecuteAsync(
            ExecutionContext<ExecutionUnit> context,
            ExecutionDelegate<ExecutionUnit> next)
        {
            trace.Add("behavior:enter");
            var result = await next();
            trace.Add("behavior:exit");
            return result;
        }

        public async ValueTask DisposeAsync()
        {
            await Task.Yield();
            trace.Add("behavior:disposed");
        }
    }
}
