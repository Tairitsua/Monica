using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.WebUtilities;

namespace Monica.SignalR.UISignalR.State;

/// <summary>Shares one owned browser session and URL navigation across the three SignalR workspaces.</summary>
/// <remarks>The route component owns and drains operations. Child views borrow this context and never dispose its state.</remarks>
public sealed class SignalRWorkspaceContext(
    SignalRDebugPageState state,
    NavigationManager navigation,
    Func<Func<Task>, Task> runOperation)
{
    /// <summary>Gets the borrowed family state, retained while navigating among the three routes.</summary>
    public SignalRDebugPageState State { get; } = state;
    /// <summary>Gets the current application-relative path without its query or fragment.</summary>
    public string Path => "/" + navigation.ToBaseRelativePath(navigation.Uri).Split('?', '#')[0].Trim('/');
    /// <summary>Gets a URL-held filter or selection, or an empty string when absent.</summary>
    public string Get(string name) => QueryHelpers.ParseQuery(new Uri(navigation.Uri).Query).TryGetValue(name, out var value)
        ? value.ToString() : string.Empty;
    /// <summary>Changes URL-held state. Evidence openings push history; ordinary filter edits replace it.</summary>
    public void Set(string name, string? value, bool push = false) => Set(new Dictionary<string, object?> { [name] = string.IsNullOrEmpty(value) ? null : value }, push);
    /// <summary>Changes several URL-held values atomically, retaining other filters.</summary>
    public void Set(IReadOnlyDictionary<string, object?> values, bool push = false) =>
        navigation.NavigateTo(navigation.GetUriWithQueryParameters(values), replace: !push);
    /// <summary>Resolves a family route within the host's application base path, retaining its query.</summary>
    public string Href(string path) => navigation.ToAbsoluteUri(path.TrimStart('/')).ToString();
    /// <summary>Runs work through the route owner's admission and retirement boundary.</summary>
    public Task Run(Func<Task> operation) => runOperation(operation);
}
