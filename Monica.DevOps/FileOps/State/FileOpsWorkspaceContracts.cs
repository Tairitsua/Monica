using Monica.Core.Results;
using Monica.Core.Results.Abstractions;
using Monica.DevOps.FileOps.Models;

namespace Monica.DevOps.FileOps.State;

/// <summary>Describes admission or confirmation, independently of a backend outcome.</summary>
public enum FileOpsOperationDisposition { Completed, Cancelled, Rejected, Retired }

/// <summary>Identifies real evidence reads and commands for localized presentation.</summary>
public enum FileOpsOperationKind { LoadPolicy, Browse, ReadText, SaveText, CreateDirectory, DeleteEntry, DeleteSelection, Upload, Download, DownloadSelection, SavePolicy }

/// <summary>Available local sort keys; directories remain first in either direction.</summary>
public enum FileOpsSortField { Name, Type, Size, Modified }

/// <summary>Identifies the proposed action which would abandon a captured dirty draft.</summary>
public enum FileOpsDiscardReason { Navigate, OpenFile, Reload, CloseInspector, UploadReplacement, PolicyReplacement, Leave }

/// <summary>Detached outcome of an actual facade operation; a refresh cannot replace a command outcome.</summary>
public sealed record FileOpsOutcome(FileOpsOperationKind Kind, bool Succeeded, ResultStatus Status, string? Message)
{
    internal static FileOpsOutcome From(FileOpsOperationKind kind, IResultEnvelope result, bool hasData = true) =>
        new(kind, result.IsSuccess() && hasData, result.Status, result.Message);
}

/// <summary>Immutable identity of the dirty draft presented to a native discard confirmation.</summary>
public sealed record FileOpsDiscardRequest(FileOpsDiscardReason Reason, string FullPath, string? DestinationPath, long DraftRevision);

/// <summary>Captured parent supplied to a native create-directory name collector.</summary>
public sealed record FileOpsCreateContext(string ParentPath);

/// <summary>Identifies a failed text-preview attempt separately from any retained accepted editor target.</summary>
/// <remarks>Name is a display label derived from the requested path. Entry is null when no actual row
/// metadata was captured; do not infer sizes, dates or action capabilities in that case. The outcome is
/// the actual text-read result, not a claim that the file is editable or unavailable for download.</remarks>
public sealed record FileOpsInspectionAttempt(string FullPath, string Name, FileOpsEntrySummary? Entry, FileOpsOutcome Outcome);

/// <summary>Detached exact deletion targets and any inspected draft affected by their ancestry.</summary>
/// <remarks>The callback owns its entry copies. Mutating them cannot change the admitted backend targets.
/// A nullable recursive response cancels; either nonnull boolean affirms the exact captured targets.</remarks>
public sealed record FileOpsDeleteConfirmation(IReadOnlyList<FileOpsEntrySummary> Entries, bool AffectsDirtyDraft, string? InspectedPath);

/// <summary>Collects an explicit native discard decision; false preserves the captured draft.</summary>
public delegate Task<bool> FileOpsDiscardConfirmation(FileOpsDiscardRequest request, CancellationToken cancellationToken);

/// <summary>Collects a native directory name; null cancels without creating a directory.</summary>
public delegate Task<string?> FileOpsDirectoryNameCollector(FileOpsCreateContext context, CancellationToken cancellationToken);

/// <summary>Collects a native deletion decision; null cancels and false means nonrecursive deletion.</summary>
public delegate Task<bool?> FileOpsDeleteConfirmationCallback(FileOpsDeleteConfirmation context, CancellationToken cancellationToken);

/// <summary>Uses a borrowed descriptor until browser invocation completes; the controller disposes its stream.</summary>
/// <remarks>Do not dispose or transfer the descriptor stream. Own and drain any JS imports/invocations,
/// recheck retirement before starting JS after awaits, and use DotNetStreamReference with leaveOpen true.
/// Return true only after browser initiation; false indicates no initiation. Neither value acknowledges
/// completed browser persistence.</remarks>
public delegate Task<bool> FileOpsDownloadCallback(FileOpsDownloadDescriptor descriptor, CancellationToken cancellationToken);
