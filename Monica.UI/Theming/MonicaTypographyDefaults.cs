using MudBlazor;

namespace Monica.UI.Theming;

/// <summary>
/// Provides Monica's offline-safe baseline typography for themes that do not define a type system.
/// </summary>
internal static class MonicaTypographyDefaults
{
    private static readonly string[] BodyFontFamily =
    [
        "MoDefaultBody", "IBM Plex Sans", "Noto Sans SC", "Microsoft YaHei UI", "PingFang SC", "sans-serif"
    ];

    private static readonly string[] DisplayFontFamily =
    [
        "MoDefaultBody", "IBM Plex Sans", "Noto Sans SC", "Microsoft YaHei UI", "PingFang SC", "sans-serif"
    ];

    private static readonly string[] PrecisionDisplayFontFamily =
    [
        "MoDefaultDisplay", "Oxanium", "MoDefaultBody", "IBM Plex Sans", "Noto Sans SC", "Microsoft YaHei UI", "PingFang SC", "sans-serif"
    ];

    private static readonly string[] GroveDisplayFontFamily =
    [
        "MoGroveDisplay", "MoGroveSerifSC", "Georgia", "serif"
    ];

    internal static Typography CreatePrecision()
    {
        string[] precisionFamily = ["MoPrecision", "Microsoft YaHei UI", "Microsoft YaHei", "PingFang SC", "Noto Sans SC", "system-ui", "sans-serif"];
        return new Typography
        {
            Default = new DefaultTypography
            {
                FontFamily = [.. precisionFamily],
                FontSize = "0.9375rem",
                FontWeight = "400",
                LineHeight = "1.5",
                LetterSpacing = "0"
            },
            H1 = new H1Typography { FontFamily = [.. precisionFamily], FontSize = "2.5rem", FontWeight = "650", LineHeight = "1.08", LetterSpacing = "-0.035em" },
            H2 = new H2Typography { FontFamily = [.. precisionFamily], FontSize = "2rem", FontWeight = "650", LineHeight = "1.12", LetterSpacing = "-0.03em" },
            H3 = new H3Typography { FontFamily = [.. precisionFamily], FontSize = "1.625rem", FontWeight = "650", LineHeight = "1.18", LetterSpacing = "-0.025em" },
            H4 = new H4Typography { FontFamily = [.. precisionFamily], FontSize = "1.375rem", FontWeight = "650", LineHeight = "1.25", LetterSpacing = "-0.02em" },
            H5 = new H5Typography { FontFamily = [.. precisionFamily], FontSize = "1.125rem", FontWeight = "650", LineHeight = "1.35", LetterSpacing = "-0.012em" },
            H6 = new H6Typography { FontFamily = [.. precisionFamily], FontSize = "1rem", FontWeight = "650", LineHeight = "1.4", LetterSpacing = "-0.006em" },
            Subtitle1 = new Subtitle1Typography { FontFamily = [.. precisionFamily], FontSize = "1rem", FontWeight = "600", LineHeight = "1.5", LetterSpacing = "0" },
            Subtitle2 = new Subtitle2Typography { FontFamily = [.. precisionFamily], FontSize = "0.875rem", FontWeight = "600", LineHeight = "1.4", LetterSpacing = "0" },
            Body1 = new Body1Typography { FontFamily = [.. precisionFamily], FontSize = "0.95rem", FontWeight = "400", LineHeight = "1.55", LetterSpacing = "0" },
            Body2 = new Body2Typography { FontFamily = [.. precisionFamily], FontSize = "0.875rem", FontWeight = "400", LineHeight = "1.5", LetterSpacing = "0" },
            Button = new ButtonTypography { FontFamily = [.. precisionFamily], FontSize = "0.875rem", FontWeight = "600", LineHeight = "1.25", LetterSpacing = "0", TextTransform = "none" },
            Caption = new CaptionTypography { FontFamily = [.. precisionFamily], FontSize = "0.75rem", FontWeight = "400", LineHeight = "1.35", LetterSpacing = "0.01em" },
            Overline = new OverlineTypography { FontFamily = [.. precisionFamily], FontSize = "0.6875rem", FontWeight = "600", LineHeight = "1.25", LetterSpacing = "0.09em", TextTransform = "uppercase" }
        };
    }

