using Microsoft.Extensions.Logging;
using Monica.UI.UIDiffHighlight.Abstractions;
using Monica.UI.UIDiffHighlight.Models;
using Monica.Core.Results;

namespace Monica.UI.UIDiffHighlight.Facades;

/// <summary>
/// UI-facing service for diff highlighting operations.
/// </summary>
public class DiffHighlightFacade(IDiffHighlight diffHighlight, ILogger<DiffHighlightFacade> logger)
{
    /// <summary>
    /// Runs a text diff and returns the highlighted result.
    /// </summary>
    /// <param name="oldText">The original text.</param>
    /// <param name="newText">The updated text.</param>
    /// <param name="options">The diff options to apply.</param>
    /// <returns>A response containing the highlighted result.</returns>
    public async Task<Result<DiffHighlightResult>> HighlightAsync(string oldText, string newText, DiffHighlightOptions? options = null)
    {
        try
        {
            logger.LogDebug("执行文本差异对比，oldText长度: {OldLength}, newText长度: {NewLength}",
                oldText.Length, newText.Length);

            var result = await diffHighlight.HighlightAsync(oldText, newText, options);
            
            logger.LogInformation("文本差异对比完成，处理时间: {ProcessingTime}ms, 变更数: {TotalChanges}",
                result.ProcessingTimeMs, result.Statistics.TotalChanges);
            
            return Result.Success(result);
        }
        catch (ArgumentException ex)
        {
            logger.LogWarning(ex, "文本差异对比参数错误: {Message}", ex.Message);
            return Result.Failure<DiffHighlightResult>($"参数错误: {ex.Message}");
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "文本差异对比过程中发生错误");
            return Result.Failure<DiffHighlightResult>($"文本差异对比失败: {ex.Message}");
        }
    }
    
    /// <summary>
    /// Runs a text diff synchronously and returns the highlighted result.
    /// </summary>
    /// <param name="oldText">The original text.</param>
    /// <param name="newText">The updated text.</param>
    /// <param name="options">The diff options to apply.</param>
    /// <returns>A response containing the highlighted result.</returns>
    public Result<DiffHighlightResult> Highlight(string oldText, string newText, DiffHighlightOptions? options = null)
    {
        try
        {
            logger.LogDebug("执行文本差异对比（同步），oldText长度: {OldLength}, newText长度: {NewLength}",
                oldText.Length, newText.Length);
            
            var result = diffHighlight.Highlight(oldText, newText, options);
            
            logger.LogInformation("文本差异对比完成，处理时间: {ProcessingTime}ms, 变更数: {TotalChanges}",
                result.ProcessingTimeMs, result.Statistics.TotalChanges);
            
            return Result.Success(result);
        }
        catch (ArgumentException ex)
        {
            logger.LogWarning(ex, "文本差异对比参数错误: {Message}", ex.Message);
            return Result.Failure<DiffHighlightResult>($"参数错误: {ex.Message}");
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "文本差异对比过程中发生错误");
            return Result.Failure<DiffHighlightResult>($"文本差异对比失败: {ex.Message}");
        }
    }
    
    /// <summary>
    /// Gets diff statistics without exposing the rendered content.
    /// </summary>
    /// <param name="oldText">The original text.</param>
    /// <param name="newText">The updated text.</param>
    /// <param name="options">The diff options to apply.</param>
    /// <returns>A response containing the diff statistics.</returns>
    public async Task<Result<DiffStatistics>> GetStatisticsAsync(string oldText, string newText, DiffHighlightOptions? options = null)
    {
        try
        {
            logger.LogDebug("获取文本差异统计信息");
            
            var result = await diffHighlight.HighlightAsync(oldText, newText, options);
            
            logger.LogDebug("获取文本差异统计信息完成");
            return Result.Success(result.Statistics);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "获取文本差异统计信息过程中发生错误");
            return Result.Failure<DiffStatistics>($"获取统计信息失败: {ex.Message}");
        }
    }
    
    /// <summary>
    /// Checks whether two texts produce any changes.
    /// </summary>
    /// <param name="oldText">The original text.</param>
    /// <param name="newText">The updated text.</param>
    /// <param name="options">The diff options to apply.</param>
    /// <returns>A response indicating whether the texts are identical.</returns>
    public async Task<Result<bool>> IsIdenticalAsync(string oldText, string newText, DiffHighlightOptions? options = null)
    {
        try
        {
            logger.LogDebug("检查文本是否相同");
            
            var result = await diffHighlight.HighlightAsync(oldText, newText, options);
            var isIdentical = !result.HasChanges;
            
            logger.LogDebug("文本相同性检查完成，结果: {IsIdentical}", isIdentical);
            return Result.Success(isIdentical);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "检查文本相同性过程中发生错误");
            return Result.Failure<bool>($"检查失败: {ex.Message}");
        }
    }
}
