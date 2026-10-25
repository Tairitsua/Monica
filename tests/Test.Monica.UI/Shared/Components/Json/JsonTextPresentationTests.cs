using AwesomeAssertions;
using Monica.UI.Shared.Components.Json;
using Xunit;

namespace Test.Monica.UI.Shared.Components.Json;

public sealed class JsonTextPresentationTests
{
    [Theory]
    [InlineData("null")]
    [InlineData("true")]
    [InlineData("-1.2300e+45")]
    [InlineData("\"中文 😀\"")]
    [InlineData("[{},null,false,1]")]
    public void Create_WhenRootIsAnyJsonValue_ShouldAcceptStrictJson(string source)
    {
        var result = JsonTextPresentation.Create(source);
        result.Validation.IsValid.Should().BeTrue();
        result.Source.Should().Be(source);
        JsonTextPresentation.Create(result.Formatted).Validation.IsValid.Should().BeTrue();
        JsonTextPresentation.Create(result.Compact).Validation.IsValid.Should().BeTrue();
    }

    [Fact]
    public void Create_WhenFormatting_ShouldPreserveLargeNumbersDuplicatePropertiesAndReadableUnicode()
    {
        const string source = "{\"id\":123456789012345678901234567890,\"id\":1.2300e+45,\"label\":\"中文 <script>\"}";
        var result = JsonTextPresentation.Create(source);
        result.Source.Should().Be(source);
        result.Formatted.Should().Contain("123456789012345678901234567890").And.Contain("1.2300e+45")
            .And.Contain("中文 <script>");
        result.Compact.Should().Be(source);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" \t\r\n")]
    public void Create_WhenEmpty_ShouldDistinguishAbsenceFromInvalidJson(string? source)
    {
        var result = JsonTextPresentation.Create(source);
        result.Validation.Should().Be(new MoJsonValidation(false, true));
        result.Source.Should().Be(source ?? string.Empty);
    }

    [Theory]
    [InlineData("{\"a\":1,}")]
    [InlineData("// comment\n{}")]
    [InlineData("{}{}")]
    [InlineData("{\"draft\":")]
    public void Create_WhenSyntaxIsInvalid_ShouldRetainDraftWithoutRepair(string source)
    {
        var result = JsonTextPresentation.Create(source);
        result.Validation.IsValid.Should().BeFalse();
        result.Validation.IsEmpty.Should().BeFalse();
        result.Validation.Error.Should().NotBeNullOrWhiteSpace();
        result.Source.Should().Be(source);
        result.Formatted.Should().Be(source);
        result.Compact.Should().Be(source);
    }

    [Fact]
    public void Create_WhenNonAsciiPrecedesError_ShouldReportOneBasedUtf8BytePosition()
    {
        var result = JsonTextPresentation.Create("{\n\"中文\": }");
        result.Validation.LineNumber.Should().Be(2);
        result.Validation.BytePositionInLine.Should().Be(11);
    }

    [Theory]
    [InlineData("{\"a\\\"b\":\"</span><script>alert(1)</script>\",\"n\":-1.2e+3,\"v\":null}")]
    [InlineData("{\"draft\": \"unfinished\\")]
    [InlineData("\r\n { \"a\" : true, \"a\":false } \t\n")]
    public void Tokenize_WhenSourceIsUntrustedOrIncomplete_ShouldRetainEveryCharacter(string source)
    {
        var tokens = JsonTextPresentation.Tokenize(source).ToArray();
        string.Concat(tokens.Select(token => token.Text)).Should().Be(source);
        tokens.Where(token => token.Role is not null).Select(token => token.Role)
            .Should().OnlyContain(role => role == "key" || role == "string" || role == "number" || role == "literal");
    }

    [Fact]
    public void Tokenize_WhenStringContainsKeywordsAndDigits_ShouldKeepThemInOneStringToken()
    {
        var tokens = JsonTextPresentation.Tokenize("{\"key\":\"true null 123\\\"\",\"value\":false}").ToArray();
        tokens.Should().Contain(token => token.Role == "key" && token.Text == "\"key\"");
        tokens.Should().Contain(token => token.Role == "string" && token.Text == "\"true null 123\\\"\"");
        tokens.Should().Contain(token => token.Role == "literal" && token.Text == "false");
    }
}
