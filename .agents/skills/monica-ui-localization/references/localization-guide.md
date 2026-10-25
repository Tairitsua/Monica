# Localization Guide (i18n)

Comprehensive guidance for implementing multilingual support in Monica UI components.

## Architecture Overview

Monica uses a decentralized localization pattern where each UI module manages its own localization resources independently. This improves modularity, reduces coupling, and makes modules truly self-contained.

## Decentralized Pattern

### Overview

Each `*.UI` project has its own localization infrastructure:
- Marker class in `Localization/{ModuleName}Resource.cs`
- JSON files in `Localization/{ModuleName}Resource/zh-CN.json` and `en-US.json`
- Embedded resources configured in `.csproj`
- Auto-discovered by `MoStringLocalizerFactory`

### Resource Registration and Loading

Reusable Monica modules register their resource marker intrinsically from the owning module's `Describe` method:

```csharp
module.Require<ModuleLocalization, ModuleLocalizationOption>(
    static option => option.AddResource<StateStoreResource>());
```

Host/business resource types may also be discovered through the host's configured business-type scan. Independent packages must not rely on an assembly-name prefix or on the host scanning their assembly.

For each registered resource type, Monica loads embedded JSON files using `{Namespace}.{ResourceName}.{Culture}.json`.

**Example:** For type `StateStoreResource` in namespace `Monica.StateStore.UI.Localization`, it loads:
- `Monica.StateStore.UI.Localization.StateStoreResource.zh-CN.json`
- `Monica.StateStore.UI.Localization.StateStoreResource.en-US.json`

Module packages must register the marker type through `AddResource<TResource>()`, create the JSON files, and configure embedding.

### Implementation Steps

#### Step 1: Create Marker Class

Create a marker class in your UI module:

```csharp
// Monica.StateStore.UI/Localization/StateStoreResource.cs
namespace Monica.StateStore.UI.Localization;

/// <summary>
/// Marker class for StateStore UI localization resources
/// </summary>
public class StateStoreResource
{
}
```

**Naming convention:** `{ModuleName}Resource` (e.g., `StateStoreResource`, `ConfigurationResource`)

#### Step 2: Create JSON Files

Create JSON files in `Localization/{ResourceName}/` folder:

```
Monica.StateStore.UI/
└── Localization/
    └── StateStoreResource/
        ├── zh-CN.json
        └── en-US.json
```

**JSON structure:**

```json
{
  "Dashboard": {
    "PageTitle": "State Store Management",
    "Title": "State Store Dashboard",
    "Tabs": {
      "ProviderOverview": "Provider Overview",
      "KeyExplorer": "Key Explorer"
    }
  },
  "KeyExplorer": {
    "Labels": {
      "KeyPattern": "Key Pattern",
      "KeyName": "Key Name"
    },
    "Actions": {
      "Scan": "Scan Keys",
      "Refresh": "Refresh"
    }
  }
}
```

**Key naming:** Use hierarchical structure without module prefix:
- ✅ `"Dashboard:PageTitle"` (correct)
- ❌ `"StateStore:Dashboard:PageTitle"` (wrong - no module prefix needed)

#### Step 3: Configure Project File

Update your `.csproj` to embed JSON files:

```xml
<Project Sdk="Microsoft.NET.Sdk.Razor">
  <!-- ... other configuration ... -->

  <ItemGroup>
    <EmbeddedResource Include="Localization\**\*.json" />
  </ItemGroup>
</Project>
```

This embeds all JSON files in the `Localization` folder as embedded resources.

#### Step 4: Update _Imports.razor

Add the localization namespace to your module's `_Imports.razor`:

```razor
@using Microsoft.Extensions.Localization
@using Monica.StateStore.UI.Localization
```

#### Step 5: Use in Components

Inject the localizer in your components:

```razor
@inject IStringLocalizer<StateStoreResource> L

<MudText Typo="Typo.h4">@L["Dashboard:PageTitle"]</MudText>
<MudButton OnClick="ScanKeys">@L["KeyExplorer:Actions:Scan"]</MudButton>
<MudTextField Label="@L["KeyExplorer:Labels:KeyPattern"]" />
```

#### Step 6: Register localized navigation explicitly

Every localized page declares its owning resource type from the UI module's `Describe` method. Shared shell categories use stable built-in IDs:

```csharp
module.Require<ModuleLocalization, ModuleLocalizationOption>(
    static option => option.AddResource<StateStoreResource>());

module.Require<ModuleShellUI, ModuleShellUIOption>(
    static option => option.ConfigureNavigation(registry =>
        registry.RegisterLocalizedPage<UIStateStorePage, StateStoreResource>(
            UIStateStorePage.PAGE_URL,
            "Navigation:Title",
            Icons.Material.Filled.Storage,
            BuiltInNavigationCategoryIds.Debug,
            addToNav: true,
            navOrder: 20)));
```

