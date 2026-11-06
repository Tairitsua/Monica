# Blazor UI Best Practices

## Overview

This document defines best practices for developing Blazor UI in the Monica framework, focusing on code quality, maintainability, and user experience.

## 1. Component Architecture Principles

### 1.1 Component Hierarchy

- **Base Components (Common)**: Reusable atomic components
- **Business Components (Business)**: Feature-specific composite components
- **Page Components (Pages)**: Complete page-level components

### 1.2 Single Responsibility Principle

- Each component handles one specific function
- Complex features combine multiple simple components
- Separate presentation logic from business logic

### 1.3 Component Communication

- Use Parameters for parent-to-child communication
- Use EventCallback for child-to-parent events
- Use state containers for complex state management

For string-typed component parameters, use an explicit Razor expression for state values, such as `Draft="@visit.Draft"` or `InspectedSessionId="@_inspectedSessionId"`. Without `@`, those values are literal text even though non-string parameters accept expressions implicitly. Check the actual native input value and inspected identity in the browser; a successful build or correct nearby caption does not prove the binding.

A reused component retains parameter values that the next render omits; a property initializer supplies its construction default, not a reset on every render. Pass transition values explicitly, including `false` or `null` when clearing prior state. For example, a shared access surface must receive `IsChecking="false"` when a pending check becomes Denied. Use a different `@key` only when the state really needs a new component lifetime. Verify the settled native transition, including `aria-busy`, live-region content and any pending indicator, rather than checking only the updated heading.

When adjacent branches reuse the same keyed child and the parent needs its reference for post-render interop, capture that reference consistently in every branch that can create the child. Adding `@ref` only in a later branch can leave the owner without a capture when Blazor reuses the existing component. Verify a real pending-to-terminal transition and the acknowledged focus target; a direct visit to the terminal branch does not exercise this reuse path. Keep the reference with that owner rather than introducing another focus coordinator.

When a page resets a debounced native input, reset the input owner as well as the filtered model. In MudBlazor 9, `MudTextField.ClearAsync()` through a component reference clears its internal text/value path; changing only a parent query can restore rows while leaving stale text visible. Native browser checks must assert both the actual input value and the restored results, re-enter the identical query, and cover clearing a pending debounce, rather than treating row counts alone as a successful reset.

### 1.4 Native Dialog Headings

Use `Monica.UI.Shared.Components.Dialogs.MoDialogHeading` inside a native `MudDialog`'s `TitleContent` when the modal needs a localized accessible name. Supply the caller's localized `Text`; optional `Class` adds presentation classes. The normal Monica shell supplies the native dialog provider. Keep dialog focus, Escape, backdrop and result handling with MudBlazor.

Choose initial dialog focus through the native `DefaultFocus` contract. For an approval with Reject before Approve and no earlier focusable content, `DefaultFocus.FirstChild` focuses Reject. A child component's first-render `FocusAsync` can be overwritten by the enclosing trap's initialization; do not make two owners compete. Verify the actual initial target and Tab sequence after the whole dialog mounts.

Keep an async dialog's exact native launch control focusable while its preview/read and dialog result drain. MudBlazor v9 captures the active element when the focus trap mounts and calls that element's `focus()` when the trap is disposed; disabling the opener before either step can leave focus on the page body. Capture the opener's identity before the first busy publication, preserve only that button's native enabled state, and use `aria-disabled` plus an admission guard to reject repeat requests. Release the identity after the owned chain completes. Apply the same rule to a nested inspector, without adding a competing manual focus owner. Verify actual Close and Escape restoration after the native trap settles. Set `CloseOnEscapeKey` explicitly when dismissal is intended; the v9 native dialog fallback is false.

For a built-in `MudMenu` launch, v9 closes the menu and restores focus to its activator before invoking the item's callback. Preserve that actual activator through the admitted dialog chain and keep its command items unavailable while busy. Arbitrary menu `UserAttributes` land on its outer element, so an `aria-disabled` attribute there does not describe its native button. Inspect the installed activator contract before replacing built-in content; do not add a competing focus broker to compensate for disabling the native opener.

Check first-Escape dismissal for mouse-open built-in icon menus. MudBlazor v9.11.0's portaled wrapper lacks `tabindex`, and the activator root tracks keyboard mode rather than dismissing Escape. If the activator retains focus and the first Escape fails to close, bind `@bind-Open` rather than reading the unbound `MudMenu.Open` parameter, and add an Escape fallback on a real, nonfocusable wrapper around the native activator. While the component is active and the bound state is open, await its menu reference's `CloseAllMenusAsync()` to preserve native closure and focus return; keep portaled item keys native, with no JavaScript or custom focus broker. Set `AriaLabel` to name the generated icon button; an outer `title` does not name that button. Verify actual focus after settled mouse-open, dismissal on exactly the first Escape, return to the exact native opener, and the same behavior after reopening. A later Escape after focus migrated does not establish the initial path; recheck other dependency versions.

