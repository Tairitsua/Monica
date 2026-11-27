# FileOps workspace and host policy

FileOps provides bounded emergency filesystem access. The UI reads and mutates through the real FileOps facade. Browser capability marks are observations and each operation checks current host policy. Create explicit allowed directories before registration.

```csharp
builder.AddMonica(monica =>
{
    monica.AddFileOps(options => options.RuntimeConfig = new()
    {
        AllowedRoots = [dataRoot],
        StartupPath = dataRoot,
        ReadOnlyPaths = [readOnlyDirectory],
        AllowWriteOperations = true,
        AllowDeleteOperations = false
    });
    monica.AddFileOpsUI();
});
```

`AddFileOpsUI` requires FileOps, contributes `FileOpsResource` localization and registers both Files routes. Web hosts use `app.UseMonica()` and `app.MapMonica()`.

Hosts can set `ModuleFileOpsUIOption.PolicyAccessPolicyType` to a registered, circuit-resolvable `IPageAccessPolicy`. The default is null and adds no per-page restriction. The shell and the policy editor enforce the configured policy, including before policy reads and Apply. A missing or failed policy fails closed. If Files itself is accessible, direct policy denial offers a link back to the workspace. The host owns Files route authorization and HTTP API authorization independently.

## Files and policy share one visit

`/ui/file-ops` is the directory command bar, capability breakdown and inventory. `/ui/file-ops/policy` is its independent peer with a normalization preview and Apply. The legacy `/ui/file-ops?policy=edit` link redirects to the policy route. Sibling navigation retains the directory, local view, checked files, inspector and unsaved drafts inside one circuit. Leaving the route family or ending the circuit retires the visit after any required discard decisions.

The Files URL accepts `path`, `file`, `cap`, `q`, `sort` and `dir`. `cap` accepts `dir`, `editable`, `preview`, `binary` or `blocked`. `sort` accepts `Name`, `Type`, `Size` or `Modified`, with directories first in either direction. A filter edit replaces its history entry. Directory and inspector selections create history entries. Absolute directory and file paths in those links can appear in browser history and request logs. Checked batch paths and draft contents stay in the circuit. The inspector rechecks any file link against current policy.

Capability counts and local search operate on the accepted displayed subset. The service limits returned entries to `EntryLimit`, provides `IsTruncated` and does not provide a total count or paging. The tape leaves the undisclosed remainder visibly unmeasured. Select-all applies to the filtered rows currently shown. Checks remain on other accepted rows when the filter changes. Clear returns focus to the visible select-all control or the search field.

The service examines a bounded file prefix for the displayed subset to report text, binary or oversized evidence without loading file contents into the inventory. A file can change between observation and inspection. Read-only marks describe the configured policy, while a failed preview or transfer reports the actual result.

The right-edge inspector uses `MoSideSheet`. It retains disk metadata, exposes UTF-8 or the file's detected encoding and provides a line-numbered editor, in-file search and draft byte budget against the actual save limit. Dirty drafts require a discard decision before replacement or dismissal. Refresh retains a draft and shows disk divergence or stale metadata independently. Save replaces the complete supplied text using its encoding and rereads the target. Save has no expected-version check, atomic replacement, backup, history or rollback. A later read failure leaves successful persistence and retained evidence visible. Download streams disk bytes.

## Runtime policy is host-wide and in memory

The singleton runtime configuration store returns detached normalized snapshots. Apply replaces the host-wide in-memory policy. Restart restores the configured startup policy. Editing, previewing or discarding a draft leaves the host policy intact.

Locations, gates, limits and editable extensions form one draft. The editor displays the current normalized roots or the proposed result, including startup fallback, read-only paths outside all roots and effects on the open dirty file. It uses the same host normalization rules as Apply. Applying a policy that invalidates a dirty file requires consent on the exact file and draft revision. Cancel preserves both drafts. A successful command followed by a failed policy read requires a reload to confirm effective values. A failed browse or text reread is reported separately from confirmed policy.

An empty or entirely invalid allowed-root list falls back to the working directory, executable base directory and process temporary directory. Explicit existing roots keep the intended scope bounded. Trailing directory separators are removed before path deduplication, except for filesystem roots. An invalid, unavailable or out-of-scope startup path falls back to the first normalized root. Read-only paths outside allowed roots are discarded.

| Setting | Default | Normalization |
| --- | --- | --- |
| Write operations | Enabled | Current policy remains authoritative |
| Delete operations | Disabled | Independent of write enablement |
| Hidden entries | Hidden | Dot-prefixed names and hidden attributes are excluded |
| Text size | 512 KiB | At least 4 KiB |
| Upload size | 20 MiB | At least 64 KiB |
| Per-file download size | 100 MiB | At least the normalized upload size |
| Displayed directory entries | 500 | Between 50 and 5,000 |

Path and read-only checks use lexical paths and OS-specific identity. They do not establish physical symlink or junction containment. Allowed roots themselves cannot be deleted. Preview accepts bounded decoded text independently of the editable-extension allowlist. Saving requires an allowed extension and a writable target.

## Transfers and destructive results

The UI uploads one file at a time. Confirming an upload replaces an existing same-name file. Current policy checks the final target and declared length. Copy failure or cancellation can leave partial content. Successful metadata is sampled after the destination stream closes.

Single downloads return caller-owned streams. ZIP downloads include visible descendants, preserve empty folders and disambiguate duplicate archive names. Policy limits individual files. The temporary archive is removed when the stream closes. The browser retains the descriptor through interop and a successful UI outcome confirms initiation.

Deletion confirms the exact frozen targets and indicates whether an open draft is affected. A folder can include its contents when recursive deletion is selected. Batch deletion uses one policy snapshot, deduplicates targets in original order and stops at the first failure. Earlier deletions remain applied. A failed facade result carries no partial-success list, so the workbench refreshes directory evidence after the attempt.

## Developer ownership

`IFileOpsWorkspaceService` and `IFileOpsTransferService` are scoped filesystem boundaries. The module aliases its concrete scoped services to those interfaces. Controlled tests can replace these boundaries and keep the actual facade.

`FileOpsNavigationState` owns the controller and drafts for the Files family. The composed `FileOpsWorkspace` is the entry point for the route family. The two routes borrow the controller and own their dialog and interop sessions. Retire dialog owners before draining work that awaits their results. Mutation admission freezes the target and draft revision. A started mutation is drained even when the route retires. The browser transfer session owns the JS stream reference and the invoking operation owns the descriptor stream.
