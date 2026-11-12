using Microsoft.Extensions.Hosting;
using Monica.Core.Modularity.Extensions;
using Monica.Testing.Hosting;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Monica.DependencyInjection.Abstractions;
using Monica.JobScheduler.Abstractions;
using Monica.JobScheduler.EfCore;
using Monica.JobScheduler.Models;
using Monica.JobScheduler.Models.Definitions;
using Monica.JobScheduler.Models.Execution;
using Monica.JobScheduler.Providers;
using Monica.Modules;
using Monica.Repository.Persistence.Services;
using Xunit;

namespace Test.Monica.JobScheduler.Stores.Shared;

/// <summary>
/// Creates scheduler-store fixtures for either provider so behavioral contracts run against both.
/// </summary>
internal abstract class StoreFixture : IAsyncDisposable
{
    protected StoreFixture(IJobSchedulerStore store, ManualTimeProvider time)
    {
        Store = store;
        Time = time;
    }

    internal const string SCOPE = "contract-scope";
    internal const string OWNER_A = "owner-a";
    internal const string OWNER_B = "owner-b";

    internal IJobSchedulerStore Store { get; }
    internal ManualTimeProvider Time { get; }
    internal string Scope => SCOPE;

    internal static async Task<StoreFixture> CreateAsync(bool useEfCore, DateTimeOffset now, params IInterceptor[] interceptors)
    {
        return useEfCore
            ? await EfCoreStoreFixture.CreateAsync(now, interceptors)
            : new InMemoryStoreFixture(now);
    }

    internal static async Task<StoreFixture> CreateEfCoreAsync(
        DateTimeOffset now, ModuleJobSchedulerOption options, params IInterceptor[] interceptors) =>
        await EfCoreStoreFixture.CreateAsync(now, interceptors, options);

    /// <summary>
    /// Publishes one owner snapshot, explicitly authorizing replacement for scenarios that change declarations.
    /// </summary>
    internal async Task<JobDefinitionSyncResult> SyncAsync(
        string ownerKey,
        params JobDeclaration[] declarations)
    {
        var token = TestContext.Current.CancellationToken;
        var previousHash = await Store.GetOwnerManifestHashAsync(SCOPE, ownerKey, token);
        return await Store.SyncOwnerSnapshotAsync(new JobOwnerSnapshot(SCOPE, ownerKey, declarations)
        {
            ExpectedPreviousManifestHash = previousHash
        }, token);
    }

    internal static JobDeclaration RecurringDeclaration(
        string jobKey,
        string cron = "*/30 * * * * *",
        int maxConcurrency = 1,
        int retryCount = 0) => new()
    {
        JobKey = jobKey,
        JobName = jobKey,
        JobType = JobType.Recurring,
        MaxConcurrency = maxConcurrency,
        RetryCount = retryCount,
        CronExpression = cron,
        TimeZoneId = "UTC"
    };

    internal static JobDeclaration TriggeredDeclaration(string jobKey, int maxConcurrency = 1) => new()
    {
        JobKey = jobKey,
        JobName = jobKey,
        JobType = JobType.Triggered,
        MaxConcurrency = maxConcurrency,
        JobArgsKey = $"{jobKey}Args",
        MaxExecutionTimeout = TimeSpan.FromMinutes(5)
    };

    /// <summary>
    /// Synchronizes a recurring cursor for the definition and returns it.
    /// </summary>
    internal async Task<RecurringScheduleCursor> SyncCursorAsync(
        string ownerKey,
        string jobKey,
        bool debugMode = false)
    {
        var result = await Store.SynchronizeRecurringScheduleAsync(new RecurringScheduleSynchronization
        {
            CursorKey = new RecurringScheduleCursorKey
            {
                SchedulerScopeKey = SCOPE,
                OwnerKey = ownerKey,
                JobKey = jobKey
            },
            HostSuspensionReasons = debugMode
                ? JobRecurringScheduleSuspensionReason.Manual
                : JobRecurringScheduleSuspensionReason.None
        });
        return result.Cursor
               ?? throw new InvalidOperationException($"Cursor for '{ownerKey}/{jobKey}' was not created.");
    }