A package-owned category registers its stable identity and localized label once, then passes the returned ID to pages:

```csharp
module.Require<ModuleShellUI, ModuleShellUIOption>(
    static option => option.ConfigureNavigation(registry =>
    {
        var categoryId = registry.RegisterLocalizedCategory<ExampleResource>(
            "Contoso.Monica.Example",
            "Navigation:Category",
            order: 450,
            descriptionKey: "Navigation:CategoryDescription");

        registry.RegisterLocalizedPage<UIExamplePage, ExampleResource>(
            UIExamplePage.PAGE_URL,
            "Navigation:Title",
            Icons.Material.Filled.Extension,
            categoryId,
            addToNav: true,
            navOrder: 10,
            descriptionKey: "Navigation:PageDescription");
    }));
```

The category ID is never translated and is compared case-insensitively. Category order is explicit. The registry freezes on its first read, so all contributions must occur during application startup.

### Key Naming Conventions

**Hierarchical structure** matching JSON organization:

```
ComponentName (top-level)
├── Labels (UI labels)
├── Placeholders (input placeholders)
├── Actions (button text)
├── Messages (user feedback)
├── Dialogs (dialog titles/content)
└── Tabs (tab labels)
```

**Examples:**

```razor
@L["Dashboard:PageTitle"]
@L["KeyExplorer:Labels:KeyPattern"]
@L["KeyExplorer:Actions:Scan"]
@L["KeyExplorer:Messages:FoundKeys", count]
@L["KeyEditor:Dialogs:ConfirmDelete"]
```

**Rules:**
- PascalCase for all parts
- No module prefix (e.g., no "StateStore:" prefix)
- Descriptive names (e.g., `InitializationComplete` not `InitDone`)
- Consistent terminology across the module

### Parameterized Strings

**JSON:**
```json
{
  "KeyExplorer": {
    "Messages": {
      "FoundKeys": "Found {0} keys",
      "KeyRange": "Showing keys {0} to {1} of {2}"
    }
  }
}
```

**Razor:**
```razor
@L["KeyExplorer:Messages:FoundKeys", count]
@L["KeyExplorer:Messages:KeyRange", start, end, total]
```

### Complete Example

**File structure:**
```
Monica.StateStore.UI/
├── Localization/
│   ├── StateStoreResource.cs
│   └── StateStoreResource/
│       ├── zh-CN.json
│       └── en-US.json
├── _Imports.razor
├── Monica.StateStore.UI.csproj
└── UIStateStore/
    └── Components/
        └── StateStoreKeyExplorer.razor
```

**StateStoreResource.cs:**
```csharp
namespace Monica.StateStore.UI.Localization;

public class StateStoreResource { }
```

**zh-CN.json:**
```json
{
  "KeyExplorer": {
    "Labels": {
      "KeyPattern": "键模式"
    },
    "Actions": {
      "Scan": "扫描键"
    },
    "Messages": {
      "FoundKeys": "找到 {0} 个键"
    }
  }
}
```

**en-US.json:**
```json
{
  "KeyExplorer": {
    "Labels": {
      "KeyPattern": "Key Pattern"
    },
    "Actions": {
      "Scan": "Scan Keys"
    },
    "Messages": {
      "FoundKeys": "Found {0} keys"
    }
  }
}
```

**_Imports.razor:**
```razor
@using Microsoft.Extensions.Localization
@using Monica.StateStore.UI.Localization
```

**StateStoreKeyExplorer.razor:**
```razor
@inject IStringLocalizer<StateStoreResource> L

<MudTextField Label="@L["KeyExplorer:Labels:KeyPattern"]" />
<MudButton OnClick="ScanKeys">@L["KeyExplorer:Actions:Scan"]</MudButton>

@code {
    private async Task ScanKeys()
    {
        var keys = await ScanAsync();
        Snackbar.Add(L["KeyExplorer:Messages:FoundKeys", keys.Count], Severity.Success);
    }
}
```

## Validation Workflow

### Running the Script

**Basic validation:**
```bash
python scripts/validate_localization.py
```

**Summary only:**
```bash
python scripts/validate_localization.py --summary
```

**Strict mode** (treat unused keys as errors):
```bash
python scripts/validate_localization.py --strict
```

**JSON output** (for CI/CD):
```bash
python scripts/validate_localization.py --json
```

### Validation Checks

Discovery excludes exact generated or archive directory names below the requested root, including `.evidence`, `.tmp`, and `.pending`. Preserved baseline copies inside these scratch roots do not participate in resource resolution.
An exact `evidence` directory beneath `.ui-design` is also an archive; its preserved projects, source files,
and resources cannot define live resource identities or satisfy current key usages. Live `.ui-design` bridge
projects remain in scope. Names such as `.evidence-copy` and `evidence-copy`, and production `Evidence`
directories outside `.ui-design`, are not excluded by this archive rule. Directory-name comparisons ignore case.

