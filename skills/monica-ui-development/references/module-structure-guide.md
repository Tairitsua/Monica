# UI Module Structure Guide

This guide defines the architecture patterns, folder conventions, and component organization for UI modules in Monica.

> **Canonical architecture reference**: `monica-architecture` skill (`SKILL.md`)
> When this file conflicts with the architecture skill, the architecture skill takes precedence.

## Core Rule

UI modules are **pure presentation layers**. They:

- Inject Facades from the infrastructure module directly (Facades return `Result<T>`)
- Do NOT have their own service layer for data access
- Focus on components, page composition, state management, and localization
- Maximize reuse of Models from the infrastructure module's public `Models/` folder
- Implement `IUIModule` as an explicit presentation capability
- Usually remain non-web `MonicaModule<TOptions>` strategies without a web marker

Every UI module implements `IUIModule`; never infer UI identity from a class or project name. Implement `IWebModule` separately only when the UI module itself configures ASP.NET Core middleware or endpoints. A UI module that only registers pages, dialogs, shell navigation, or localized components stays non-web. Add `IWebHostRequiredModule` only for an intrinsic host requirement; an opt-in web feature calls `registration.RequireWebHost(reason)`.

## UI Module Architecture Patterns

### Pattern A: Mixed Module

**When to use**: Infrastructure and UI are tightly coupled, UI is lightweight, no need for separate packaging.

A single project contains both infrastructure and UI code, with two separate module classes.

```
Monica.{Name}/
├── Modules/
│   ├── Module{Name}.cs               # Infrastructure module
│   └── Module{Name}UI.cs             # UI module
│
├── Abstractions/                      # Infrastructure layers
│   └── Internal/
├── Models/
│   └── Internal/
├── Facades/
│   └── {Name}Facade.cs
├── Services/
│   └── Support/
├── Providers/
│   └── {ProviderName}/
│
├── Pages/                             # UI layers
│   ├── UI{Name}Page.razor
│   └── UI{Name}Page.razor.css
├── UI{Name}/
│   ├── Components/
│   ├── Dialogs/
│   ├── Models/                        # View-only models (minimize)
│   ├── State/
│   └── Support/
│
├── Localization/
└── wwwroot/
```

Key characteristics:
- Two module classes: `Module{Name}` (infrastructure) + `Module{Name}UI` (UI)
- UI code lives in `UI{Name}/` subfolder, strictly separated from infrastructure
- UI components inject `{Name}Facade` directly — no intermediate UI service
- Being in the same assembly does NOT relax layering rules

**When to upgrade**: If UI pages keep growing, multiple UI sub-features emerge, or separate deployment is needed — split into `Monica.{Name}.UI`.

### Pattern B: Standalone UI Module

**When to use**: UI is complex enough for its own project, or the infrastructure module is in a separate package.

```
Monica.{Name}.UI/
├── Modules/
│   └── Module{Name}UI.cs
│
├── Pages/
│   ├── UI{Name}Page.razor
│   └── UI{Name}Page.razor.css
│
├── UI{Name}/
│   ├── Components/
│   ├── Dialogs/
│   ├── Models/                        # View-only models (minimize)
│   ├── State/
│   └── Support/
│
├── Localization/
│   └── {Name}Resource/
│       ├── zh-CN.json
│       └── en-US.json
│
└── wwwroot/
```

Key characteristics:
- Single UI module class: `Module{Name}UI`
- Uses `UI{Name}/` feature directory (NOT flat root-level Components/Services/Models)
- Injects `{Name}Facade` from the infrastructure module directly
- References infrastructure module via project/package dependency

### Pattern C: Composite UI Module

**When to use**: One UI project hosts multiple UI sub-modules.

```
Monica.{Family}.UI/
├── Modules/
│   ├── Module{FeatureA}UI.cs
│   ├── Module{FeatureB}UI.cs
│   └── Module{Family}UI.cs           # (optional, aggregator)
│
├── Pages/
│   ├── UI{FeatureA}Page.razor
│   ├── UI{FeatureB}Page.razor
│   └── ...
│
├── UI{FeatureA}/
│   ├── Components/
│   ├── Dialogs/
│   ├── Models/
│   ├── State/
│   └── Support/
│
├── UI{FeatureB}/
│   ├── Components/
│   ├── Models/
│   ├── State/
│   └── Support/
│
├── Localization/
└── wwwroot/
```

Key characteristics:
- One parent module + N sub-modules, each with own `UI{Name}/` folder
- Each sub-module injects the corresponding infrastructure Facade

### Pattern Selection Guide

| Criteria | Mixed (A) | Standalone (B) | Composite (C) |
|----------|-----------|----------------|---------------|
| Infrastructure + UI coupling | Tight | Loose | N/A |
| UI complexity | Low | High | Low per module |
| Number of UI modules | 1 | 1 | Multiple |
| Separate deployment | No | Yes | No |

## UI Module Class Implementation

```csharp
public class Module{Name}UI : MonicaModule<Module{Name}UIOption>, IUIModule
{
    public override void Describe(ModuleDescriptor module)
    {
        module.Require<Module{Name}, Module{Name}Option>();
        module.Require<ModuleLocalization, ModuleLocalizationOption>(
            static option => option.AddResource<{Name}Resource>());
        module.Require<ModuleShellUI, ModuleShellUIOption>(
            static option => option.ConfigureNavigation(registry =>
                registry.RegisterLocalizedPage<UI{Name}Page, {Name}Resource>(
                    UI{Name}Page.{NAME}_URL,
                    "Pages:{Name}:Title",
                    Icons.Material.Filled.Settings,
                    BuiltInNavigationCategoryIds.Infrastructure,
                    addToNav: true,
                    navOrder: 100)));
    }

    public override void ConfigureServices(ModuleContext<Module{Name}UIOption> context)
    {
        context.Services.AddScoped<{Name}PageState>();
    }
}

public static class Module{Name}UIBuilderExtensions
{
    extension(IMonicaBuilder builder)
    {
        public ModuleRegistration<Module{Name}UI, Module{Name}UIOption> Add{Name}UI()
        {
            return builder.AddModule<Module{Name}UI, Module{Name}UIOption>();
        }
    }
}

public class Module{Name}UIOption : ModuleOptions<Module{Name}UI>;
```

