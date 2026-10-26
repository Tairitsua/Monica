using Microsoft.EntityFrameworkCore;
using Monica.DependencyInjection.Abstractions;
using Monica.JobScheduler.EfCore.Entities;
using Monica.JobScheduler.Models;
using Monica.JobScheduler.Models.Definitions;
using Monica.Repository.Persistence.Services;

namespace Monica.JobScheduler.EfCore;

/// <summary>
/// Owns the relational correctness boundary for job definitions and the durable execution queue.
/// </summary>
public sealed class JobSchedulerDbContext(
    DbContextOptions<JobSchedulerDbContext> options,
    ICachedServiceProvider serviceProvider)
    : RepositoryDbContext<JobSchedulerDbContext>(options, serviceProvider)
{
    internal DbSet<JobDefinitionEntity> Definitions => Set<JobDefinitionEntity>();
    internal DbSet<JobExecutionEntity> Executions => Set<JobExecutionEntity>();
    internal DbSet<JobExecutionHistoryEntity> ExecutionHistory => Set<JobExecutionHistoryEntity>();
    internal DbSet<JobExecutionGateEntity> ExecutionGates => Set<JobExecutionGateEntity>();
    internal DbSet<JobRecurringCursorEntity> RecurringCursors => Set<JobRecurringCursorEntity>();
    internal DbSet<JobSchedulerOwnerEntity> SchedulerOwners => Set<JobSchedulerOwnerEntity>();
    internal DbSet<JobSchedulerCoordinatorEntity> Coordinators => Set<JobSchedulerCoordinatorEntity>();

    protected override void OnModelCreatingExtend(ModelBuilder modelBuilder)
    {
        base.OnModelCreatingExtend(modelBuilder);
        ConfigureDefinitions(modelBuilder);
        ConfigureExecution(modelBuilder);
        RemoveInheritedStringDefaults(modelBuilder);
    }

    private static void ConfigureDefinitions(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<JobSchedulerOwnerEntity>(entity =>
        {
            entity.ToTable("JobSchedulerOwners");
            entity.HasKey(row => new { row.SchedulerScopeKey, row.OwnerKey });
            entity.Property(row => row.SchedulerScopeKey).HasMaxLength(JobSchedulerIdentity.STANDARD_MAX_LENGTH);
            entity.Property(row => row.OwnerKey).HasMaxLength(JobSchedulerIdentity.STANDARD_MAX_LENGTH);
            entity.Property(row => row.ManifestHash).HasMaxLength(64);
        });
        modelBuilder.Entity<JobSchedulerCoordinatorEntity>(entity =>
        {
            entity.ToTable("JobSchedulerCoordinators");
            entity.HasKey(row => new { row.SchedulerScopeKey, row.CoordinatorKey });
            entity.Property(row => row.SchedulerScopeKey).HasMaxLength(JobSchedulerIdentity.STANDARD_MAX_LENGTH);
            entity.Property(row => row.CoordinatorKey).HasMaxLength(256);
            entity.Property(row => row.WorkerInstanceId).HasMaxLength(JobSchedulerIdentity.STANDARD_MAX_LENGTH);
            entity.Property(row => row.LeaseToken).HasMaxLength(32);
        });

        modelBuilder.Entity<JobDefinitionEntity>(entity =>
        {
            entity.ToTable("JobDefinitions");
            entity.HasKey(item => new { item.SchedulerScopeKey, item.OwnerKey, item.JobKey });
            entity.Property(item => item.SchedulerScopeKey).HasMaxLength(JobSchedulerIdentity.STANDARD_MAX_LENGTH);
            entity.Property(item => item.OwnerKey).HasMaxLength(JobSchedulerIdentity.STANDARD_MAX_LENGTH);
            entity.Property(item => item.JobKey).HasMaxLength(JobSchedulerIdentity.JOB_KEY_MAX_LENGTH);
            entity.Property(item => item.DeclarationJson).HasColumnType("text");
            entity.Property(item => item.PolicyOverridesJson).HasColumnType("text");
            entity.Property(item => item.PolicyConcurrencyStamp).HasMaxLength(32);
            entity.Property(item => item.JobName).HasMaxLength(JobDeclaration.JOB_NAME_MAX_LENGTH);
            entity.Property(item => item.Description).HasColumnType("text");
            entity.Property(item => item.JobType).HasConversion<string>().HasMaxLength(16);
            entity.Property(item => item.ConcurrencyToken).IsConcurrencyToken();
            entity.HasIndex(item => new { item.SchedulerScopeKey, item.OwnerKey });
        });

        modelBuilder.Entity<JobRecurringCursorEntity>(entity =>
        {
            entity.ToTable("JobRecurringCursors");
            entity.HasKey(item => new { item.SchedulerScopeKey, item.OwnerKey, item.JobKey });
            entity.Property(item => item.SchedulerScopeKey).HasMaxLength(JobSchedulerIdentity.STANDARD_MAX_LENGTH);
            entity.Property(item => item.OwnerKey).HasMaxLength(JobSchedulerIdentity.STANDARD_MAX_LENGTH);
            entity.Property(item => item.JobKey).HasMaxLength(JobSchedulerIdentity.JOB_KEY_MAX_LENGTH);
            entity.Property(item => item.TemplateJson).HasColumnType("text");
            entity.Property(item => item.ScheduleJson).HasColumnType("text");
            entity.Property(item => item.SuspensionReasons).HasConversion<int>();
            entity.Property(item => item.ConcurrencyToken).IsConcurrencyToken();
            entity.HasIndex(item => new { item.SchedulerScopeKey, item.OwnerKey, item.NextOccurrenceUtcTicks });
        });

        modelBuilder.Entity<JobExecutionGateEntity>(entity =>
        {
            entity.ToTable("JobExecutionGates");
            entity.HasKey(item => new { item.SchedulerScopeKey, item.OwnerKey, item.JobKey });
            entity.Property(item => item.SchedulerScopeKey).HasMaxLength(JobSchedulerIdentity.STANDARD_MAX_LENGTH);
            entity.Property(item => item.OwnerKey).HasMaxLength(JobSchedulerIdentity.STANDARD_MAX_LENGTH);
            entity.Property(item => item.JobKey).HasMaxLength(JobSchedulerIdentity.JOB_KEY_MAX_LENGTH);
            entity.Property(item => item.ConcurrencyToken).IsConcurrencyToken();
        });
    }

    private static void ConfigureExecution(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<JobExecutionEntity>(entity =>
        {
            entity.ToTable("JobExecutions");
            entity.HasKey(item => new { item.SchedulerScopeKey, item.InstanceId });
            entity.Property(item => item.SchedulerScopeKey).HasMaxLength(JobSchedulerIdentity.STANDARD_MAX_LENGTH);
            entity.Property(item => item.InstanceId).HasMaxLength(JobSchedulerIdentity.STANDARD_MAX_LENGTH);
            entity.Property(item => item.TemplateJson).HasColumnType("text");
            entity.Property(item => item.OwnerKey).HasMaxLength(JobSchedulerIdentity.STANDARD_MAX_LENGTH);
            entity.Property(item => item.JobKey).HasMaxLength(JobSchedulerIdentity.JOB_KEY_MAX_LENGTH);
            entity.Property(item => item.JobArgs).HasColumnType("text");
            entity.Property(item => item.Origin).HasConversion<string>().HasMaxLength(30);
            entity.Property(item => item.SkipReason).HasConversion<string>().HasMaxLength(40);
            entity.Property(item => item.State).HasConversion<string>().HasMaxLength(20);
            entity.Property(item => item.RunningWorkerInstanceId).HasMaxLength(JobSchedulerIdentity.STANDARD_MAX_LENGTH);
            entity.Property(item => item.ExecutionLeaseToken).HasMaxLength(32);
            entity.Property(item => item.ConcurrencyToken).IsConcurrencyToken();
            entity.HasIndex(item => new
            {
                item.SchedulerScopeKey,
                item.State,
                item.AvailableAtUtcTicks,
                item.OwnerKey
            });
            entity.HasIndex(item => new { item.SchedulerScopeKey, item.OwnerKey, item.JobKey, item.State });
            entity.HasIndex(item => new { item.SchedulerScopeKey, item.State, item.LeaseExpiresAtUtcTicks, item.InstanceId });
            entity.HasIndex(item => new { item.SchedulerScopeKey, item.OwnerKey, item.JobKey, item.State,
                item.AvailableAtUtcTicks, item.CreatedAtUtcTicks, item.InstanceId });
            entity.HasIndex(item => new { item.SchedulerScopeKey, item.OwnerKey, item.JobKey, item.CreatedAtUtcTicks });
            entity.HasMany(item => item.History)
                .WithOne(item => item.Execution)
                .HasForeignKey(item => new { item.SchedulerScopeKey, item.InstanceId })
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<JobExecutionHistoryEntity>(entity =>
        {
            entity.ToTable("JobExecutionHistory");
            entity.HasKey(item => new { item.SchedulerScopeKey, item.InstanceId, item.Sequence });
            entity.Property(item => item.SchedulerScopeKey).HasMaxLength(JobSchedulerIdentity.STANDARD_MAX_LENGTH);
            entity.Property(item => item.InstanceId).HasMaxLength(JobSchedulerIdentity.STANDARD_MAX_LENGTH);
            entity.Property(item => item.Kind).HasConversion<string>().HasMaxLength(30);
            entity.Property(item => item.PreviousState).HasConversion<string>().HasMaxLength(20);
            entity.Property(item => item.NewState).HasConversion<string>().HasMaxLength(20);
            entity.Property(item => item.LogLevel).HasConversion<string>().HasMaxLength(20);
            entity.Property(item => item.Message).HasColumnType("text");
            entity.Property(item => item.WorkerInstanceId).HasMaxLength(JobSchedulerIdentity.STANDARD_MAX_LENGTH);
        });
    }

    private static void RemoveInheritedStringDefaults(ModelBuilder modelBuilder)
    {
        // Scheduler identities and payloads are always written explicitly. Database-side empty-string
        // defaults would weaken required-key validation and erase the meaning of nullable fields.
        foreach (var property in modelBuilder.Model.GetEntityTypes()
                     .SelectMany(entityType => entityType.GetProperties())
                     .Where(property => property.ClrType == typeof(string)))
        {
            property.SetDefaultValue(null);
        }
    }
}
