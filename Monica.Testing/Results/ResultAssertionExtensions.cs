using AwesomeAssertions;
using Monica.Core.Results;

namespace Monica.Testing.Results;

/// <summary>
/// Provides Monica-specific result assertions for infrastructure and application tests.
/// </summary>
public static class ResultAssertionExtensions
{
    /// <summary>
    /// Asserts that the result succeeded and optionally verifies the message.
    /// </summary>
    public static Result ShouldSucceed(this Result result, string? expectedMessage = null)
    {
        result.IsSuccess().Should().BeTrue();
        if (expectedMessage is not null)
        {
            result.Message.Should().Be(expectedMessage);
        }

        return result;
    }

    /// <summary>
    /// Asserts that the result failed and optionally verifies the status and message.
    /// </summary>
    public static Result ShouldFail(
        this Result result,
        ResultStatus expectedStatus = ResultStatus.BadRequest,
        string? expectedMessage = null)
    {
        result.Status.Should().Be(expectedStatus);
        if (expectedMessage is not null)
        {
            result.Message.Should().Be(expectedMessage);
        }

        return result;
    }

    /// <summary>
    /// Asserts that the result succeeded and returns the payload for additional checks.
    /// </summary>
    public static T? ShouldSucceed<T>(this Result<T> result, string? expectedMessage = null)
    {
        result.IsSuccess().Should().BeTrue();
        if (expectedMessage is not null)
        {
            result.Message.Should().Be(expectedMessage);
        }

        return result.Data;
    }

    /// <summary>
    /// Asserts that the result failed and optionally verifies the status and message.
    /// </summary>
    public static Result<T> ShouldFail<T>(
        this Result<T> result,
        ResultStatus expectedStatus = ResultStatus.BadRequest,
        string? expectedMessage = null)
    {
        result.Status.Should().Be(expectedStatus);
        if (expectedMessage is not null)
        {
            result.Message.Should().Be(expectedMessage);
        }

        return result;
    }

    /// <summary>
    /// Asserts that the paged result succeeded and returns the page payload.
    /// </summary>
    public static PagedResult<T>.PageData ShouldSucceed<T>(this PagedResult<T> result, string? expectedMessage = null)
    {
        result.IsSuccess().Should().BeTrue();
        if (expectedMessage is not null)
        {
            result.Message.Should().Be(expectedMessage);
        }

        return result.Data;
    }
}
