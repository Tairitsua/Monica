using Monica.Configuration.Models;
using Monica.Configuration.UI.Models;

namespace Monica.Configuration.UI.State;

/// <summary>
/// Scoped UI store for staged configuration mutations.
/// </summary>
public sealed class ConfigurationStateStore
{
    private readonly Dictionary<(string DefinitionKey, LogicalPath LogicalPath), PendingChange> _pendingChanges = [];
    private readonly Dictionary<(string DefinitionKey, LogicalPath LogicalPath), ConfigurationValidationIssue> _validationIssues = [];

    /// <summary>
    /// Raised whenever staged changes are modified.
    /// </summary>
    public event Action? Changed;

    /// <summary>
    /// Gets all staged changes.
    /// </summary>
    /// <returns>Pending changes ordered for display.</returns>
    public IReadOnlyList<PendingChange> GetPending()
    {
        return _pendingChanges.Values
            .OrderBy(change => change.DefinitionDisplayName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(change => change.LogicalPath.ToCanonicalString(), StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    /// <summary>
    /// Gets all active validation issues.
    /// </summary>
    /// <returns>Validation issues ordered for display.</returns>
    public IReadOnlyList<ConfigurationValidationIssue> GetValidationIssues()
    {
        return _validationIssues.Values
            .OrderBy(issue => issue.DefinitionDisplayName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(issue => issue.LogicalPath.ToCanonicalString(), StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    /// <summary>
    /// Gets a staged change for one path.
    /// </summary>
    /// <param name="definitionKey">The definition key.</param>
    /// <param name="path">The logical path.</param>
    /// <returns>The staged change, or null when none exists.</returns>
    public PendingChange? Get(string definitionKey, LogicalPath path)
    {
        return _pendingChanges.GetValueOrDefault(Key(definitionKey, path));
    }

    /// <summary>
    /// Gets a validation issue for one path.
    /// </summary>
    /// <param name="definitionKey">The definition key.</param>
    /// <param name="path">The logical path.</param>
    /// <returns>The validation issue, or null when none exists.</returns>
    public ConfigurationValidationIssue? GetValidationIssue(string definitionKey, LogicalPath path)
    {
        return _validationIssues.GetValueOrDefault(Key(definitionKey, path));
    }

    /// <summary>
    /// Gets all staged changes inside one definition path scope.
    /// </summary>
    /// <param name="definitionKey">The definition key that owns the scope.</param>
    /// <param name="scopePath">The root logical path of the scope.</param>
    /// <returns>The staged changes under the scope.</returns>
    public IReadOnlyList<PendingChange> GetScope(string definitionKey, LogicalPath scopePath)
    {
        return _pendingChanges.Values
            .Where(change => string.Equals(change.DefinitionKey, definitionKey, StringComparison.Ordinal) && IsPrefix(scopePath, change.LogicalPath))
            .OrderBy(change => change.LogicalPath.ToCanonicalString(), StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    /// <summary>
    /// Stages one pending change.
    /// </summary>
    /// <param name="change">The pending change.</param>
    public void Stage(PendingChange change)
    {
        _pendingChanges[Key(change.DefinitionKey, change.LogicalPath)] = change;
        _validationIssues.Remove(Key(change.DefinitionKey, change.LogicalPath));
        NotifyChanged();
    }

    /// <summary>
    /// Reports one invalid UI edit.
    /// </summary>
    /// <param name="issue">The validation issue.</param>
    public void ReportValidationIssue(ConfigurationValidationIssue issue)
    {
        _validationIssues[Key(issue.DefinitionKey, issue.LogicalPath)] = issue;
        _pendingChanges.Remove(Key(issue.DefinitionKey, issue.LogicalPath));
        NotifyChanged();
    }

    /// <summary>
    /// Clears one validation issue.
    /// </summary>
    /// <param name="definitionKey">The definition key.</param>
    /// <param name="path">The logical path.</param>
    public void ClearValidationIssue(string definitionKey, LogicalPath path)
    {
        if (_validationIssues.Remove(Key(definitionKey, path)))
        {
            NotifyChanged();
        }
    }

    /// <summary>
    /// Replaces all staged changes inside one definition path scope.
    /// </summary>
    /// <param name="definitionKey">The definition key that owns the scope.</param>
    /// <param name="scopePath">The root logical path of the scope to replace.</param>
    /// <param name="changes">The replacement staged changes for the scope.</param>
    public void ReplaceScope(string definitionKey, LogicalPath scopePath, IReadOnlyList<PendingChange> changes)
    {
        ReplaceScope(definitionKey, scopePath, changes, []);
    }

    /// <summary>
    /// Replaces all staged changes and validation issues inside one definition path scope.
    /// </summary>
    /// <param name="definitionKey">The definition key that owns the scope.</param>
    /// <param name="scopePath">The root logical path of the scope to replace.</param>
    /// <param name="changes">The replacement staged changes for the scope.</param>
    /// <param name="issues">The replacement validation issues for the scope.</param>
    public void ReplaceScope(
        string definitionKey,
        LogicalPath scopePath,
        IReadOnlyList<PendingChange> changes,
        IReadOnlyList<ConfigurationValidationIssue> issues)
    {
        var scopedKeys = _pendingChanges.Keys
            .Where(key => string.Equals(key.DefinitionKey, definitionKey, StringComparison.Ordinal) && IsPrefix(scopePath, key.LogicalPath))
            .ToArray();
        var scopedIssueKeys = _validationIssues.Keys
            .Where(key => string.Equals(key.DefinitionKey, definitionKey, StringComparison.Ordinal) && IsPrefix(scopePath, key.LogicalPath))
            .ToArray();

        foreach (var key in scopedKeys)
        {
            _pendingChanges.Remove(key);
        }

        foreach (var key in scopedIssueKeys)
        {
            _validationIssues.Remove(key);
        }

        foreach (var change in changes.Where(change =>
                     string.Equals(change.DefinitionKey, definitionKey, StringComparison.Ordinal) && IsPrefix(scopePath, change.LogicalPath)))
        {
            _pendingChanges[Key(change.DefinitionKey, change.LogicalPath)] = change;
        }

        foreach (var issue in issues.Where(issue =>
                     string.Equals(issue.DefinitionKey, definitionKey, StringComparison.Ordinal) && IsPrefix(scopePath, issue.LogicalPath)))
        {
            _validationIssues[Key(issue.DefinitionKey, issue.LogicalPath)] = issue;
        }

        if (scopedKeys.Length > 0 || scopedIssueKeys.Length > 0 || changes.Count > 0 || issues.Count > 0)
        {
            NotifyChanged();
        }
    }

    /// <summary>Checks that an editor can preserve redacted drafts without splitting an overlapping parent edit.</summary>
    internal bool CanReplaceJsonEditorScope(string definitionKey, LogicalPath scopePath, IReadOnlyList<LogicalPath> redactedPaths)
    {
        bool IsHidden(LogicalPath path) => redactedPaths.Any(redacted => IsPrefix(redacted, path));
        bool IsAmbiguous(LogicalPath path) =>
            IsPrefix(path, scopePath) && !path.Equals(scopePath)
            || redactedPaths.Any(redacted => IsPrefix(path, redacted) && !IsHidden(path));

        if (_pendingChanges.Values.Any(change =>
                string.Equals(change.DefinitionKey, definitionKey, StringComparison.Ordinal)
                && (IsPrefix(scopePath, change.LogicalPath) || IsPrefix(change.LogicalPath, scopePath))
                && IsAmbiguous(change.LogicalPath)))
            return false;

        foreach (var issue in _validationIssues.Values.Where(issue =>
                     string.Equals(issue.DefinitionKey, definitionKey, StringComparison.Ordinal)))
        {
            var locations = issue.LogicalPaths.Append(issue.LogicalPath).Distinct().ToArray();
            if (!locations.Any(path => IsPrefix(scopePath, path) || IsPrefix(path, scopePath)))
                continue;
            if (locations.Any(IsAmbiguous) || locations.Any(IsHidden) && !locations.All(IsHidden))
                return false;
        }
        return true;
    }

    /// <summary>Replaces visible editor state while retaining current drafts and issues wholly inside redacted paths.</summary>
    internal bool TryReplaceJsonEditorScope(ConfigurationJsonEditResult result)
    {
        var draft = result.Draft;
        bool IsHidden(LogicalPath path) => result.RedactedPaths.Any(redacted => IsPrefix(redacted, path));
        bool TouchesHidden(LogicalPath path) => result.RedactedPaths.Any(redacted =>
            IsPrefix(redacted, path) || IsPrefix(path, redacted));
        if (!CanReplaceJsonEditorScope(draft.DefinitionKey, draft.ScopePath, result.RedactedPaths)
            || draft.Changes.Any(change => TouchesHidden(change.LogicalPath))
            || draft.ValidationIssues.Any(issue => issue.LogicalPaths.Append(issue.LogicalPath).Any(TouchesHidden)))
            return false;

        var changes = draft.Changes.Concat(GetScope(draft.DefinitionKey, draft.ScopePath)
            .Where(change => IsHidden(change.LogicalPath))).ToArray();
        var issues = draft.ValidationIssues.Concat(_validationIssues.Values.Where(issue =>
            string.Equals(issue.DefinitionKey, draft.DefinitionKey, StringComparison.Ordinal)
            && IsPrefix(draft.ScopePath, issue.LogicalPath) && IsHidden(issue.LogicalPath))).ToArray();
        ReplaceScope(draft.DefinitionKey, draft.ScopePath, changes, issues);
        return true;
    }

    /// <summary>
    /// Replaces the complete pending-change set while preserving validation issues.
    /// </summary>
    /// <param name="changes">The refreshed pending changes.</param>
    public void ReplacePending(IReadOnlyList<PendingChange> changes)
    {
        _pendingChanges.Clear();
        foreach (var change in changes)
        {
            _pendingChanges[Key(change.DefinitionKey, change.LogicalPath)] = change;
        }

        NotifyChanged();
    }

    /// <summary>
    /// Removes one pending change.
    /// </summary>
    /// <param name="definitionKey">The definition key.</param>
    /// <param name="path">The logical path.</param>
    public void Undo(string definitionKey, LogicalPath path)
    {
        var key = Key(definitionKey, path);
        var removedPending = _pendingChanges.Remove(key);
        var removedIssue = _validationIssues.Remove(key);
        if (removedPending || removedIssue)
        {
            NotifyChanged();
        }
    }

    /// <summary>
    /// Removes only the staged change for one path, preserving any validation issue recorded at the same path.
    /// </summary>
    /// <param name="definitionKey">The definition key.</param>
    /// <param name="path">The logical path.</param>
    public void UndoChange(string definitionKey, LogicalPath path)
    {
        if (_pendingChanges.Remove(Key(definitionKey, path)))
        {
            NotifyChanged();
        }
    }

    /// <summary>
    /// Removes all staged changes inside one definition path scope.
    /// </summary>
    /// <param name="definitionKey">The definition key that owns the scope.</param>
    /// <param name="scopePath">The root logical path of the scope.</param>
    public void UndoScope(string definitionKey, LogicalPath scopePath)
    {
        var scopedKeys = _pendingChanges.Keys
            .Where(key => string.Equals(key.DefinitionKey, definitionKey, StringComparison.Ordinal) && IsPrefix(scopePath, key.LogicalPath))
            .ToArray();
        var scopedIssueKeys = _validationIssues.Keys
            .Where(key => string.Equals(key.DefinitionKey, definitionKey, StringComparison.Ordinal) && IsPrefix(scopePath, key.LogicalPath))
            .ToArray();

        foreach (var key in scopedKeys)
        {
            _pendingChanges.Remove(key);
        }

        foreach (var key in scopedIssueKeys)
        {
            _validationIssues.Remove(key);
        }

        if (scopedKeys.Length > 0 || scopedIssueKeys.Length > 0)
        {
            NotifyChanged();
        }
    }

    /// <summary>
    /// Clears all pending changes.
    /// </summary>
    public void Clear()
    {
        if (_pendingChanges.Count == 0 && _validationIssues.Count == 0)
        {
            return;
        }

        _pendingChanges.Clear();
        _validationIssues.Clear();
        NotifyChanged();
    }

    private static (string DefinitionKey, LogicalPath LogicalPath) Key(string definitionKey, LogicalPath path)
    {
        return (definitionKey, path);
    }

    private static bool IsPrefix(LogicalPath ancestor, LogicalPath path)
    {
        if (ancestor.Depth > path.Depth)
        {
            return false;
        }

        for (var index = 0; index < ancestor.Depth; index++)
        {
            if (!ancestor.Segments[index].Equals(path.Segments[index]))
            {
                return false;
            }
        }

        return true;
    }

    private void NotifyChanged()
    {
        Changed?.Invoke();
    }
}
