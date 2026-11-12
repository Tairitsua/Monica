using MudBlazor;

namespace Monica.UI.Theming.Definitions;

/// <summary>
/// The default Monica Precision theme for vivid, information-dense operational interfaces.
/// </summary>
public sealed class DefaultTheme : ThemeDefinitionBase
{
    public override MonicaThemeKind Kind => MonicaThemeKind.Default;
    public override string DisplayName => "Default";
    public override string Description => "Vivid signal colors, technical typography, and clear operational hierarchy.";

    public override MudTheme CreateTheme() => new()
    {
        PaletteLight = CreateLightPalette(),
        PaletteDark = CreateDarkPalette(),
        LayoutProperties = new LayoutProperties
        {
            DefaultBorderRadius = "8px",
            DrawerWidthLeft = "232px",
            DrawerWidthRight = "260px",
            DrawerMiniWidthLeft = "64px",
            DrawerMiniWidthRight = "64px",
            AppbarHeight = "56px"
        },
        Shadows = MonicaShadowDefaults.Create(),
        Typography = MonicaTypographyDefaults.CreatePrecision()
    };

    private static MudMarkdownPaletteLight CreateLightPalette() => new()
    {
        CodeHighlight = MudCodeHighlightThemes.Light.GitHub,
        Primary = "#512BD4",
        PrimaryLighten = "#6A4AE8",
        PrimaryDarken = "#4220B8",
        PrimaryContrastText = "#ffffff",

        Secondary = "#3f779c",
        SecondaryLighten = "#5b96ba",
        SecondaryDarken = "#2e5c7b",
        SecondaryContrastText = "#ffffff",

        Tertiary = "#eeeafd",
        TertiaryContrastText = "#4e3cb2",

        Info = "#0891B2",
        InfoLighten = "#20A7C7",
        InfoDarken = "#08748E",
        InfoContrastText = "#ffffff",

        Success = "#0b9f73",
        SuccessLighten = "#2db88d",
        SuccessDarken = "#087d5a",
        SuccessContrastText = "#ffffff",

        Warning = "#b97806",
        WarningLighten = "#d89525",
        WarningDarken = "#925e05",
        WarningContrastText = "#ffffff",

        Error = "#d5445e",
        ErrorLighten = "#e86b82",
        ErrorDarken = "#b1324b",
        ErrorContrastText = "#ffffff",

        Dark = "#16161D",
        DarkLighten = "#2B2B35",
        DarkDarken = "#0B0B0F",
        DarkContrastText = "#F6F6F9",

        Background = "#F6F6F9",
        BackgroundGray = "#F0F0F4",
        Surface = "#ffffff",

        DrawerBackground = "#ECECF1",
        DrawerText = "#5C5D6B",
        DrawerIcon = "#63778E",

        AppbarBackground = "#ECECF1",
        AppbarText = "#16161D",

        TextPrimary = "#16161D",
        TextSecondary = "#5C5D6B",
        TextDisabled = "#878896",

        ActionDefault = "#5C5D6B",
        ActionDisabled = "#aeb7c6",
        ActionDisabledBackground = "#e8ecf2",

        Divider = "#E3E3EA",
        DividerLight = "#F0F0F4",
        LinesDefault = "#E3E3EA",
        LinesInputs = "#CFD0DA",

        TableStriped = "#f8f9fb",
        TableHover = "#f5f3ff",

        OverlayDark = "rgba(21, 27, 43, 0.58)",
        OverlayLight = "rgba(255, 255, 255, 0.78)",
        HoverOpacity = 0.055,

        GrayDefault = "#878896",
        GrayLight = "#CFD0DA",
        GrayLighter = "#F0F0F4",
        GrayDark = "#5C5D6B",
        GrayDarker = "#30303A"
    };

    private static MudMarkdownPaletteDark CreateDarkPalette() => new()
    {
        CodeHighlight = MudCodeHighlightThemes.Dark.GitHub,
        Primary = "#8D7DFF",
        PrimaryLighten = "#A597FF",
        PrimaryDarken = "#A296FF",
        PrimaryContrastText = "#0B0B0F",

        Secondary = "#a1a1aa",
        SecondaryLighten = "#d4d4d8",
        SecondaryDarken = "#71717a",
        SecondaryContrastText = "#18181b",

        Tertiary = "#27272a",
        TertiaryContrastText = "#f4f4f5",

        Info = "#1BA3C6",
        InfoLighten = "#4DB7D2",
        InfoDarken = "#1592B3",
        InfoContrastText = "#0B0B0F",

        Success = "#2FBF86",
        SuccessLighten = "#34d399",
        SuccessDarken = "#059669",
        SuccessContrastText = "#0B0B0F",

        Warning = "#E3A23B",
        WarningLighten = "#fbbf24",
        WarningDarken = "#d97706",
        WarningContrastText = "#0B0B0F",

        Error = "#F06A7D",
        ErrorLighten = "#f87171",
        ErrorDarken = "#dc2626",
        ErrorContrastText = "#0B0B0F",

        Dark = "#EDEDF2",
        DarkLighten = "#FFFFFF",
        DarkDarken = "#D5D5DF",
        DarkContrastText = "#111116",

        Background = "#111116",
        BackgroundGray = "#1F1F26",
        Surface = "#18181E",

        DrawerBackground = "#0B0B0F",
        DrawerText = "#A2A2B0",
        DrawerIcon = "#8B9EB5",

        AppbarBackground = "#0B0B0F",
        AppbarText = "#EDEDF2",

        TextPrimary = "#EDEDF2",
        TextSecondary = "#A2A2B0",
        TextDisabled = "#71717E",

        ActionDefault = "#A2A2B0",
        ActionDisabled = "#393944",
        ActionDisabledBackground = "#27272F",

        Divider = "#27272F",
        DividerLight = "#1F1F26",
        LinesDefault = "#27272F",
        LinesInputs = "#393944",

        TableStriped = "#111116",
        TableHover = "#27272F",

        OverlayDark = "rgba(0, 0, 0, 0.8)",
        OverlayLight = "rgba(24, 24, 27, 0.5)",
        HoverOpacity = 0.08,

        GrayDefault = "#71717E",
        GrayLight = "#A2A2B0",
        GrayLighter = "#EDEDF2",
        GrayDark = "#5C5D6B",
        GrayDarker = "#393944"
    };

}