    internal static Typography CreateGrove()
    {
        return new Typography
        {
            Default = new DefaultTypography
            {
                FontFamily = CloneBodyFamily(),
                FontSize = "0.875rem",
                FontWeight = "400",
                LineHeight = "1.5",
                LetterSpacing = "0"
            },
            H1 = new H1Typography { FontFamily = [.. GroveDisplayFontFamily], FontSize = "2.25rem", FontWeight = "400", LineHeight = "1.22", LetterSpacing = "-0.025em" },
            H2 = new H2Typography { FontFamily = [.. GroveDisplayFontFamily], FontSize = "2rem", FontWeight = "400", LineHeight = "1.25", LetterSpacing = "-0.02em" },
            H3 = new H3Typography { FontFamily = [.. GroveDisplayFontFamily], FontSize = "1.75rem", FontWeight = "400", LineHeight = "1.3", LetterSpacing = "-0.015em" },
            H4 = new H4Typography { FontFamily = [.. GroveDisplayFontFamily], FontSize = "1.5rem", FontWeight = "400", LineHeight = "1.35", LetterSpacing = "-0.01em" },
            H5 = new H5Typography { FontFamily = CloneBodyFamily(), FontSize = "1.125rem", FontWeight = "600", LineHeight = "1.35", LetterSpacing = "-0.01em" },
            H6 = new H6Typography { FontFamily = CloneBodyFamily(), FontSize = "1rem", FontWeight = "600", LineHeight = "1.4", LetterSpacing = "-0.005em" },
            Subtitle1 = new Subtitle1Typography { FontFamily = CloneDisplayFamily(), FontSize = "1rem", FontWeight = "600", LineHeight = "1.5", LetterSpacing = "0" },
            Subtitle2 = new Subtitle2Typography { FontFamily = CloneBodyFamily(), FontSize = "0.875rem", FontWeight = "600", LineHeight = "1.4", LetterSpacing = "0" },
            Body1 = new Body1Typography { FontFamily = CloneBodyFamily(), FontSize = "0.875rem", FontWeight = "400", LineHeight = "1.55", LetterSpacing = "0" },
            Body2 = new Body2Typography { FontFamily = CloneBodyFamily(), FontSize = "0.875rem", FontWeight = "400", LineHeight = "1.5", LetterSpacing = "0" },
            Button = new ButtonTypography { FontFamily = CloneBodyFamily(), FontSize = "0.875rem", FontWeight = "600", LineHeight = "1.25", LetterSpacing = "0", TextTransform = "none" },
            Caption = new CaptionTypography { FontFamily = CloneBodyFamily(), FontSize = "0.75rem", FontWeight = "400", LineHeight = "1.35", LetterSpacing = "0.01em" },
            Overline = new OverlineTypography { FontFamily = CloneDisplayFamily(), FontSize = "0.6875rem", FontWeight = "600", LineHeight = "1.25", LetterSpacing = "0.09em", TextTransform = "uppercase" }
        };
    }

    internal static Typography CreateFallback()
    {
        var typography = new Typography();

        typography.Default.FontFamily = CloneBodyFamily();
        typography.H1.FontFamily = ClonePrecisionDisplayFamily();
        typography.H2.FontFamily = ClonePrecisionDisplayFamily();
        typography.H3.FontFamily = ClonePrecisionDisplayFamily();
        typography.H4.FontFamily = ClonePrecisionDisplayFamily();
        typography.H5.FontFamily = ClonePrecisionDisplayFamily();
        typography.H6.FontFamily = ClonePrecisionDisplayFamily();
        typography.Subtitle1.FontFamily = ClonePrecisionDisplayFamily();
        typography.Subtitle2.FontFamily = CloneBodyFamily();
        typography.Body1.FontFamily = CloneBodyFamily();
        typography.Body2.FontFamily = CloneBodyFamily();
        typography.Button.FontFamily = CloneBodyFamily();
        typography.Caption.FontFamily = CloneBodyFamily();
        typography.Overline.FontFamily = ClonePrecisionDisplayFamily();

        return typography;
    }

    private static string[] CloneBodyFamily() => [.. BodyFontFamily];

    private static string[] CloneDisplayFamily() => [.. DisplayFontFamily];

    private static string[] ClonePrecisionDisplayFamily() => [.. PrecisionDisplayFontFamily];
}
