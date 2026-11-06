using System.Text.Json;
using System.Text.Json.Serialization;
using Monica.Core.Execution;
using Monica.Core.Results;
using Monica.Core.Results.Abstractions;
using Xunit;

namespace Test.Monica.Core.Results;

public sealed class ResultContractTests
{
    private static readonly JsonSerializerOptions WEB_JSON = new(JsonSerializerDefaults.Web);
    private static readonly JsonSerializerOptions OMIT_NULL_JSON = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    [Fact]
    public void Factories_WhenCreatingSuccessAndCreated_ShouldKeepStringAsDataAndMessageSeparate()
    {
        Result empty = Result.Success();
        Result<string> data = Result.Success("Alice");
        Result created = Result.Created();
        Result<string> createdData = Result.Created("order");
        Result<string?> optionalName = Result.Success<string?>(null);
        Result<int?> optionalCount = Result.Success<int?>(null);
        IReadOnlyList<int> values = [1, 2];
        Result<IReadOnlyList<int>> collection = Result.Success<IReadOnlyList<int>>(values);

        Assert.Equal("", empty.Message);
        Assert.Equal(ResultStatus.Ok, empty.Status);
        Assert.Null(data.Message);
        Assert.Equal("Alice", data.Data);
        Assert.Equal(ResultStatus.Created, created.Status);
        Assert.Equal("", created.Message);
        Assert.Equal(ResultStatus.Created, createdData.Status);
        Assert.Null(createdData.Message);
        Assert.Equal("order", createdData.Data);
        Assert.True(optionalName.IsSuccess());
        Assert.Null(optionalName.Data);
        Assert.True(optionalCount.IsSuccess());
        Assert.Null(optionalCount.Data);
        Assert.Same(values, collection.Data);
    }

    [Fact]
    public void WithMessage_WhenAppliedToEachEnvelope_ShouldMutateAndPreserveItsConcreteType()
    {
        var plain = Result.Success();
        var typed = Result.Success("payload");
        var paged = PagedResult.Success<int>(1, [42]);

        Result plainReturned = plain.WithMessage("saved");
        Result<string> typedReturned = typed.WithMessage("loaded");
        PagedResult<int> pagedReturned = paged.WithMessage("listed");

        Assert.Same(plain, plainReturned);
        Assert.Same(typed, typedReturned);
        Assert.Same(paged, pagedReturned);
        Assert.Equal("payload", typedReturned.Data);
        Assert.Equal("loaded", typedReturned.Message);
        Assert.Equal(ResultStatus.Ok, typedReturned.Status);
        Assert.Equal("listed", pagedReturned.Message);
    }

    [Fact]
    public void Classification_WhenStatusIsDefined_ShouldDistinguishSuccessAndFailure()
    {
        foreach (var status in Enum.GetValues<ResultStatus>().Where(status => status != ResultStatus.Unknown))
        {
            var result = new Result { Status = status };
            var success = status is ResultStatus.Ok or ResultStatus.Created;
            Assert.Equal(success, result.IsSuccess());
            Assert.Equal(!success, result.IsFailure());
        }
    }

    [Theory]
    [InlineData(ResultStatus.Unknown)]
    [InlineData((ResultStatus)299)]
    [InlineData((ResultStatus)460)]
    public void Classification_WhenStatusIsInvalid_ShouldRejectBothPredicatesAndRequireRollback(ResultStatus status)
    {
        var result = new Result { Status = status };
        Assert.Throws<InvalidOperationException>(() => result.IsSuccess());
        Assert.Throws<InvalidOperationException>(() => result.IsFailure());
        Assert.True(ExecutionOutcomePolicy.RequiresRollback(result));
        Assert.True(ExecutionOutcomePolicy.RequiresRollback(result, isReadOnly: true));
    }

