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

    [Theory]
    [InlineData(0, 13, 14)]
    [InlineData(5, 13, 14)]
    [InlineData(23, 13, 14)]
    public void FromLocalToUtc_WhenGivenLocalTime_ShouldApplyTheLocalBaseOffset(int hour, int minute, int second)
    {
        var local = new DateTimeOffset(2000, 1, 1, hour, minute, second, TimeZoneInfo.Local.BaseUtcOffset);

        var result = TimeOnly.FromDateTime(local.DateTime).FromLocalToUtc();

        Assert.Equal(TimeOnly.FromDateTime(local.UtcDateTime), result);
    }
}