A shared cursor menu needs a distinct identity for each opening, even A → B → A at identical coordinates. Capture its action, retained context and completion callbacks before awaiting; an old action must not close a newer opening. Serialize the actual native open/close tasks against the latest desired state, and release programmatic-close suppression in `finally` without turning an action fault into successful dismissal. MudBlazor v9 clears item registration during close; key the rendered item/focus subtree to a stable presentation revision so a coalesced close/reopen creates fresh native registrations. Position-only changes can replace presentation without retargeting an already-admitted action. Retirement seals admission and observes native work already started, without manually disposing the rendered menu or waiting on a caller-owned dialog whose routed parent must retire it. Verify reopening, native keyboard admission, dialog handoff and retirement in real callers; pure interleaving tests do not prove native focus or popup placement.

In MudBlazor 9, `MudDialog.UserAttributes` are applied to its content rather than the enclosing `role="dialog"` owner. Putting `aria-label` on that component does not name the modal. `MoDialogHeading` mounts a unique visible heading, then its local module assigns `aria-labelledby` and `aria-modal` to the nearest native dialog after render. It owns and drains its import/use/disposal, including a late import after retirement; it does not mutate DOM during disposal or create callbacks. Verify the outer dialog's localized accessible name and native focus restoration in the browser.

An intentionally headerless dialog can mount the same visible `MoDialogHeading` inside its content, such as a search results heading. Keep its typography in that component's isolated CSS. Do not enable a redundant native title row or assume that the service's hidden title names the modal.

## 2. Lifecycle Best Practices

### 2.1 Separate Data Initialization from DOM-Dependent JS Interop

```csharp
private IReadOnlyList<Item> _items = [];

protected override async Task OnInitializedAsync()
{
    _items = await DataService.GetItemsAsync();
}

protected override async Task OnAfterRenderAsync(bool firstRender)
{
    if (!firstRender)
    {
        return;
    }

    // This call belongs here because it requires the rendered element.
    await JSRuntime.InvokeVoidAsync("initializeKeyboardNavigation");
}
```

Use initialization and parameter lifecycle methods for data work. Keep the component renderable before an incomplete await so loading UI can be displayed safely. Reserve `OnAfterRenderAsync` for DOM-dependent coordination; it is re-entrant and does not automatically rerender the component when its returned task completes.

`ElementReference` parameters are render-time value copies. A parent's `@ref` populated later in that render does not update a child parameter that already received its default value. For child-owned interop, pass the child's own rendered native element and resolve the relevant ancestor locally. Otherwise explicitly coordinate a parent render after its reference is ready. Verify the initial native mount, rather than relying on a later data refresh to repair reference timing.

### 2.2 Bound Async Work to the Component Lifetime

Blazor component code is re-entrant at every incomplete `await`. Keep the component renderable before awaiting data, stop new admission when it retires, pass a cached lifetime token where possible, and recheck cancellation/retirement before adopting evidence or invoking UI work. Retrieve the token while its cancellation source is live rather than reading the source's `Token` property after a disposal window.

Cancellation alone does not establish completion. Retain the actual admitted data, parameter-synchronization, polling, focus, or clipboard task; cancel and independently drain each owned task before disposing its cancellation source or other resources. A tokenless or cancellation-ignoring provider can continue after a cancellation wrapper completes. A failure while observing one task must not abandon another; guarantee final resource release and preserve unexpected failures.

When admission and retirement share a lock, reserve each action's completion task and the single disposal completion while holding that lock. Mark retirement and snapshot admitted work atomically, then release the lock before invoking external async work, cancellation, native dialog `Close`, callbacks, or cleanup. An async method runs synchronously until its first incomplete await, so assigning `_disposeTask ??= DisposeCoreAsync()` inside the lock can already invoke external code. A `TaskCompletionSource` with `RunContinuationsAsynchronously` can publish the completion before execution starts; complete it only after the actual work or teardown finishes. Repeated retirement calls share that completion and cannot start another teardown. This avoids lock inversion when a sampler holds its own lock while notifying a callback that re-enters component admission. Preserve renderer ownership when starting the work; releasing the lock does not require `Task.Run`. Verify callback re-entry during actions and retirement with controlled non-rendering interleavings.

