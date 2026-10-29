# Sociable Application Test Templates

These examples belong to `Test.Roles.User`: they protect `Roles.User` behavior while loading its `Domains.User` core and selected production Host composition. The execution host does not change the test owner; see [project ownership and naming](project-naming.md).

## Project Factory

UserHostComposition is an application-owned composition method extracted from the production Host, not a Monica module per feature. It registers the real UserDbContext, domain participation and any application model required by the scenario. Include the actual role, core, Protocol, lightweight catalog where used, and actual local Host manifest assemblies in TypeDiscoveryAssemblies when composition spans them; the anchor alone supplies only its own assembly.

```csharp
public sealed class UserRoleTestApplicationFactory
    : MonicaTestApplicationFactory<CommandHandlerGrantPermission>
{
    protected override void ConfigureMonica(IMonicaBuilder monica)
    {
        UserHostComposition.Configure(monica); // Reuses the product-owned native host registrations.
    }

    protected override void ConfigureServices(IServiceCollection services)
    {
        base.ConfigureServices(services);
        services.UseTestDatabase<UserDbContext>();
    }
}
```

The factory is a stateless recipe. CreateAsync builds a new complete host; CreateScope creates only a normal child scope. Replace a boundary for one scenario through CreateAsync(seams => seams.With<IExternalDirectory>(directory)).

## Command and Repository Scenario

```csharp
public sealed class CommandHandlerGrantPermissionTests(UserRoleTestApplicationFactory factory)
    : IClassFixture<UserRoleTestApplicationFactory>
{
    private readonly UserRoleTestApplicationFactory _factory = factory;

    [Fact]
    public async Task Handle_WhenUnitExists_ShouldPersistPermission()
    {
        var token = TestContext.Current.CancellationToken;
        await using var application = await _factory.CreateAsync(cancellationToken: token);

        var unitId = await application.SeedAsync<UserDbContext, long>(async (db, ct) =>
        {
            var unit = TestOrganUnits.Create();
            db.AddRange(unit, new Permission { Id = 201, Name = "user:view" });
            await db.SaveChangesAsync(ct);
            return unit.Id;
        }, token);

        var result = await application.ExecuteAsync(scope =>
            scope.Resolve<CommandHandlerGrantPermission>().Handle(
                new CommandGrantPermission(unitId, 201), scope.CancellationToken),
            cancellationToken: token);
        result.ShouldSucceed();

        await application.VerifyAsync<UserDbContext>(async (db, ct) =>
        {
            var stored = await db.OrganUnits.AsNoTracking()
                .Include(unit => unit.Permissions).SingleAsync(unit => unit.Id == unitId, ct);
            stored.Permissions.Should().Contain(permission => permission.Id == 201);
        }, token);
    }
}
```

No handler or tracked entity escapes the act scope. No test-only save loop or tracker clearing is involved.

For a repository-focused write, use the same boundary:

```csharp
await application.ExecuteAsync(async scope =>
{
    var repository = scope.Resolve<IRepositoryPermission>();
    var permission = await repository.GetAsync(201, scope.CancellationToken);
    repository.Remove(permission);
}, cancellationToken: token);

await application.VerifyAsync<UserDbContext>(async (db, ct) =>
{
    var stored = await db.Permissions.IncludeSoftDeleted().AsNoTracking()
        .SingleAsync(permission => permission.Id == 201, ct);
    stored.IsDeleted.Should().BeTrue();
    stored.DeletionTime.Should().NotBeNull();
}, token);
```

IncludeSoftDeleted disables only Monica's soft-delete filter. Do not bypass tenant filters to inspect deleted data.

## Read-Only and Mediator Scenarios

Arrange in a seed scope first. Then a direct read can resolve IOrganUnitQueries in a fresh application.CreateScope(token). To exercise the mediator adapter, resolve IMediator in that scope and Send an IQuery<T> request. Its marker avoids a new transaction by default while preserving participation in an existing Shared transaction.

## Durable Notification Scenario

Configure AddOutbox in the production composition; keep its entity projections in tests. Replace `IEventTransport` with a recording transport while retaining the scoped bus gateway.

```csharp
await application.ExecuteAsync(async scope =>
{
    await scope.Resolve<IDistributedEventBus>().PublishAsync(
        new UserChangedV1(userId), cancellationToken: token);
}, cancellationToken: token);

await application.VerifyAsync<UserDbContext>(async (db, ct) =>
{
    var message = await db.Set<OutboxMessage>().SingleAsync(ct);
    message.DeliveredAtUtc.Should().BeNull();
}, token);

await application.DrainOutboxAsync<UserDbContext>(cancellationToken: token);
application.Services.GetRequiredService<RecordingEventBus>()
    .Messages.Should().ContainSingle();
```

The event type must carry `[Outbox]` and stable `[EventName]` metadata. When testing a local subscription, subscribe to the ordinary event type and exact topic. Test stable MessageId and consumer deduplication under retry.

## Module Composition

Assert application.ModuleSnapshots for module registration. Resolve scoped repositories inside application.CreateScope; do not resolve them from the root Services provider.

## Raw ProjectUnit Fast Path

Use ProjectUnitFixture<TUnit>.Builder().WithSubstitute<TCollaborator>(out var collaborator).Build() only for explicit collaboration. It does not validate transactions or production composition. Entity invariants can use ordinary constructors without a host.
