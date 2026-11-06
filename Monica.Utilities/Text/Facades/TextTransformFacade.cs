using System.Text.Json;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Logging;
using Monica.Core.Extensions;
using Monica.Core.Results;
using Monica.Utilities.Localization;
using Monica.Utilities.Text.Models;
using Monica.Utilities.Text.Services;

namespace Monica.Utilities.Text.Facades;

/// <summary>
/// Result-envelope entry point for text and JSON transformations consumed by UI pages or APIs.
/// </summary>
public sealed class TextTransformFacade
{
    private readonly TextTransformService _textTransformService;
    private readonly IStringLocalizer<UtilitiesResource> _localizer;
    private readonly ILogger<TextTransformFacade> _logger;

    /// <summary>
    /// Creates the text transformation facade.
    /// </summary>
    /// <param name="textTransformService">Service that performs escaped-text and JSON rewriting operations.</param>
    /// <param name="localizer">Localizer used for developer-facing result messages.</param>
    /// <param name="logger">Logger used to record unexpected failures.</param>
    public TextTransformFacade(
        TextTransformService textTransformService,
        IStringLocalizer<UtilitiesResource> localizer,
        ILogger<TextTransformFacade> logger)
    {
        _textTransformService = textTransformService;
        _localizer = localizer;
        _logger = logger;
    }

    /// <summary>
    /// Executes the requested text transformation and wraps the output in Monica's result envelope.
    /// </summary>
    /// <param name="request">Transformation request describing the input text and operation.</param>
    /// <returns>A result envelope containing the transformed output.</returns>
    public Result<TextTransformResult> Transform(TextTransformRequest request)
    {
        try
        {
            return Result.Success(_textTransformService.Transform(request));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to execute utilities text transform {Operation}.", request.Operation);
            return Result.Failure<TextTransformResult>(
                _localizer["ServiceMessages:TextTransformFailed", ex.GetMessageRecursively()].Value,
                GetStatus(ex));
        }
    }

    private static ResultStatus GetStatus(Exception exception)
    {
        return exception switch
        {
            ArgumentException => ResultStatus.BadRequest,
            JsonException => ResultStatus.BadRequest,
            FormatException => ResultStatus.BadRequest,
            _ => ResultStatus.InternalError
        };
    }
}