A circuit-scoped state injected from DI is borrowed by its routed consumers. It can hold detached evidence and serialize operations while accepting the component's token; the component owns its own cancellation and admitted work, not disposal of the injected state. A component-created session is different: follow its explicit ownership contract and drain it before disposal. The [page-state pattern](../../monica-architecture/references/page-state-pattern.md) supplies a complete component-owned cancellation/drain example and explains the state boundary.

When a public persistence operation is intentionally uncancelled after admission, route retirement must drain its actual producer and captured reconciliation callback. Reconcile applied outcomes with the borrowed draft state even after the route retires, while suppressing retired UI work. Closing the dialog or abandoning a cancellable await does not establish that persistence stopped.

An async dialog open or JS import produces a resource. Observe the actual task and close/dispose a result arriving after retirement, instead of canceling the await and losing the result. Dialog results, native focus ownership, and interop teardown have their own mechanics; see the dialog heading contract above and the interop section below.

Native dialog results may resume their consumers synchronously inside `Close` or `Dismiss`. Publish a reserved close-completion task before invoking either method, then complete that reservation only after renderer dispatch finishes. Assigning `closing = CloseAsync()` is insufficient when `CloseAsync` can finish the result during its synchronous prefix; the result consumer can observe the old task and miss pending work or a dispatch failure. Verify this interleaving with a native-shaped completion source without `RunContinuationsAsynchronously`, including dispatch that closes the reference before remaining pending or throwing.

MudBlazor v9's `DialogReference.Close` raises a service event; the provider completes the result through `Dismiss`. A disposed provider unsubscribes without completing every reference. During owned retirement, dispatch normal `Close` first, then use `Dismiss(DialogResult.Cancel())` if its actual result remains incomplete after that dispatch. This avoids draining forever on an already retired provider. Do not dismiss first while a provider is live: its later close handler can then skip removing the already completed reference. Verify the fallback with the real native reference and an unsubscribed service, independently of the inline-result dispatch test.

Escape or the native close button can complete a dialog result before the renderer disposes its component. Capture the acquired component in the dialog reference's lease before awaiting the native result; retirement must not depend on a later lookup of the rendered component. A routed dialog owner that needs result completion to establish drained work should initiate and await that component's idempotent async retirement after the native result, as well as any close dispatch. The native provider will subsequently dispose the rendered component again. Retain the actual native result when callers need to reconcile an admitted persistence outcome; resource retirement must not turn a saved result into cancellation. Do not await an operation's own lifetime retirement from inside that admitted operation; it would wait for itself. Explicit Cancel can begin retirement, close the native result immediately, and then await drainage outside admission.

Keep an existing opening-acknowledgement contract separate from the private result/component lifetime. A request that previously completed when its native reference was acquired need not start waiting for dialog closure to repair ownership. Reserve both completions before external calls, coalesce pending opening requests and guard reopening through child retirement. Retain incomplete or failed internal chains until teardown observes them; prune successful completed chains so normal repeated use does not accumulate historical operations. Test a successful opening acknowledgement while the actual result or child disposal is still held, then prove retirement and late failures remain owned independently.

A child inspector belongs to both its routed owner and its originating dialog. Link those lifetimes, reject a late native dialog reference after either retires, and close/dismiss an acquired child before draining its parent's callback. Keep renderer-bound cancellation callbacks on the renderer: `CancellationTokenSource.CancelAsync()` schedules callbacks without preserving that dispatcher, so a registration that directly closes a native dialog cannot assume it runs on the UI context. Cancel synchronously from renderer-owned retirement, or explicitly dispatch the callback and observe its actual task.

When page work awaits a native dialog result or JavaScript callback completion, start retirement of those resource owners before awaiting the page's admitted work. Sequentially draining the page first can deadlock on a result that only dialog retirement will close, or on a callback that only interop shutdown will release. Initiate each owner's idempotent retirement, then observe all completions; an early failure must not leave another owner running. Facades injected into these sessions remain borrowed, while a dialog's refresh/search producers belong to that dialog and must be drained after it closes.

### 2.3 Keep Single Ownership of JS Interop References

```csharp
@implements IAsyncDisposable
@inject IJSRuntime JSRuntime

@code {
    private IJSObjectReference? _module;
    private bool _disposed;

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (!firstRender || _disposed)
        {
            return;
        }

        // Always observe resource-producing imports so a late result can be disposed.
        var module = await JSRuntime.InvokeAsync<IJSObjectReference>(
            "import",
            "./component.js");

        if (_disposed)
        {
            await DisposeModuleAsync(module);
            return;
        }

        _module = module;
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        var module = Interlocked.Exchange(ref _module, null);
        if (module is not null)
        {
            await DisposeModuleAsync(module);
        }
    }

    private static async ValueTask DisposeModuleAsync(IJSObjectReference module)
    {
        try
        {
            await module.DisposeAsync();
        }
        catch (JSDisconnectedException)
        {
            // Expected when a Blazor Server circuit is already disconnected.
        }
    }
}
```