Exclusions apply only below the requested root, not to its parent directories. To validate an archived project
itself, explicitly pass that project as `--root`; the root must contain a `.csproj`, `.slnx`, or `.sln` file.
The same rules apply to directories below that explicit root. For example, from the repository root:

```bash
python skills/monica-ui-localization/scripts/validate_localization.py --root .ui-design/review/evidence/bridge-preparation/ReviewHost --strict
```

1. **Missing keys** (ERROR): Keys used in Razor or C# but not defined in JSON
2. **Unused keys** (WARNING): Keys defined in JSON but never used
3. **Language sync** (ERROR): Keys in one language but not another
4. **Navigation registration keys** (ERROR): Page/category labels and optional `descriptionKey` values must resolve from their explicitly declared module resource; named and positional description arguments are checked
5. **Navigation resource registration** (ERROR): Every page/category resource must be registered through `AddResource<TResource>()`
6. **Legacy navigation API** (ERROR): `RegisterLocalizedComponent` and an implicit resource type are rejected

### Example Output

```
=== Localization Validation Report ===

[ERROR] Missing Keys (used but not defined):
  ✗ UserManagement:Title
    Used in: Monica.UI/Components/Pages/UserManagement.razor:15

[WARNING] Unused Keys (defined but not used):
  ⚠ Common:Import
    Defined in: zh-CN.json, en-US.json

[ERROR] Language Sync Issues:
  ✗ ModuleSystem:NewFeature:Title
    Present in: zh-CN.json
    Missing in: en-US.json

Summary:
  Total keys: 245
  Missing: 1
  Unused: 1
  Sync issues: 1
  Status: FAILED
```

### Fixing Issues

**Missing keys:** Add to both JSON files
**Unused keys:** Remove from both JSON files (verify not used dynamically)
**Sync issues:** Add missing translations to ensure both files match

### CI/CD Integration

```yaml
- name: Validate Localization
  run: |
    python <path-to-monica-ui-localization-skill>/scripts/validate_localization.py --strict --json
```

## Troubleshooting

### Key Not Found at Runtime

**Symptom:** Text displays as key path instead of translated text

**Solution:**
1. Verify key exists in JSON file
2. Check key path matches JSON structure exactly
3. Ensure JSON file is valid (no syntax errors)
4. Rebuild project

### Navigation/AppBar Key Shows Raw Text

**Symptom:** The navigation or AppBar shows a raw key such as `Pages:GitRepositories:Title`

**Cause:** The page or category key is missing from the explicitly declared module resource, or the module did not register that resource. Monica no longer has an implicit `UIRegistryResource` fallback.

**Solution:**
1. Register the page with `RegisterLocalizedPage<TPage, TResource>` and keep its title key in both language files for `TResource`.
2. Use a `BuiltInNavigationCategoryIds` value for shell taxonomy, or register a module-owned category once with `RegisterLocalizedCategory<TResource>(stableId, displayNameKey, order)`.
3. Keep a module-owned category label key in the same `TResource` language files and pass the returned category ID to every page in that category.
4. Ensure the module declares `AddResource<TResource>()` through its localization dependency.
5. Re-run `python scripts/validate_localization.py --strict`.

### Parameterized String Shows {0}

**Symptom:** Parameters show as `{0}`, `{1}` instead of values

**Solution:**
```razor
<!-- Wrong -->
@L["KeyExplorer:Messages:FoundKeys"]

<!-- Correct -->
@L["KeyExplorer:Messages:FoundKeys", count]
```

### Language Sync Issues

**Symptom:** Validation script reports keys missing in one language

**Solution:**
1. Run validation to identify missing keys
2. Add missing keys to the other language file
3. Ensure both files have identical structure
4. Re-run validation

### JSON Syntax Error

**Symptom:** Localization not working at all

**Solution:**
1. Validate JSON files using a JSON validator
2. Check for:
   - Missing commas
   - Trailing commas
   - Unescaped quotes
   - Mismatched brackets

## Best Practices

1. **Always use localization** - Never hardcode user-facing text
2. **Inject once** - Add `@inject IStringLocalizer<{YourModule}Resource> L` at component top
3. **Descriptive keys** - Use `KeyExplorer:Actions:Scan` not `KE:AS`
4. **Keep languages in sync** - Always update both JSON files
5. **Validate before commit** - Run validation script before committing
6. **Test both languages** - Verify text displays correctly in all languages
7. **Use parameters** - For dynamic text, use parameterized strings
8. **Follow naming conventions** - PascalCase and hierarchical structure