    [Theory]
    [InlineData(ResultStatus.Unknown)]
    [InlineData(ResultStatus.Ok)]
    [InlineData(ResultStatus.Created)]
    [InlineData((ResultStatus)460)]
    public void FailureFactories_WhenStatusIsNotAFailure_ShouldRejectIt(ResultStatus status)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => Result.Failure("error", status));
        Assert.Throws<ArgumentOutOfRangeException>(() => Result.Failure<int>("error", status));
        Assert.Throws<ArgumentOutOfRangeException>(() => PagedResult.Failure<int>("error", status));
    }

    [Fact]
    public void Projections_WhenFailureCarriesReport_ShouldPreserveStatusAndCopyMetadata()
    {
        var fields = new[] { new ResultFieldError("name", "required", "Name is required.") };
        var error = new ResultError("validation.failed", "origin-trace", "source", "save", fields);
        var nested = new object();
        var source = Result.Failure("Rejected", ResultStatus.Conflict).SetError(error).SetMetadata("nested", nested);
        var report = new[] { "invalid item" };

        var withData = source.WithData(report);
        var withoutData = withData.WithoutData();
        var forwarded = Result.FromFailure<int>(withData);
        var page = PagedResult.FromFailure<int>(withData);
        var pageWithoutData = page.WithoutData();
        Result[] copies = [withoutData, pageWithoutData, Result.FromFailure(withData)];

        Assert.Same(report, withData.Data);
        Assert.Equal(ResultStatus.Conflict, withData.Status);
        Assert.Equal("Rejected", withData.Message);
        Assert.Equal(0, forwarded.Data);
        Assert.Equal(0, page.Data.Sum);
        Assert.Empty(page.Data.Items!);
        foreach (IResultEnvelope copy in copies.Cast<IResultEnvelope>().Concat([withData, forwarded, page]))
        {
            Assert.NotSame(source.Metadata, copy.Metadata);
            Assert.Equal(ResultStatus.Conflict, copy.Status);
            Assert.Equal("Rejected", copy.Message);
            Assert.True(copy.TryGetError(WEB_JSON, out var copiedError));
            Assert.Same(error, copiedError);
            Assert.Same(nested, ((IDictionary<string, object?>)copy.Metadata!)["nested"]);
        }
        withData.SetMetadata("local", 1);
        Assert.False(((IDictionary<string, object?>)source.Metadata!).ContainsKey("local"));
        Assert.False(((IDictionary<string, object?>)withoutData.Metadata!).ContainsKey("local"));
    }

    [Fact]
    public void FromFailure_WhenSourceIsNullSuccessfulOrInvalid_ShouldRejectIt()
    {
        Assert.Throws<ArgumentNullException>(() => Result.FromFailure(null!));
        Assert.Throws<ArgumentNullException>(() => Result.FromFailure<int>(null!));
        Assert.Throws<ArgumentNullException>(() => PagedResult.FromFailure<int>(null!));

        IResultEnvelope[] invalid = [Result.Success(), Result.Created(1), new Result(), new Result { Status = (ResultStatus)460 }];
        foreach (var source in invalid)
        {
            Assert.Throws<InvalidOperationException>(() => Result.FromFailure(source));
            Assert.Throws<InvalidOperationException>(() => Result.FromFailure<int>(source));
            Assert.Throws<InvalidOperationException>(() => PagedResult.FromFailure<int>(source));
        }
    }

    [Fact]
    public void Serialization_WhenUsingDefaultsAndNullOmission_ShouldPreserveEnvelopeMeaning()
    {
        using var plain = JsonDocument.Parse(JsonSerializer.Serialize(Result.Success(), WEB_JSON));
        Assert.Equal("", plain.RootElement.GetProperty("message").GetString());
        Assert.Equal(200, plain.RootElement.GetProperty("status").GetInt32());
        Assert.False(plain.RootElement.TryGetProperty("metadata", out _));
        Assert.False(plain.RootElement.TryGetProperty("data", out _));

        using var data = JsonDocument.Parse(JsonSerializer.Serialize(Result.Success("Alice"), OMIT_NULL_JSON));
        Assert.Equal("Alice", data.RootElement.GetProperty("data").GetString());
        Assert.False(data.RootElement.TryGetProperty("message", out _));
        Assert.False(data.RootElement.TryGetProperty("isSuccess", out _));
        Assert.False(data.RootElement.TryGetProperty("isFailure", out _));

        var created = JsonSerializer.Deserialize<Result<string>>(
            JsonSerializer.Serialize(Result.Created("order").WithMessage("Created"), WEB_JSON), WEB_JSON);
        Assert.NotNull(created);
        Assert.Equal(ResultStatus.Created, created.Status);
        Assert.Equal("order", created.Data);
        Assert.Equal("Created", created.Message);

        var report = Result.Failure("Rejected", ResultStatus.Conflict).WithData(new[] { "invalid" });
        var roundTrip = JsonSerializer.Deserialize<Result<string[]>>(JsonSerializer.Serialize(report, WEB_JSON), WEB_JSON);
        Assert.NotNull(roundTrip);
        Assert.Equal(ResultStatus.Conflict, roundTrip.Status);
        Assert.Equal("Rejected", roundTrip.Message);
        Assert.Equal(new[] { "invalid" }, roundTrip.Data);

        using var nullable = JsonDocument.Parse(JsonSerializer.Serialize(Result.Success<int?>(null), OMIT_NULL_JSON));
        Assert.False(nullable.RootElement.TryGetProperty("data", out _));
    }

    [Fact]
    public void Serialization_WhenFailureContainsErrorMetadata_ShouldKeepOriginAndFieldErrors()
    {
        var error = new ResultError("validation.failed", "origin-trace", "orders", "create",
            [new ResultFieldError("name", "required", "Name is required.")]);
        var original = Result.Failure<string>("Rejected", ResultStatus.Conflict).SetError(error);
        var json = JsonSerializer.Serialize(original, WEB_JSON);
        using var document = JsonDocument.Parse(json);
        var serializedError = document.RootElement.GetProperty("metadata").GetProperty("error");
        Assert.Equal("origin-trace", serializedError.GetProperty("traceId").GetString());
        Assert.Equal("name", serializedError.GetProperty("fields")[0].GetProperty("path").GetString());
        var decoded = JsonSerializer.Deserialize<Result<string>>(json, WEB_JSON);
        Assert.NotNull(decoded);
        Assert.True(decoded.TryGetError(WEB_JSON, out var actual));
        Assert.Equal(error.Code, actual.Code);
        Assert.Equal(error.TraceId, actual.TraceId);
        Assert.Equal(error.Service, actual.Service);
        Assert.Equal(error.Operation, actual.Operation);
        Assert.Equal("required", Assert.Single(actual.Fields!).Code);
    }

    [Fact]
    public void ToBulkResult_WhenSomeResultsFail_ShouldKeepTheFirstFailureAndAllData()
    {
        Result<int>[] responses =
        [
            Result.Success(1).WithMessage("one"),
            Result.Failure("conflict", ResultStatus.Conflict).WithData(2),
            Result.Failure<int>("not found", ResultStatus.NotFound)
        ];
        var bulk = responses.ToBulkResult();
        Assert.Equal(ResultStatus.Conflict, bulk.Status);
        Assert.Equal(new[] { 1, 2, 0 }, bulk.Data);
        Assert.Equal($"one{Environment.NewLine}conflict{Environment.NewLine}not found", bulk.Message);
    }

    [Fact]
    public void Pagination_WhenSuccessfulOrFailed_ShouldKeepPageWireDefaultsAndCalculations()
    {
        var success = PagedResult.Success<int>(8, [4, 5, 6], currentPage: 2, pageSize: 3, cursor: "next");
        var successJson = JsonSerializer.Serialize(success, WEB_JSON);
        using var successDocument = JsonDocument.Parse(successJson);
        var page = successDocument.RootElement.GetProperty("data");
        Assert.Equal(8, page.GetProperty("sum").GetInt32());
        Assert.Equal(3, page.GetProperty("items").GetArrayLength());
        Assert.Equal(3, page.GetProperty("pageSize").GetInt32());
        Assert.Equal(2, page.GetProperty("currentPage").GetInt32());
        Assert.Equal(3, page.GetProperty("totalPages").GetInt32());
        Assert.True(page.GetProperty("hasPrevious").GetBoolean());
        Assert.True(page.GetProperty("hasNext").GetBoolean());
        Assert.Equal("next", page.GetProperty("cursor").GetString());
        var roundTrip = JsonSerializer.Deserialize<PagedResult<int>>(successJson, WEB_JSON);
        Assert.NotNull(roundTrip);
        Assert.Equal(new[] { 4, 5, 6 }, roundTrip.Data.Items);
        Assert.Equal(3, roundTrip.Data.TotalPages);

        var failure = PagedResult.Failure<int>("Rejected", ResultStatus.Conflict);
        using var failureDocument = JsonDocument.Parse(JsonSerializer.Serialize(failure, OMIT_NULL_JSON));
        var failedPage = failureDocument.RootElement.GetProperty("data");
        Assert.Equal(0, failedPage.GetProperty("sum").GetInt32());
        Assert.Equal(0, failedPage.GetProperty("items").GetArrayLength());
        Assert.False(failedPage.TryGetProperty("pageSize", out _));
        Assert.False(failedPage.TryGetProperty("currentPage", out _));
        Assert.False(failedPage.TryGetProperty("cursor", out _));

        var remote = PagedResult<int>.CreateRemoteFailure(ResultStatus.BadGateway, "Remote failure");
        using var remoteDocument = JsonDocument.Parse(JsonSerializer.Serialize(remote, WEB_JSON));
        var remotePage = remoteDocument.RootElement.GetProperty("data");
        Assert.Equal(JsonValueKind.Null, remotePage.GetProperty("sum").ValueKind);
        Assert.Equal(JsonValueKind.Null, remotePage.GetProperty("items").ValueKind);
        Assert.Equal(ResultStatus.BadGateway, remote.Status);
    }
}
