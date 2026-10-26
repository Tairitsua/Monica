using System.Data.Common;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Monica.JobScheduler.Models;
using Monica.JobScheduler.Models.Definitions;
using Monica.JobScheduler.Models.Execution;
using Xunit;

namespace Test.Monica.JobScheduler.Stores.External;

/// <summary>
/// Verifies that manifest replacement and claiming linearize at the same native job Gate.
/// </summary>
[Trait("Category", "ExternalDatabase")]
[Collection(ExternalSchedulerDatabaseCollection.NAME)]
public sealed class ExternalJobSchedulerManifestFenceTests
{
    private const string SCOPE = "external-manifest-fence";
    private const string OWNER = "manifest-owner";
    private const string JOB = "manifest-job";
    private const string INSTANCE = "queued-before-replacement";

    [Theory]
    [InlineData(ExternalSchedulerDatabase.POSTGRES)]
    [InlineData(ExternalSchedulerDatabase.VASTBASE)]
    public async Task ClaimAsync_WhenManifestChangesBeforeGateAcquisition_ShouldRejectTheStaleClaimWithoutMutatingTheQueue(string provider)
    {
        var token = TestContext.Current.CancellationToken;
        var pause = new ClaimGatePause();
        var fixture = await ExternalSchedulerStoreFixture.CreateAsync(provider, token, pause);
        await ExternalSchedulerScenario.RunAsync(fixture, async () =>
        {
            var claimant = fixture.CreateStore();
            var publisher = fixture.CreateStore();
            var declaration = new JobDeclaration
            {
                JobKey = JOB, JobName = "original declaration", JobType = JobType.Triggered,
                JobArgsKey = "test-args", MaxConcurrency = 1
            };
            var first = await publisher.SyncOwnerSnapshotAsync(new JobOwnerSnapshot(SCOPE, OWNER, [declaration]), token);
            await publisher.EnqueueAsync(new JobEnqueueRequest
            {
                SchedulerScopeKey = SCOPE, OwnerKey = OWNER, JobKey = JOB, InstanceId = INSTANCE, JobArgs = "{}"
            }, token);

            pause.Arm();
            var pendingClaim = claimant.ClaimAsync(new JobClaimRequest
            {
                SchedulerScopeKey = SCOPE, OwnerKey = OWNER, JobKeys = [JOB], WorkerInstanceId = "stale-manifest-worker",
                OwnerManifestHash = first.ManifestHash, MaxCount = 1, LeaseDuration = TimeSpan.FromMinutes(2)
            }, token);
            Exception? operationError = null;
            try
            {
                await pause.Reached.WaitAsync(TimeSpan.FromSeconds(4), token);
                // The Claim has not sent its Gate statement. This independently committed publication must be
                // visible when Claim obtains the Gate, even if Claim read H1 before it reached this barrier.
                var replacement = await publisher.SyncOwnerSnapshotAsync(new JobOwnerSnapshot(SCOPE, OWNER,
                    [declaration with { JobName = "replacement declaration" }])
                {
                    ExpectedPreviousManifestHash = first.ManifestHash
                }, token);
                replacement.ContentChanged.Should().BeTrue();
                replacement.ManifestHash.Should().NotBe(first.ManifestHash);

                pause.Resume();
                (await pendingClaim).Should().BeEmpty();
                var execution = await publisher.GetExecutionAsync(SCOPE, INSTANCE, token);
                execution.Should().NotBeNull();
                execution!.State.Should().Be(JobExecutionState.Queued);
                execution.ExecutionAttempt.Should().Be(0);
                (await publisher.GetOwnerManifestHashAsync(SCOPE, OWNER, token)).Should().Be(replacement.ManifestHash);

                await using var connection = await fixture.Database.OpenConnectionAsync(token);
                await using var gate = connection.CreateCommand();
                gate.CommandTimeout = 5;
                gate.CommandText = """
                    SELECT "ActiveCount" FROM "JobExecutionGates"
                    WHERE "SchedulerScopeKey"=@scope AND "OwnerKey"=@owner AND "JobKey"=@job
                    """;
                Add(gate, "scope", SCOPE);
                Add(gate, "owner", OWNER);
                Add(gate, "job", JOB);
                Convert.ToInt32(await gate.ExecuteScalarAsync(token)).Should().Be(0);
            }
            catch (Exception error)
            {
                operationError = error;
                throw;
            }
            finally
            {
                pause.Resume();
                // Drain the real provider operation before fixture pool disposal, retaining the first failure.
                try { await pendingClaim; }
                catch when (operationError is not null) { }
            }
        });
    }

    private static void Add(DbCommand command, string name, object value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value;
        command.Parameters.Add(parameter);
    }

    private sealed class ClaimGatePause : DbCommandInterceptor
    {
        private readonly TaskCompletionSource _reached = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _resume = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _armed;

        internal Task Reached => _reached.Task;
        internal void Arm() => Volatile.Write(ref _armed, 1);
        internal void Resume() => _resume.TrySetResult();

        public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            if (command.CommandText.Contains("JobExecutionGates", StringComparison.Ordinal)
                && command.CommandText.Contains("FOR UPDATE SKIP LOCKED", StringComparison.OrdinalIgnoreCase)
                && Interlocked.CompareExchange(ref _armed, 0, 1) == 1)
            {
                _reached.TrySetResult();
                await _resume.Task.WaitAsync(cancellationToken);
            }

            return result;
        }
    }
}
