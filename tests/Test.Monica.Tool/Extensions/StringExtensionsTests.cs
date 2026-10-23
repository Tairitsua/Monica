using Monica.Tool.Extensions;

namespace Test.Monica.Tool.Extensions;

public sealed class StringExtensionsTests
{
    [Fact]
    public void BeIfNullOrEmpty_WhenSourceIsEmpty_ShouldReturnFirstNonEmptyFallback()
    {
        var result = string.Empty.BeIfNullOrEmpty(string.Empty, "value1", "value2");

        Assert.Equal("value1", result);
    }

    [Fact]
    public void BeIfNullOrEmpty_WhenSourceHasValue_ShouldReturnSource()
    {
        var result = "value4".BeIfNullOrEmpty("value3", "value1", "value2");

        Assert.Equal("value4", result);
    }

    [Fact]
    public void BeIfNullOrEmpty_WhenAllValuesAreEmpty_ShouldReturnEmptyString()
    {
        var result = string.Empty.BeIfNullOrEmpty(string.Empty, string.Empty);

        Assert.Equal(string.Empty, result);
    }

    [Fact]
    public void FromLocalToUtc_WhenChinaLocalTimeCrossesDateBoundary_ShouldWrapToPreviousUtcDay()
    {
        var result = new TimeOnly(5, 13, 14).FromLocalToUtc();

        Assert.Equal(new TimeOnly(21, 13, 14), result);
    }
}
