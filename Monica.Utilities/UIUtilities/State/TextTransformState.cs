using Monica.Core.Results;
using Monica.Utilities.Text.Facades;
using Monica.Utilities.Text.Models;

namespace Monica.Utilities.UIUtilities.State;

/// <summary>
/// Owns the transient UI state for the text and JSON transformation panel.
/// </summary>
public sealed class TextTransformState(TextTransformFacade textTransformFacade)
{
    /// <summary>
    /// Gets or sets the current input text.
    /// </summary>
    public string InputText { get; set; } = string.Empty;

    /// <summary>
    /// Gets the latest transformation output.
    /// </summary>
    public string OutputText { get; private set; } = string.Empty;

    /// <summary>
    /// Gets the latest transformation error, if any.
    /// </summary>
    public string? ErrorMessage { get; private set; }

    /// <summary>
    /// Gets the latest successful transformation result.
    /// </summary>
    public TextTransformResult? LastResult { get; private set; }

    /// <summary>
    /// Gets the raw input and operation that produced the retained successful output.
    /// This snapshot is not normalized and survives draft edits and failed attempts.
    /// </summary>
    public TextTransformRequest? AcceptedRequest { get; private set; }

    /// <summary>Gets the raw input and operation of the latest explicit transformation attempt.</summary>
    public TextTransformRequest? LastAttemptedRequest { get; private set; }

    /// <summary>
    /// Gets whether the latest attempt succeeded, or null before an attempt or after Clear.
    /// Reusing output changes the draft without creating a transformation attempt.
    /// </summary>
    public bool? LatestAttemptSucceeded { get; private set; }

    /// <summary>
    /// Gets whether the draft differs ordinally from the retained result's raw input.
    /// Result lengths remain the service's processed-input lengths, independent of this draft.
    /// </summary>
    public bool HasInputChanges => AcceptedRequest is { } accepted
        && !string.Equals(InputText, accepted.Input, StringComparison.Ordinal);

    /// <summary>
    /// Executes the selected text transformation.
    /// </summary>
    /// <param name="operation">Transformation operation to execute.</param>
    /// <returns>The result envelope returned by the facade.</returns>
    public Result<TextTransformResult> Execute(TextTransformOperation operation)
    {
        ErrorMessage = null;

        var request = new TextTransformRequest(InputText, operation);
        LastAttemptedRequest = request;
        var result = textTransformFacade.Transform(request);
        var error = result.WithoutData();
        var transformed = result.Data;
        if (result.IsFailure() || transformed is null)
        {
            ErrorMessage = error.Message ?? "The text transformation returned no data.";
            LatestAttemptSucceeded = false;
            if (result.IsSuccess()) return Result.Failure<TextTransformResult>(ErrorMessage);
        }
        else
        {
            LastResult = transformed;
            OutputText = transformed.Output;
            AcceptedRequest = request;
            LatestAttemptSucceeded = true;
        }

        return result;
    }

    /// <summary>
    /// Replaces the current input with the latest output so the next action can build on it.
    /// </summary>
    public void UseOutputAsInput()
    {
        if (string.IsNullOrEmpty(OutputText))
        {
            return;
        }

        InputText = OutputText;
        ErrorMessage = null;
    }

    /// <summary>
    /// Clears the input, output, and result metadata.
    /// </summary>
    public void Clear()
    {
        InputText = string.Empty;
        OutputText = string.Empty;
        ErrorMessage = null;
        LastResult = null;
        AcceptedRequest = null;
        LastAttemptedRequest = null;
        LatestAttemptSucceeded = null;
    }
}
