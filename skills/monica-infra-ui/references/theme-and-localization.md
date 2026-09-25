# Theme and language in the shell

Configure shell defaults inside the Web host's `builder.AddMonica(...)` callback. `DefaultTheme` is `MonicaThemeKind.Default`, `DefaultDarkMode` is false, and `ShowLanguageSwitcher` is true. Theme and dark-mode defaults apply only until a browser stores its own preference; changing a host default will not override a user's saved choice. The host still completes the UI lifecycle with `app.UseMonica()` and `app.MapMonica()`.

```csharp
builder.AddMonica(monica =>
{
    monica.AddUIShell(options =>
    {
        options.DefaultTheme = MonicaThemeKind.Default;
        options.DefaultDarkMode = false;
        options.ShowLanguageSwitcher = true;
    });
    monica.AddLocalization(options => options.DefaultCulture = "en-US");
});
```

Localization defaults to `zh-CN` and supports `zh-CN` and `en-US`. `ModuleLocalizationOption.SupportedCultures` and `DefaultCulture` define the host profile; Web request culture persists through the localization cookie. The switcher affects whether users see a language control, not which resources are registered. Built-in UI modules contribute their own resource markers through a Localization dependency. For a custom UI module, contribute its marker in `Describe(...)` and use `ConfigureNavigation(...)` with `RegisterLocalizedPage<TPage,TResource>(...)`; keep route and category IDs stable while translating display labels. See [shell and access](shell-and-access.md) for when that page becomes routable and protected, and `$monica-ui-localization` for resource layout and validation.

For first-party styling, prefer `--mud-palette-*` variables and the small `--mo-color-*` contract in `Monica.UI/wwwroot/css/mo-theme-main.css`. Component construction, MudBlazor v9 APIs, CSS isolation, and browser checks belong to `$monica-ui-development`.

Source: `Monica.UI/Modules/ModuleShellUI.cs`, `Monica.Core/Modules/ModuleLocalization.cs`, `Monica.UI/Shell/Support/INavigationRegistryBuilder.cs`, and `tests/Test.Monica.UI/UI/ThemeDisplayTextProviderTests.cs`.
