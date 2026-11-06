using AwesomeAssertions;
using Monica.Core.Results;
using Monica.Utilities.Text.Models;
using Test.Monica.Utilities.Support;
using Xunit;

namespace Test.Monica.Utilities.UIUtilities.State;

public sealed class TextTransformStateTests
{
    [Fact]
    public void Execute_WhenDraftChangesAndLaterAttemptsFail_ShouldRetainAcceptedSourceOperationAndOutput()
    {
        var state = UtilitiesTestServices.CreateTextState();
        const string rawSource = "  {\"text\":\"\\u4e2d\\u6587\"} \r\n";
        state.InputText = rawSource;

        var accepted = state.Execute(TextTransformOperation.FormatJson);

        accepted.Status.Should().Be(ResultStatus.Ok);
        accepted.Message.Should().BeNull();
        accepted.Data.Should().NotBeNull();
        var acceptedOutput = accepted.Data!.Output;
        state.AcceptedRequest.Should().Be(new TextTransformRequest(rawSource, TextTransformOperation.FormatJson));
        state.LatestAttemptSucceeded.Should().BeTrue();
        state.HasInputChanges.Should().BeFalse();

        state.InputText = "{broken";
        state.HasInputChanges.Should().BeTrue();
        state.OutputText.Should().Be(acceptedOutput);
        var malformed = state.Execute(TextTransformOperation.MinifyJson);

        malformed.Status.Should().Be(ResultStatus.BadRequest);
        malformed.Message.Should().NotBeNullOrWhiteSpace();
        malformed.Data.Should().BeNull();
        state.ErrorMessage.Should().Be(malformed.Message);
        state.LastAttemptedRequest.Should().Be(new TextTransformRequest("{broken", TextTransformOperation.MinifyJson));
        state.LatestAttemptSucceeded.Should().BeFalse();
        state.LastResult.Should().BeSameAs(accepted.Data);
        state.AcceptedRequest.Should().Be(new TextTransformRequest(rawSource, TextTransformOperation.FormatJson));
        state.OutputText.Should().Be(acceptedOutput);

        state.InputText = " \r\n";
        var empty = state.Execute(TextTransformOperation.DecodeEscapedText);

        empty.Status.Should().Be(ResultStatus.BadRequest);
        empty.Message.Should().NotBeNullOrWhiteSpace();
        empty.Data.Should().BeNull();
        state.ErrorMessage.Should().Be(empty.Message);
        state.LastAttemptedRequest.Should().Be(new TextTransformRequest(" \r\n", TextTransformOperation.DecodeEscapedText));
        state.LastResult.Should().BeSameAs(accepted.Data);
        state.AcceptedRequest.Should().Be(new TextTransformRequest(rawSource, TextTransformOperation.FormatJson));
        state.OutputText.Should().Be(acceptedOutput);
    }

    [Fact]
    public void Execute_WhenJsonHasWhitespaceAndStringLayer_ShouldReportProcessedLengthsAndReadableUnicode()
    {
        var state = UtilitiesTestServices.CreateTextState();
        const string rawSource = " \r\n\"{\\\"text\\\":\\\"\\\\u4e2d\\\\u6587\\\"}\" \t";
        state.InputText = rawSource;

        var result = state.Execute(TextTransformOperation.MinifyJson);

        result.Status.Should().Be(ResultStatus.Ok);
        result.Message.Should().BeNull();
        result.Data.Should().NotBeNull();
        result.Data!.Output.Should().Be("{\"text\":\"中文\"}");
        result.Data.InputLength.Should().Be(rawSource.Trim().Length);
        result.Data.OutputLength.Should().Be(result.Data.Output.Length);
        result.Data.JsonStringLayersRemoved.Should().Be(1);
        result.Data.DecodePasses.Should().Be(0);
        state.AcceptedRequest!.Input.Should().Be(rawSource);
        state.HasInputChanges.Should().BeFalse();
    }

    [Fact]
    public void Execute_WhenDecodeHasNoEscapes_ShouldAcceptUnchangedSourceWithAccurateMetadata()
    {
        var state = UtilitiesTestServices.CreateTextState();
        const string source = "  already readable 中文\r\n";
        state.InputText = source;

        var result = state.Execute(TextTransformOperation.DecodeEscapedText);

        result.Status.Should().Be(ResultStatus.Ok);
        result.Message.Should().BeNull();
        result.Data.Should().NotBeNull();
        result.Data!.Output.Should().Be(source);
        result.Data.InputLength.Should().Be(source.Length);
        result.Data.OutputLength.Should().Be(source.Length);
        result.Data.DecodePasses.Should().Be(0);
        result.Data.JsonStringLayersRemoved.Should().Be(0);
        state.LatestAttemptSucceeded.Should().BeTrue();
        state.AcceptedRequest.Should().Be(new TextTransformRequest(source, TextTransformOperation.DecodeEscapedText));
    }

    [Fact]
    public void UseOutputAsInputAndClear_WhenAnAttemptFailed_ShouldReuseRetainedOutputWithoutRewritingAttemptHistory()
    {
        var state = UtilitiesTestServices.CreateTextState();
        state.InputText = "\\u4e2d\\u6587";
        var accepted = state.Execute(TextTransformOperation.DecodeEscapedText);
        state.InputText = "{invalid";
        state.Execute(TextTransformOperation.FormatJson);
        var attempted = state.LastAttemptedRequest;
        var acceptedRequest = state.AcceptedRequest;

        state.UseOutputAsInput();

        state.InputText.Should().Be("中文");
        state.OutputText.Should().Be("中文");
        state.ErrorMessage.Should().BeNull();
        state.LastResult.Should().BeSameAs(accepted.Data);
        state.AcceptedRequest.Should().BeSameAs(acceptedRequest);
        state.LastAttemptedRequest.Should().BeSameAs(attempted);
        state.LatestAttemptSucceeded.Should().BeFalse();
        state.HasInputChanges.Should().BeTrue();

        state.Clear();

        state.InputText.Should().BeEmpty();
        state.OutputText.Should().BeEmpty();
        state.ErrorMessage.Should().BeNull();
        state.LastResult.Should().BeNull();
        state.AcceptedRequest.Should().BeNull();
        state.LastAttemptedRequest.Should().BeNull();
        state.LatestAttemptSucceeded.Should().BeNull();
        state.HasInputChanges.Should().BeFalse();
    }
}