`IUIModule` is always present on a UI module. If it also maps middleware or endpoints, implement `IWebModule`; generic hosts omit those contributions by default. Add `IWebHostRequiredModule` only when the module type intrinsically requires the web host, and use `registration.RequireWebHost(reason)` for an opt-in feature requirement.

## UI Folder Responsibilities

| Folder | What belongs here | What does NOT belong here |
|--------|-------------------|--------------------------|
| `Pages/` | Route pages, page-level composition, lifecycle entry | Business orchestration, SDK calls |
| `UI{Name}/Components/` | Reusable Blazor components, partial UI composition | Page routing, business entry points |
| `UI{Name}/Dialogs/` | Dialog components | Non-dialog components |
| `UI{Name}/Models/` | ViewModels, DialogModels, display-only models | Infrastructure models (reuse from Facade Models) |
| `UI{Name}/State/` | Browser state, page state, session state, table state | Business orchestration |
| `UI{Name}/Support/` | Resolvers, formatters, coordinators, storage adapters | Data access (use Facade instead) |
| `Localization/` | Resource markers and localization JSON files | Business logic |

## UI Model Reuse Rule

UI modules must maximize reuse of Models from the infrastructure module's public `Models/` folder.

Only create UI-specific models when:
- The view requires a shape that genuinely differs from any existing model
- The model is purely presentational (e.g., `DialogModel`, `ViewModel` with UI-only state like `IsExpanded`, `IsSelected`)

Do NOT duplicate infrastructure models in the UI module.

## State and Support Classification

Not everything in the UI layer should be lumped together. Use precise folder placement:

| Type | Folder | Examples |
|------|--------|---------|
| Page/session state | `State/` | `ChatSessionStateManager`, `ChatSessionStorage` |
| Browser persistence | `State/` | `RAGBrowserState`, `TableStateStorage` |
| Orchestration helpers | `Support/` | `RAGBatchIndexCoordinator`, `RAGChunkViewCoordinator` |
| Resolution/lookup | `Support/` | `RAGMarkdownDocumentResolver` |
| Format conversion | `Support/` | `ChunkHighlightFormatter` |

## Naming Conventions

| Variable | Format | Example |
|----------|--------|---------|
| UI module class | `Module{Name}UI` | `ModuleRAGUI` |
| UI folder | `UI{Name}/` | `UIRAG/`, `UIChat/` |
| Page component | `UI{Name}Page` | `UIRAGManagePage` |
| Route URL | `/{name}-{action}` (kebab-case) | `/rag-manage`, `/ai-chat` |
| State class | `{Feature}State` / `{Feature}StateManager` | `ChatSessionStateManager` |

## Data Model Naming

| Type | Naming Pattern |
|------|---------------|
| Request models | `{Feature}Request` |
| Response models | `{Feature}Response` |
| View models | `{Feature}ViewModel` |
| DTOs | `Dto{Feature}` |

Prioritize reusing infrastructure module models. Composition over redefinition.

## Page File Pattern

```razor
@attribute [Route({NAME}_URL)]

@code {
    public const string {NAME}_URL = "/module-name-page";
}
```

### Page Dependency Injection

```razor
@using Monica.{Name}.{Feature}.Facades
@using Monica.{Name}.UI.UI{Name}.Components
@inject {Name}Facade {Name}Facade
@inject {Name}PageState PageState
```

Note: Pages inject the infrastructure Facade directly — no UI service intermediary.

## Example References

### UIRAG (Composite UI — Monica.AI.UI)

```
Modules/ModuleRAGUI.cs
Pages/UIAIRAGManagePage.razor
UIRAG/
├── Components/
│   ├── KnowledgeBasePanel.razor
│   ├── IndexingPanel.razor
│   └── SearchResultCard.razor
├── Dialogs/
│   ├── CreateKnowledgeBaseDialog.razor
│   └── DocumentSelectionDialog.razor
├── Models/                              # Only if genuinely needed
├── State/
│   └── RAGPageState.cs
└── Support/
    ├── RAGBatchIndexCoordinator.cs
    └── RAGChunkViewCoordinator.cs
```

### UIDataChannel (Mixed Module)

```
Modules/ModuleDataChannelUI.cs
Pages/UIDataChannelPage.razor
UIDataChannel/
├── Components/
│   ├── ChannelStatusCard.razor
│   └── MetadataDisplay.razor
├── Dialogs/
│   ├── ExceptionDetailsDialog.razor
│   └── MessageDebuggerDialog.razor
├── Models/
│   ├── ChannelStatusInfo.cs
│   └── DtoChannelInfo.cs
└── State/
```

### UIConfiguration (Standalone UI)

```
Monica.Configuration.UI/
├── Modules/ModuleConfigurationUI.cs
├── Pages/UIConfigurationDashboardPage.razor
├── UIConfiguration/
│   ├── Components/
│   │   ├── ConfigurationEditor.razor
│   │   ├── ConfigurationExplorer.razor
│   │   └── ConfigurationList.razor
│   ├── Dialogs/
│   │   ├── SaveConfirmationDialog.razor
│   │   └── RollbackConfirmationDialog.razor
│   ├── Models/
│   │   └── ConfigurationStateManager.cs
│   └── State/
└── Localization/
```