Do not leave a disposed module published in a field, and do not assign a module whose import completed after component disposal. Avoid canceling resource-producing imports when cancellation can abandon their late result. Atomically detaching a field prevents new acquisitions but does not protect callers that already captured the reference; serialize with or drain those callers before disposal. Stop JavaScript callback producers before disposing any retained `DotNetObjectReference`. A retried controller factory must retain its owned callback reference or explicitly release an earlier acquisition before replacing it. Use a client-side `MutationObserver` for DOM cleanup instead of invoking DOM-cleanup JavaScript from `DisposeAsync`. Do not catch `ObjectDisposedException` from a locally owned interop reference; it identifies an ownership or teardown-order race.

A `ResizeObserver` that selects an interaction mode belongs to the rendered component's JavaScript session and observes its actual bounded workspace. Keep the observed root, pending measurements, and callback completions instance-owned. Coalesce queued measurements while a .NET callback is pending. If the latest observation returns to the in-flight value, clear an obsolete pending update instead of publishing it afterward. Shutdown disconnects the observer, clears pending measurements, and drains already-started callbacks before releasing the callback reference. An overlapping old/new route must not borrow the other's observer or mutate its panes. Use [composition guidance](monica-workbench-design-language.md#design-responsive-and-scroll-ownership-explicitly) for pane preferences and threshold transitions.

A browser reload can cancel an admitted JS import, observer factory, or final reference release with `OperationCanceledException` rather than `JSDisconnectedException`. Retirement alone does not establish the cancellation's cause. Treat it as expected only when it is attributable to the owner's actual cancellation or verified disconnection; an unrelated cancellation or timeout remains observable, including during retirement. Independently drain admitted operations, stop the observer and its callbacks, and release the callback and module references. Continue observing the actual resource-producing task so a successful late result is still released. Exercise late success, attributable cancellation, and unexpected live or retired failures with controlled non-rendering interleaving tests.

## 3. Performance Optimization

### 3.1 Use @key for List Rendering

```razor
@foreach (var item in Items)
{
    <div @key="item.Id">
        <ItemComponent Item="@item" />
    </div>
}
```

### 3.2 Avoid Unnecessary Rerenders

```csharp
// Control rendering with ShouldRender
protected override bool ShouldRender()
{
    // Only rerender when data actually changes
    return _hasDataChanged;
}
```

### 3.3 Use Virtualization for Large Data Sets

```razor
<MudVirtualize Items="@LargeDataSet" Context="item">
    <ItemTemplate>
        <ItemDisplay Item="@item" />
    </ItemTemplate>
</MudVirtualize>
```

## 4. Style Management

### 4.1 Use CSS Variables

```css
:root {
    --mo-primary-color: #7e6fff;
    --mo-spacing-unit: 8px;
    --mo-border-radius: 4px;
}

.mo-component {
    padding: calc(var(--mo-spacing-unit) * 2);
    border-radius: var(--mo-border-radius);
}
```

### 4.2 Component Style Isolation

- Use `.razor.css` files for component style isolation
- Avoid global style selectors
- Use BEM naming convention or CSS Modules

### 4.3 Responsive Design

```css
.mo-container {
    display: grid;
    grid-template-columns: repeat(auto-fit, minmax(300px, 1fr));
    gap: var(--mo-spacing-unit);
}

@media (max-width: 768px) {
    .mo-container {
        grid-template-columns: 1fr;
    }
}
```

## 5. State Management

### 5.1 Use State Containers

```csharp
public class AppStateContainer
{
    private string _userName = string.Empty;

    public string UserName
    {
        get => _userName;
        set
        {
            _userName = value;
            NotifyStateChanged();
        }
    }

    public event Action? OnChange;

    private void NotifyStateChanged() => OnChange?.Invoke();
}
```

### 5.2 Use State Containers in Components

```csharp
@inject AppStateContainer AppState
@implements IDisposable

@code {
    protected override void OnInitialized()
    {
        AppState.OnChange += StateHasChanged;
    }

    public void Dispose()
    {
        AppState.OnChange -= StateHasChanged;
    }
}
```

Draft review and undo must capture the originating route and the exact draft and validation-issue objects being reviewed. Reject undo after that route retires, and remove an item only if the current state still matches its captured object. Reconcile a save by its actual applied request IDs with the same comparison. A newer draft or issue at the same path must survive an older review's undo or persistence completion.

Knowledge/RAG management state has a visit lifetime, not a circuit lifetime. Inject `KnowledgeBaseManagePageStateFactory` or `RAGManagePageStateFactory`, create state with `action => InvokeAsync(action)`, await its `Func<Task>` notifications, unsubscribe, and await `DisposeAsync` from the page's `IAsyncDisposable` implementation. Returned state and its private poller belong to the page; facade and UI-service dependencies remain container-owned. Do not retain former scoped disposable-state registrations. `KnowledgePageSession` reuses `AIAdministrationOperationLifetime` to admit and drain producers, renderer dispatch, and dialogs. Verify retirement during an actual incomplete read and renderer publication, plus A → B → A selection changes; cancellation must not abandon a tokenless facade read. Every destructive/source command captures its base and document before awaiting confirmation. See the owning [Knowledge/RAG contract](../../monica-infra-ai/references/knowledge-and-rag.md#page-owned-management-state) for migration and error/refresh APIs.

## 6. Error Handling

### 6.1 Use ErrorBoundary

```razor
<ErrorBoundary>
    <ChildContent>
        <ComplexComponent />
    </ChildContent>
    <ErrorContent Context="exception">
        <MudAlert Severity="Severity.Error">
            The operation could not be completed.
        </MudAlert>
    </ErrorContent>
</ErrorBoundary>
```

### 6.2 Service Layer Error Handling

```csharp
public async Task<Result<TData>> GetDataAsync(CancellationToken cancellationToken)
{
    try
    {
        var data = await FetchDataAsync(cancellationToken);
        return Result.Success(data);
    }
    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
    {
        throw;
    }
    catch (Exception ex)
    {
        Logger.LogError(ex, "Failed to get data");
        return Result.Failure<TData>("The data could not be loaded.", ResultStatus.InternalError);
    }
}
```

## 7. Form Handling

### 7.1 Use EditForm with Validation

```razor
<EditForm Model="@model" OnValidSubmit="@HandleValidSubmit">
    <DataAnnotationsValidator />
    <ValidationSummary />

    <MudTextField @bind-Value="model.Name"
                  Label="Name"
                  For="@(() => model.Name)" />

    <MudButton ButtonType="ButtonType.Submit"
               Variant="Variant.Filled"
               Color="Color.Primary">
        Submit
    </MudButton>
</EditForm>
```

### 7.2 Custom Validation

Configuration editor identities come from the scanned schema: use its `RelativePath` for commands, search selection, and deep links, and its aliased names for JSON fields. Never reconstruct these identities from CLR property names. CLR member names returned by object validation require the owning validation layer's schema mapping; see [options and validation](../../monica-infra-configuration/references/options-and-validation.md).

Portable field or fragment validation is not authoritative complete-object approval. Stage related sibling edits together and use the complete-group preview and save boundary to enforce their combined constraints; keep the two validation scopes explicit in the UI.

```csharp
public class CustomValidator : ComponentBase
{
    [CascadingParameter]
    private EditContext? CurrentEditContext { get; set; }

    protected override void OnInitialized()
    {
        if (CurrentEditContext is null)
        {
            throw new InvalidOperationException(
                $"{nameof(CustomValidator)} requires a cascading parameter of type {nameof(EditContext)}.");
        }

        CurrentEditContext.OnValidationRequested += ValidateModel;
    }

    private void ValidateModel(object? sender, ValidationRequestedEventArgs e)
    {
        // Custom validation logic
    }
}
```

## 8. Accessibility

### 8.1 Use Semantic HTML

```razor
<nav aria-label="Main navigation">
    <ul>
        <li><a href="/">Home</a></li>
        <li><a href="/about">About</a></li>
    </ul>
</nav>
```

### 8.2 Keyboard Navigation Support

```razor
<div @onkeydown="HandleKeyDown" tabindex="0">
    <!-- Keyboard navigable content -->
</div>

@code {
    private void HandleKeyDown(KeyboardEventArgs e)
    {
        switch (e.Key)
        {
            case "ArrowUp":
                // Handle up navigation
                break;
            case "ArrowDown":
                // Handle down navigation
                break;
        }
    }
}
```

### 8.3 Give Temporary Drawers a Modal Keyboard Lifecycle

A temporary `MudDrawer` supplies placement and its overlay; give an inspector an accessible dialog name, initial focus, contained Tab navigation, Escape dismissal, and return focus to its opening control. Use the existing open-state callback for every dismissal so selection, URL state, and cached data follow the same lifecycle.

In MudBlazor v9, mount `MudFocusTrap` conditionally inside an open drawer, with `DefaultFocus.FirstChild` and a first focusable Close control. Its initial render saves the opening focus and disposal restores it. Keeping a trap mounted and toggling `Disabled` does not save focus for each opening or restore it on disabling. Let the primitive own its interop; do not add a second JavaScript focus manager. Put header and locally scrolling body inside the trap, and keep the header outside that body’s scroll area. Verify focus entry, forward and reverse Tab containment, Escape, overlay dismissal, and focus return in the actual browser. A drawer opened directly through a URL has no opening control to restore.

### 8.4 Serialize ARIA States as Strings

Native Razor markup must emit literal `"true"` and `"false"` for ARIA state attributes. Binding a C# `bool` directly can produce a minimized empty attribute for true and omit the attribute for false; neither expresses an ARIA boolean state. This also prevents selectors such as `[aria-pressed="true"]` from styling the selected control.

```razor
<button type="button" aria-pressed="@(IsSelected ? "true" : "false")" @onclick="Select">
    Select view
</button>
```

Apply the same string serialization to `aria-expanded`, `aria-selected`, and `aria-hidden` where those states are needed. Verify the actual attribute value, visible selected treatment, and keyboard activation in the browser. Keep genuine HTML boolean attributes such as `disabled` bound to `bool`.

For a MudBlazor v9 `MudMenu` icon activator, set its `AriaLabel` parameter. A raw `aria-label` user attribute belongs to the outer menu container and does not name the generated trigger button. Verify the name on the focusable activator itself.

### 8.5 Keep Focus in a Parent Dialog After Confirmation

A nested confirmation or inspector cannot restore focus to an opening control that was disabled or removed by its action. Keep a temporarily busy child opener focusable with explicit `aria-disabled` and reject duplicate admission in its handler, as described below. Verify Cancel, successful completion and child Escape, then Tab and Escape in the still-open parent dialog. When native restoration has no usable destination, request focus on a stable parent heading or control after the parent renders. Use a native element's `FocusAsync` rather than another focus manager; retain and drain the admitted focus operation during retirement, and skip restoration when a newer confirmation owns focus.

Keep the opener's rendered identity stable across evidence refreshes. Native tables and grids can key rows by their item object, so replacing detached DTOs can remove an otherwise enabled opener. Inspect the installed primitive's key behavior; retain a private presentation row by exact source identity and replace its current capture without changing public DTO equality. Sorting, filtering and actions must read the newest capture. Verify focus after a refresh and after closing an inspector, including confirmations that change the row's evidence.

### 8.6 Preserve Focus While a Dialog Action Is Busy

Setting native `Disabled` on a focused action can move focus to the document body, outside the dialog's keyboard handling. This includes an action that becomes unavailable after completion, such as clearing the last retained message. For these controls, keep the action focusable, expose explicit `aria-disabled="true"`, and reject unavailable or duplicate admission in its handler. ARIA alone does not prevent activation. Other unavailable actions can use native disabling. Preserve the existing cancellation and task-drain ownership; no additional JavaScript focus manager is needed. Verify that keyboard activation keeps focus inside the dialog and that subsequent Tab and Escape still work while the operation is busy and after it completes.

### 8.7 Preserve Native Menu Activation for Form Popups

A `MudMenu` containing a settings form can have no registered `MudMenuItem` children. In MudBlazor v9.11.0, `MudMenu.HandleDismissalKeyAsync` returns immediately when `_menuItems.Count == 0`, so the primitive's Tab/Escape dismissal path does not cover that form. Custom activator content uses the native wrapper's `HandleActivatorKeydown` for Enter and Space; `TrackKeyboardInteraction` handles arrows only when menu items exist. Inspect these members in the installed source before relying on menu keyboard behavior.

Keep one semantic activator and one Tab stop. Bind the primitive's actual open state, preserve its native Enter/Space activation, and add only the missing Escape handling at the reachable activator owner and portaled form panel. Do not suppress unrelated keys or add a second focusable button inside a native activator wrapper. Close through the native menu API. `CloseAllMenusAsync` calls `FocusActivatorAsync`; verify that it restores the actual custom activator before assuming focus return is covered. If the native API cannot reach that wrapper, use a narrowly scoped, component-owned interop operation to focus the exact native activator. Own its import, admitted use, late results and teardown under the existing [lifecycle](#2-lifecycle-best-practices) rules.

Verify native browser Enter, Space, a single activator Tab stop, forward/reverse Tab through form controls, and Escape from both the activator and the form. MudBlazor v9.11.0's `_menu.scss` sets `div.mud-menu-activator` to `display: contents`; a wrapper carrying `role="button"` and `tabindex="0"` can then have a zero rectangle and fail to acquire focus. Give that actual native wrapper a scoped box-generating display such as `inline-flex`, and verify its rendered rectangle and `document.activeElement` before sending keys; a focus request completing does not prove focus moved. Check dismissal and return focus after reopening, as well as close during component retirement. Handler invocation or rendered markup alone does not establish browser key propagation or native focus behavior.

A reused contenteditable composer must restore its rendered session and draft generation atomically before accepting new input. Queued input retains its originating generation; an accepted send clears only that generation. Normalize Chromium's DIV/P line boundaries together with caret offsets after composition finishes. Render a usable native caret line for a terminal newline, excluding its filler BR from logical text. Verify Shift+Enter followed by typing, multiline restoration, and immediate conversation switching through actual browser controls.

## 9. Component Reuse Patterns

### 9.1 Generic Components

```razor
@typeparam TItem

<div class="mo-list">
    @foreach (var item in Items)
    {
        @ItemTemplate(item)
    }
</div>

@code {
    [Parameter, EditorRequired]
    public IEnumerable<TItem> Items { get; set; } = Enumerable.Empty<TItem>();

    [Parameter, EditorRequired]
    public RenderFragment<TItem> ItemTemplate { get; set; } = null!;
}
```

### 9.2 Composition Over Inheritance

```razor
<!-- Base card component -->
<MoCard>
    <Header>
        @HeaderContent
    </Header>
    <Body>
        @BodyContent
    </Body>
    <Footer>
        @FooterContent
    </Footer>
</MoCard>

<!-- Specific business card -->
<UserCard User="@user">
    <Actions>
        <MudButton>Edit</MudButton>
        <MudButton>Delete</MudButton>
    </Actions>
</UserCard>
```

## 10. MudBlazor Specific Best Practices

### 10.1 Correct Icon Property Usage

```razor
<!-- Correct: Use @ prefix -->
<MudIconButton Icon="@Icons.Material.Filled.Add" />

<!-- Wrong: Missing @ prefix -->
<MudIconButton Icon="Icons.Material.Filled.Add" />
```

### 10.2 Explicit Type Parameters for Generic Components

```razor
<!-- Correct: Explicitly specify T type -->
<MudSwitch T="bool" @bind-Value="@IsEnabled" />
<MudChip T="string" Value="@chipValue" />

<!-- Wrong: Missing type parameter -->
<MudSwitch @bind-Value="@IsEnabled" />
```

### 10.3 Use MudBlazor Theme System

```csharp
// Configure theme in layout component
<MudThemeProvider Theme="@_theme" IsDarkMode="@_isDarkMode" />

@code {
    private MudTheme _theme = new()
    {
        PaletteLight = new PaletteLight()
        {
            Primary = "#7e6fff",
            Secondary = "#ff4081"
        }
    };
}
```

### 10.4 Reset Debounced Inputs Through Their Owner

A filter reset must clear both the state container and the input's displayed text. In MudBlazor 9, a debounced `MudTextField` can suppress text reconciliation when its external `Value` differs from its internal text. Changing only the parent value can therefore restore results while leaving the old query visible. Capture the rendered field and await its public `ClearAsync()` before resetting the remaining filters:

```razor
<MudTextField T="string" @ref="_searchField"
              Value="@State.SearchText" ValueChanged="State.SetSearchText"
              DebounceInterval="250" />

@code {
    private MudTextField<string>? _searchField;

    private async Task ClearFiltersAsync()
    {
        if (_searchField is { } searchField)
        {
            await searchField.ClearAsync();
        }

        State.SetSearchText(string.Empty);
        // Reset the other filters through their existing state owner.
    }
}
```

Apply the component-lifetime checks from section 2 when the reset continuation can outlive its component. Let the MudBlazor input own its debounce cancellation; do not introduce another timer or write to its DOM value. Verify the visible query, restored results, pending typing immediately before reset, a subsequent query, and navigation with a pending input. Do not use the removed `TextUpdateSuppression` parameter as a v9 fix.

## 11. Module Development Best Practices

### 11.1 Dependency Injection Configuration

Use module options by injecting `IOptions<TModuleOption>` or `IOptionsSnapshot<TModuleOption>`:

```csharp
@inject IOptions<ModuleUIOption> Options

@code {
    protected override void OnInitialized()
    {
        var appBarName = Options.Value.UIAppBarName;
    }
}
```

### 11.2 Code Reuse Strategy

- Abstract common base classes or interfaces for similar components
- Encapsulate common UI interaction logic into reusable services
- Use MudBlazor component library for unified styles and themes

### 11.3 Performance Tips

- Split large components into smaller ones to reduce rerender scope
- Use `@key` directive to optimize list rendering
- Avoid complex calculations in templates

### 11.4 Unified Error Handling

- Use `ISnackbar` to display user-friendly error messages
- Log detailed debug information for troubleshooting
- Implement unified error handling mechanisms

```csharp
@inject ISnackbar Snackbar

private async Task HandleOperation()
{
    var result = await Service.ExecuteAsync();
    if (result.IsFailure())
    {
        Snackbar.Add(result.Message ?? "The operation failed.", Severity.Error);
        return;
    }

    Snackbar.Add("Operation successful", Severity.Success);
}
```

## 12. Service Layer Best Practices

### 12.1 Return Value Standards

- Deliberate host/UI boundaries use `Result<T>` or `Result`; internal services use direct returns and exceptions.
- Use `Result.Success(data)` for data, `Result.Success().WithMessage(message)` for a no-data success message, and `Result.Failure<T>(message, status)` for a typed failure. Forward failures with `Result.FromFailure<T>(source)`.
- Unexpected defects normally reach the host exception handler. A boundary that deliberately catches an exception logs diagnostics and returns a safe message; caller cancellation propagates. Do not expose exception text.
- `IsSuccess()` and `IsFailure()` classify the status only. Check `Data` separately and preserve the API's allowed absence semantics. See the [result contract](../../monica-development/references/result-type-guide.md).

### 12.2 Service Call Pattern

```csharp
// Inject service in Blazor page
@inject UserService UserService

@code {
    private async Task LoadDataAsync()
    {
        var result = await UserService.GetDataAsync(parameter);
        if (result.IsFailure())
        {
            // Handle error case
            Snackbar.Add(result.Message ?? "The data could not be loaded.", Severity.Error);
            return;
        }

        var data = result.Data
            ?? throw new InvalidOperationException("A successful load must include data.");
        ProcessData(data);
    }
}
```

### 12.3 Service Registration

Register services in module's `ConfigureServices` method:

```csharp
public override void ConfigureServices(IServiceCollection services)
{
    services.AddScoped<UserService>();
}
```

## 13. Data Model Management

### 13.1 Model Organization

- Place page-related data models in `UI{ModuleName}/Models/` directory
- Use strongly-typed models, avoid dynamic types
- Model naming should clearly express purpose

### 13.2 Model Naming Conventions

- Request models: `{Feature}Request`
- Response models: `{Feature}Response`
- View models: `{Feature}ViewModel`

## 14. Browser Storage Best Practices

### 14.1 Use `IBrowserStorage` for All Browser Storage

Never use raw `IJSRuntime` calls for localStorage/sessionStorage. Always inject and use `IBrowserStorage`:

```csharp
@inject IBrowserStorage BrowserStorage
```

### 14.2 Key Naming Convention

Use `{category}:{id}` format. The service auto-prefixes all keys with `mo:`:

```csharp
// Stored as "mo:table:job-instances" in localStorage
await BrowserStorage.SetAsync("table:job-instances", state);

// Stored as "mo:theme:data"
await BrowserStorage.GetAsync<ThemeData?>("theme:data", null);
```

### 14.3 Deferred Rendering Pattern

Always load persisted state in `OnAfterRenderAsync` and use a `_stateLoaded` flag to defer rendering of state-dependent UI:

```csharp
@inject IBrowserStorage BrowserStorage

@if (_stateLoaded)
{
    <MudTable @ref="_table" ServerData="LoadDataAsync"
              RowsPerPage="@_pageSize" SortLabel="@_sortBy">
        @* columns *@
    </MudTable>
}

@code {
    private bool _stateLoaded = false;
    private int _pageSize = 10;
    private string? _sortBy;

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (firstRender)
        {
            var state = await BrowserStorage.GetTableStateAsync("my-table");
            _pageSize = state.PageSize;
            _sortBy = state.SortBy;
            _stateLoaded = true;
            await InvokeAsync(StateHasChanged);
        }
    }

    private async Task<TableData<MyItem>> LoadDataAsync(TableState state, CancellationToken ct)
    {
        // Save state on every data load
        await BrowserStorage.SaveTableStateAsync("my-table", new TablePersistenceState(
            state.SortLabel, state.SortDirection == SortDirection.Descending, state.PageSize));

        // ... fetch data ...
    }
}
```

### 14.4 Service Lifetime

- `IBrowserStorage` is **Scoped** (one per Blazor circuit)
- `ThemeState` is **Scoped** (not Singleton, needs per-circuit state)

For complete API reference and patterns, see `references/browser-storage-guide.md`.

## Summary

Following these best practices helps build high-quality, maintainable, and high-performance Blazor applications. These are guiding principles that should be flexibly applied based on specific project requirements.
