using AwesomeAssertions;
using Monica.Core.Results;

namespace Test.Monica.DataChannel.Support;

internal static class ResultAssertions
{
    public static T Success<T>(Result<T> result) where T : class?
    {
        result.Status.Should().Be(ResultStatus.Ok);
        result.Message.Should().BeNullOrEmpty();
        result.Data.Should().NotBeNull();
        return result.Data!;
    }
}
