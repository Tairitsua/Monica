# UI page state and lifetime ownership

Use this reference when separating a Monica page's evidence, selection, and coordinated operations from its visual composition. A state extraction must preserve the lifetime that owns each operation; moving every mutable field into an injected service does not establish correct ownership.

## Choose the owner before extracting state

| Concern | Owner |
| --- | --- |
| Captured evidence, portable selection, computed guards, and serialized transitions shared by routed views | A scoped state borrowed from the circuit's DI scope. |
| Component cancellation, pending parameter synchronization, polling tasks, focus work, interop, and dialogs | The component that admits the work, or a session it explicitly creates for its own lifetime. |
| Fresh DbContexts or provider scopes used by a facade operation | The backend service that creates them. |
| An explicitly created page session with subscriptions or disposable resources | Its creating component, under the factory's ownership contract. |

An injected scoped state is not owned by the routed component. Do not call its `Dispose()` merely because a route is retiring. DI owns disposable services at scope retirement, and another route may still borrow the same instance. A data-only or serialized state does not need `IDisposable`. If a component explicitly creates a disposable session instead, that component must retire and drain it.

Keep visible loading status in state when several consumers need it, but keep the component's cancellation source and live task handles with their admitting owner. A state can use a semaphore to serialize transitions without taking ownership of the route lifetime. A polling preference such as interval or enabled state can be shared; a live timer, loop, and callback target require an explicit lifetime owner.

## Borrow circuit state

For interactive Blazor Server, scoped state is normally shared within one circuit, not across every tab or session belonging to a user:

```csharp
// Module ConfigureServices registration.
services.AddScoped<RepositoryDashboardState>();
```

Place feature state in `UI{Name}/State/`. Inject infrastructure facades directly; do not add a `*UIService` wrapper that merely forwards the same calls. Keep route pages as composition and query-parameter shells, and pass evidence or callbacks to children with parameters and `EventCallback`.

`RepositoryDashboardState` owns detached context evidence, selection, and migration outcomes, serializes reads and commands, and accepts each consuming component's cancellation token. The three routed workbenches own cancellation and disposal separately. See [Repository diagnostics](../../monica-infra-persistence/references/repository-diagnostics.md) for the routes and data semantics.

For transitions that must remain ordered, admit the complete read or command through one state gate. Freeze the operation's identity before awaiting, use the caller token for provider work, and recheck it before adoption. Releasing the gate only after the actual operation completes protects a later route from adopting an earlier route's stale result. Circuit serialization does not provide cross-user, cross-process, or distributed command exclusion.

A successful result envelope is not necessarily successful diagnostic data. Preserve the owning model's error/support state. Keep a command's outcome distinct from an error while refreshing evidence after the command; a failed refresh cannot undo an already completed command. Do not manufacture an empty successful result when a facade read failed.

## Keep component work observable

The following excerpt belongs to a Razor component named `RepositoryExample`. It borrows `RepositoryDashboardState`, retains the actual admitted task, and owns only its own cancellation lifetime:

```razor
@using Monica.Repository.UI.UIRepository.State
@inject RepositoryDashboardState State
@implements IAsyncDisposable

@code {
    private readonly CancellationTokenSource _lifetime = new();
    private readonly CancellationToken _token;
    private Task? _operation;
    private Task? _disposeTask;
    private bool _busy;
    private bool _retired;

    public RepositoryExample() => _token = _lifetime.Token;

    protected override Task OnInitializedAsync() => RunAsync(State.InitializeAsync);

    private Task RefreshAsync() => RunAsync(
        token => State.RefreshContextsAsync(cancellationToken: token));

    private Task RunAsync(Func<CancellationToken, Task> work)
    {
        if (_retired || _busy) return Task.CompletedTask;
        _busy = true;
        return _operation = RunCoreAsync(work);
    }

    private async Task RunCoreAsync(Func<CancellationToken, Task> work)
    {
        try { await work(_token); }
        catch (OperationCanceledException) when (_token.IsCancellationRequested) { }
        finally { if (!_retired) _busy = false; }
    }

    public ValueTask DisposeAsync() => new(_disposeTask ??= DisposeCoreAsync());

    private async Task DisposeCoreAsync()
    {
        _retired = true;
        var failures = new List<Exception>();
        try
        {
            await ObserveAsync(_lifetime.CancelAsync());
            await ObserveAsync(_operation);
        }
        finally { _lifetime.Dispose(); }

        if (failures.Count > 0)
            throw new AggregateException("Page retirement failed.", failures);

        async Task ObserveAsync(Task? task)
        {
            if (task is null) return;
            try { await task; }
            catch (OperationCanceledException) when (_token.IsCancellationRequested) { }
            catch (Exception exception) { failures.Add(exception); }
        }
    }
}
```

Keep data initialization in `OnInitializedAsync` or `OnParametersSetAsync`; DOM-dependent work belongs in `OnAfterRenderAsync`. If parameter synchronization, focus, clipboard, dialogs, or other async work is admitted, retain and independently observe each task during retirement. Stop new admission before cancellation. Drain the actual operation rather than only a cancellation wrapper; a token cannot prove that a tokenless or cancellation-ignoring provider stopped. Dispose the cancellation source after drainage, and preserve unexpected failures without abandoning other work.

Keep native dialog focus and results with MudBlazor. Opening a dialog produces an owned reference: observe a late `ShowAsync` result and close it if its component retired. Freeze a destructive action's exact identity before confirmation and use that identity afterward, rather than rereading selection. For interop and native dialog naming mechanics, use [Blazor lifecycle guidance](../../monica-ui-development/references/blazor-best-practices.md).

## Coordinate notifications and polling deliberately

A state does not need a `StateChanged` event when a component awaits an operation and the renderer already knows its completion. Add notifications only when another owner changes evidence independently. Subscribe and unsubscribe symmetrically; removing an event handler does not retire a callback that already captured the component. Marshal rendering through `InvokeAsync`, observe its task, and reject work after retirement. Never dispose borrowed state to remove one component's subscription.

For live refresh, keep the loop with its admitting component or an explicitly owned session. Follow [page auto-refresh](../../monica-ui-development/references/auto-refresh-page-pattern.md) together with the lifecycle contract: cancel and drain the previous loop before replacing its task handle or disposing its token source. Preserve detached evidence and an explicit failure/retained-snapshot state while refreshing. A manual-snapshot feature does not acquire a polling loop simply because its state is scoped.

## Decompose around operational tasks

Use a simple state bag for evidence and computed properties that need no asynchronous coordination. Add facade-calling, serialized transitions when several views share selection or mutations. Use an explicitly owned page session when disposable background work or subscriptions genuinely belong to that session. Do not select these patterns by a fixed field count.

Split independently selectable operational tasks into actual routes when they have different evidence or next actions. Preserve the exact portable identity across routes, including an identity that is currently unavailable; readable CLR labels are separate from selection IDs. Give each route a thin wrapper and let its composing component borrow shared evidence while owning its own admitted work. Browser persistence, localization, and visual hierarchy remain with their owning UI skills.

Source examples: `Monica.Repository.UI/Modules/ModuleRepositoryUI.cs`, `UIRepository/State/RepositoryDashboardState.cs`, `UIRepository/Components/RepositoryWorkbench.razor`, and `Monica.UI/UIModuleSystem/State/ModuleSystemWorkbenchSession.cs`.