    internal Task<JobExecutionInstance> EnqueueTriggeredAsync(
        string ownerKey,
        string jobKey,
        string? jobArgs = "{\"value\":1}",
        string? instanceId = null,
        DateTimeOffset? availableAtUtc = null) =>
        Store.EnqueueAsync(new JobEnqueueRequest
        {
            SchedulerScopeKey = SCOPE,
            OwnerKey = ownerKey,
            JobKey = jobKey,
            InstanceId = instanceId ?? Guid.NewGuid().ToString("N"),
            JobArgs = jobArgs,
            AvailableAtUtc = availableAtUtc
        });

    internal Task<JobExecutionInstance> RunRecurringNowAsync(
        string ownerKey,
        string jobKey,
        string? instanceId = null) =>
        Store.RunRecurringNowAsync(new JobRecurringRunNowCommand
        {
            SchedulerScopeKey = SCOPE,
            OwnerKey = ownerKey,
            JobKey = jobKey,
            InstanceId = instanceId ?? Guid.NewGuid().ToString("N")
        });

    internal Task<IReadOnlyList<JobExecutionLease>> ClaimAsync(
        string ownerKey,
        string workerInstanceId,
        IReadOnlyCollection<string> jobKeys,
        int maxCount = 16,
        TimeSpan? leaseDuration = null) =>
        Store.ClaimAsync(new JobClaimRequest
        {
            SchedulerScopeKey = SCOPE,
            OwnerKey = ownerKey,
            WorkerInstanceId = workerInstanceId,
            JobKeys = jobKeys,
            MaxCount = maxCount,
            LeaseDuration = leaseDuration ?? TimeSpan.FromSeconds(30)
        });

    internal Task<JobPolicy> UpdatePolicyAsync(
        string ownerKey,
        string jobKey,
        JobPolicy policy,
        JobPolicyOverrides overrides) =>
        Store.UpdatePolicyAsync(
            SCOPE,
            ownerKey,
            jobKey,
            new JobPolicyChange
            {
                Overrides = overrides,
                ExpectedConcurrencyStamp = policy.ConcurrencyStamp
            });

    public abstract ValueTask DisposeAsync();

    internal sealed class ManualTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        private DateTimeOffset _utcNow = utcNow.ToUniversalTime();

        public override DateTimeOffset GetUtcNow() => _utcNow;

        public void Advance(TimeSpan duration) => _utcNow = _utcNow.Add(duration);
    }

    private sealed class InMemoryStoreFixture(DateTimeOffset now)
        : StoreFixture(CreateStore(new ManualTimeProvider(now), out var time), time)
    {
        private static IJobSchedulerStore CreateStore(ManualTimeProvider time, out ManualTimeProvider shared)
        {
            shared = time;
            return new InMemoryJobSchedulerStore(time);
        }

        public override ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}

file sealed class EfCoreStoreFixture : StoreFixture
{
    private readonly IHost _host;
    private readonly SqliteConnection _connection;

    private EfCoreStoreFixture(
        IJobSchedulerStore store,
        ManualTimeProvider time,
        IHost host,
        SqliteConnection connection)
        : base(store, time)
    {
        _host = host;
        _connection = connection;
    }

    internal static async Task<EfCoreStoreFixture> CreateAsync(
        DateTimeOffset now, IInterceptor[] interceptors, ModuleJobSchedulerOption? options = null)
    {
        var time = new ManualTimeProvider(now);
        var connection = new SqliteConnection(
            $"Data Source=contract-store-{Guid.NewGuid():N};Mode=Memory;Cache=Shared");
        await connection.OpenAsync();
        var builder = Host.CreateApplicationBuilder();
        builder.AddMonica(monica => monica.AddRepository()
            .AddRepositoryDbContext<JobSchedulerDbContext>(
                (_, options) => options.UseSqlite(connection.ConnectionString).AddInterceptors(interceptors), DbContextProviderType.Default));
        builder.Services.AddMonicaTestSeams();
        builder.Services.AddSingleton<TimeProvider>(time);
        var host = builder.Build();
        var factory = host.Services.GetRequiredService<IDbContextFactory<JobSchedulerDbContext>>();
        await using (var context = await factory.CreateDbContextAsync())
        {
            await context.Database.EnsureCreatedAsync();
        }

        return new EfCoreStoreFixture(
            new EfCoreJobSchedulerStore(factory, time, options is null ? null : Options.Create(options)),
            time,
            host,
            connection);
    }

    public override async ValueTask DisposeAsync()
    {
        _host.Dispose();
        await _connection.DisposeAsync();
    }

}
