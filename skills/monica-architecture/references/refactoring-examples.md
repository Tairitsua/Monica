# Refactoring Examples & Prohibited Practices

## Infrastructure Refactoring: God Service → Layered Structure

When a single service file grows to 800+ lines handling facade logic, business rules, provider coordination, and state recovery, decompose it:

```
Monica.{Name}/{Feature}/
├── Abstractions/
│   ├── I{Extension}.cs                # Public extension points
│   └── Internal/
│       ├── I{Store}.cs                # Internal store contracts
│       └── I{SourceStore}.cs
├── Models/
│   ├── {Entity}.cs                    # Public
│   └── Internal/
│       ├── {VectorRecord}.cs
│       └── {QueueItem}.cs
├── Facades/
│   └── {Feature}Facade.cs            # ~150 lines, Result<T>
├── Services/
│   ├── {Capability}Service.cs         # One clear capability each
│   └── Support/
│       ├── {Feature}Registry.cs
│       └── {Feature}Coordinator.cs
├── Providers/
│   ├── Stores/
│   │   └── File{Store}.cs
│   └── {Strategy}/
│       └── {Strategy}Provider.cs
```

Key decisions:
- Single 800-line service → multiple focused services + support services
- Internal abstractions for store contracts (not needed by external modules)
- Public abstractions for extension points
- Providers grouped by capability
- Facade stays thin (~150 lines), delegates to services

## Prohibited Practices

- A single service acting as facade + business logic + provider coordination + state recovery
- Internal services depending on Facades
- UI components depending on Providers directly
- Inventing custom folder names outside the standard layer names
- Flat root-level `Components/Services/Models` in UI modules
- Duplicating infrastructure Models in UI modules
- Complex business logic in Minimal API handlers or Razor pages
- Using `Helpers/`, `Tools/`, `Common/`, `Misc/` as folder names
- UI module having its own service layer wrapping Facades (inject Facades directly)
- Page files exceeding 350 lines (extract to State/ + Components/)
- `CancellationTokenSource`, `Interlocked`, or polling loops in Razor page `@code` blocks

## UI Anti-Pattern: God Page

See `examples/anti-pattern-god-page.razor` for a self-contained fictional example demonstrating all common violations. The example shows:

1. God Page (1,500+ lines doing everything)
2. 20+ private fields — state machine hiding in a page
3. Concurrency primitives (`Interlocked`, `CancellationTokenSource`) in `@code`
4. 6+ `Can*()` guard methods in page code
5. Flat root-level `Services/` in UI module with unnecessary Facade wrappers

### Correct Structure After Decomposition

```
Monica.{Name}.UI/
├── Pages/
│   └── UI{Name}ManagePage.razor       # ≤ 200 lines — thin composition shell
├── UI{Name}/
│   ├── Components/
│   │   ├── {Name}ListPanel.razor
│   │   ├── {Name}DetailPanel.razor
│   │   └── {Feature}Section.razor
│   ├── Dialogs/
│   │   └── Create{Name}Dialog.razor
│   ├── State/
│   │   ├── {Name}ManagePageState.cs   # All fields, selection, loading
│   │   └── {Name}PollingState.cs      # CTS, polling loop, Interlocked
│   └── Support/
│       ├── {Feature}Coordinator.cs
│       └── {Feature}Resolver.cs
├── Localization/
└── wwwroot/
```

Key changes:
- Page shrinks to ~200 lines (composition only)
- `*UIService` wrappers eliminated — components inject Facades directly
- 20+ page fields → dedicated State classes
- Polling/concurrency → dedicated PollingState (owns CTS and Interlocked)
- `Can*()` guards → computed properties on state classes
- Flat `Services/` → `UI{Name}/State/` + `UI{Name}/Support/`
