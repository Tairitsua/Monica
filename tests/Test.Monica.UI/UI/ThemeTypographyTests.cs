using AwesomeAssertions;
using Monica.UI.Theming;
using Monica.UI.Theming.Definitions;
using MudBlazor;
using Xunit;

namespace Test.Monica.UI;

public sealed class ThemeTypographyTests
{
    [Fact]
    public void CreateTheme_DefaultTheme_ShouldUsePrecisionTypography()
    {
        var typography = new DefaultTheme().CreateTheme().Typography;

        typography.H1.FontSize.Should().Be("2.5rem");
        typography.Body1.FontSize.Should().Be("0.95rem");
        GetRoles(typography).Should().AllSatisfy(role => role.FontFamily.Should().StartWith("MoPrecision"));
    }

    [Fact]
    public void CreateTheme_ThemesWithoutTypeIdentity_ShouldChangeOnlyFontFamilies()
    {
        IThemeDefinition[] themes = [new FreshTheme(), new MudBlazorDefaultTheme()];
        var mudDefaults = new Typography();

        foreach (var definition in themes)
        {
            var typography = definition.CreateTheme().Typography;

            AssertMonicaFontRoles(typography);
            GetRoles(typography)
                .Zip(GetRoles(mudDefaults))
                .Should()
                .AllSatisfy(pair => AssertSameTypeMetrics(pair.First, pair.Second));
        }
    }

    [Fact]
    public void CreateTheme_ThemesWithTypeIdentity_ShouldRetainTheirExplicitTypography()
    {
        IThemeDefinition[] themes =
        [
            new ComicBurstTheme(),
            new FluentDesignTheme(),
            new HermesTealTheme(),
            new InkLandscapeTheme(),
            new MacaronSweetheartTheme(),
            new MaterialDesign3Theme(),
            new VibeUsageMatrixTheme(),
            new ZenInkTheme()
        ];

        foreach (var definition in themes)
        {
            var typography = definition.CreateTheme().Typography;

            typography.Default.FontFamily.Should().NotBeNullOrEmpty();
            typography.Default.FontFamily![0].Should().NotBe("MoDefaultBody");
            GetRoles(typography).Should().AllSatisfy(role => role.FontFamily.Should().NotBeNullOrEmpty());
        }
    }

    private static BaseTypography[] GetRoles(Typography typography) =>
    [
        typography.Default,
        typography.H1,
        typography.H2,
        typography.H3,
        typography.H4,
        typography.H5,
        typography.H6,
        typography.Subtitle1,
        typography.Subtitle2,
        typography.Body1,
        typography.Body2,
        typography.Button,
        typography.Caption,
        typography.Overline
    ];

    private static void AssertMonicaFontRoles(Typography typography)
    {
        BaseTypography[] bodyRoles =
        [
            typography.Default,
            typography.Subtitle2,
            typography.Body1,
            typography.Body2,
            typography.Button,
            typography.Caption
        ];
        BaseTypography[] displayRoles =
        [
            typography.H1,
            typography.H2,
            typography.H3,
            typography.H4,
            typography.H5,
            typography.H6,
            typography.Subtitle1,
            typography.Overline
        ];

        bodyRoles.Should().AllSatisfy(role => role.FontFamily.Should().StartWith("MoDefaultBody"));
        displayRoles.Should().AllSatisfy(role => role.FontFamily.Should().StartWith("MoDefaultDisplay"));
    }

    private static void AssertSameTypeMetrics(BaseTypography actual, BaseTypography expected)
    {
        actual.FontSize.Should().Be(expected.FontSize);
        actual.FontWeight.Should().Be(expected.FontWeight);
        actual.LineHeight.Should().Be(expected.LineHeight);
        actual.LetterSpacing.Should().Be(expected.LetterSpacing);
        actual.TextTransform.Should().Be(expected.TextTransform);
    }
}
