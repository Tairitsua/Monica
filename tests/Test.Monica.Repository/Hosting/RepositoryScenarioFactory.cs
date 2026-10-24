using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.Builder;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Monica.Core.Modularity.Abstractions;
using Monica.Modules;
using Monica.Repository.Outbox.Models;
using Monica.EventBus.Annotations;
using Monica.Repository.Persistence.Models;
using Monica.Repository.Snowflake.Abstractions;
using Monica.Testing.Hosting;
using Test.Monica.Repository.Persistence;
using Test.Monica.Repository.UnitOfWork;

namespace Test.Monica.Repository.Hosting;

internal sealed class RepositoryScenarioFactory(
    bool outbox = true,
    bool inbox = false,
    IInterceptor? interceptor = null,
    Action<IServiceCollection>? configure = null,
    Action<RepositoryOutboxOptions>? configureOutbox = null,
    Action<RepositoryEntityEventOptions>? configureProjections = null,
    bool manual = false) : MonicaTestApplicationFactory<TestRepositoryDbContext>
{
    protected override void ConfigureHost(WebApplicationBuilder builder)
    {
        base.ConfigureHost(builder);
        builder.Configuration["Application:AutonomousWork"] = manual ? "false" : "true";
    }

    protected override void ConfigureMonica(IMonicaBuilder builder)
    {
        var repository = builder.AddRepository()
            .AddRepositoryDbContext<TestRepositoryDbContext>((_, db) => db.UseSqlite("Data Source=unused"));
        if (outbox)
        {
            builder.AddEventBus().UseNoOpDistributedEventBus();
            repository.AddOutbox<TestRepositoryDbContext>(options =>
            {
                options.EnableWorker = false;
                configureOutbox?.Invoke(options);
            });
            repository.AddEntityEventProjections<TestRepositoryDbContext>(options =>
            {
                options.RegisterEntity<SoftDeleteAuditRow, RowChanged>(
                    (_, change) => new RowChanged(change.Kind, new RowProjection(
                        ((SoftDeleteAuditRow)change.Entry.Entity).Id,
                        ((SoftDeleteAuditRow)change.Entry.Entity).Title)));
                options.RegisterEntity<GeneratedKeyRow, GeneratedChanged>(
                    (_, change) => new GeneratedChanged(change.Kind, new GeneratedProjection(
                        ((GeneratedKeyRow)change.Entry.Entity).Id,
                        ((GeneratedKeyRow)change.Entry.Entity).Title)));
                configureProjections?.Invoke(options);
            });
        }
        if (inbox) repository.AddInbox<TestRepositoryDbContext>();
    }

    protected override void ConfigureServices(IServiceCollection services)
    {
        base.ConfigureServices(services);
        services.AddSingleton<ISnowflakeIdGenerator, SequentialTestIdGenerator>();
        if (inbox)
        {
            services.AddScoped<FirstInboxHandler>();
            services.AddScoped<SecondInboxHandler>();
        }
        services.UseTestDatabase<TestRepositoryDbContext>((_, options) =>
        {
            if (interceptor is not null) options.AddInterceptors(interceptor);
        });
        configure?.Invoke(services);
    }
}

public sealed record RowProjection(long Id, string Title);
public sealed record GeneratedProjection(int Id, string Title);
[Outbox, EventName("tests.row-change.v1")]
public sealed record RowChanged(PersistenceChangeKind Kind, RowProjection Entity);
[Outbox, EventName("tests.generated-change.v1")]
public sealed record GeneratedChanged(PersistenceChangeKind Kind, GeneratedProjection Entity);
[Outbox, EventName("tests.async-generated-change.v1")]
public sealed record AsyncGeneratedChanged(string Title);
[Outbox, EventName("tests.notice.v1")]
public sealed record IntegrationNotice(string Value);
