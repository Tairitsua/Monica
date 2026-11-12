using Monica.Core.Results;
using Monica.Markdown.Facades;
using Monica.Markdown.Models;

namespace Monica.Markdown.UIMarkdown.State;

/// <summary>Owns a group dialog's rescan and catalog read without disposing the borrowed facade.</summary>
internal sealed class MarkdownGroupSwitcherState(MarkdownFacade facade, List<MarkdownDocumentGroup> groups)
    : IAsyncDisposable
{
    private readonly MarkdownViewerOperationLifetime _lifetime = new();
    private int _refreshing;

    internal List<MarkdownDocumentGroup> Groups { get; private set; } = groups;
    internal string? ErrorMessage { get; private set; }
    internal bool IsRefreshing => Volatile.Read(ref _refreshing) != 0;
    internal bool IsRetired => _lifetime.IsRetired;

    internal Task RefreshAsync()
    {
        if (_lifetime.IsRetired || Interlocked.CompareExchange(ref _refreshing, 1, 0) != 0)
            return Task.CompletedTask;
        return _lifetime.RunAsync(async token =>
        {
            try
            {
                token.ThrowIfCancellationRequested();
                _lifetime.TryApply(() => ErrorMessage = null);
                var refresh = await facade.RefreshAllAsync();
                token.ThrowIfCancellationRequested();
                if (refresh.IsFailure())
                {
                    _lifetime.TryApply(() => ErrorMessage = refresh.Message ?? string.Empty);
                    return;
                }

                var response = await facade.GetAllDocumentGroupsAsync();
                token.ThrowIfCancellationRequested();
                if (response.IsFailure())
                    _lifetime.TryApply(() => ErrorMessage = response.Message ?? string.Empty);
                else
                    _lifetime.TryApply(() => Groups = response.Data
                        ?? throw new InvalidOperationException("The document group read returned no data."));
            }
            finally { Interlocked.Exchange(ref _refreshing, 0); }
        });
    }

    public ValueTask DisposeAsync() => _lifetime.RetireAsync();
}
