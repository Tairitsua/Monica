using AwesomeAssertions;
using Monica.Core.Results;
using Monica.Generators.AutoController;
using Xunit;

namespace Test.Monica.Generators.AutoController;

public sealed class ResultApiCompilationTests
{
    [Theory]
    [InlineData("Result<string> Call() => \"error\";", "CS0029")]
    [InlineData("Result<int> Call() => 42;", "CS0029")]
    [InlineData("Result<string> Call() => Result.Success();", "CS0029")]
    [InlineData("Result<string> Call() => Result.Failure(\"error\");", "CS0029")]
    [InlineData("Result Call() => Result.Success(\"payload\");", "CS0029")]
    [InlineData("Result Call() => (Result)Result.Success(\"payload\");", "CS0030")]
    [InlineData("Result<string> Call() => Result.Success(42);", "CS0029")]
    [InlineData("string Call() => Result.Success();", "CS0029")]
    public void Compile_WhenImplicitOrExplicitConversionIsRequested_ShouldRejectIt(string declaration, string errorId)
    {
        var source = $$"""
            using Monica.Core.Results;
            public static class Scenario
            {
                public static {{declaration}}
            }
            """;

        var run = GeneratorTestHarness.Run(source, new ApiClientSourceGenerator());

        run.OutputErrors.Should().ContainSingle(diagnostic => diagnostic.Id == errorId);
    }

    [Fact]
    public void Compile_WhenFactoriesAreExplicit_ShouldRetainPayloadTypes()
    {
        var run = GeneratorTestHarness.Run("""
            using Monica.Core.Results;
            public static class Scenario
            {
                public static Result<string> Payload() => Result.Success("payload");
                public static Result<int> Failure() => Result.Failure<int>("error");
                public static Result<string?> Optional() => Result.Success<string?>(null);
            }
            """, new ApiClientSourceGenerator());

        run.OutputErrors.Should().BeEmpty();
        Result.Success("payload").Data.Should().Be("payload");
        Result.Failure<int>("error").Status.Should().Be(ResultStatus.BadRequest);
    }
}
