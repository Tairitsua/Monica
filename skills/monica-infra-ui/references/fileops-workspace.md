# FileOps workspace and host policy

Use FileOps for bounded emergency filesystem access in a host. Its UI composes the real FileOps facade; browser capabilities are observations, while each service operation checks the current runtime policy. Register explicit existing roots and review the host's page and API authorization separately.

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

The host creates those directories before registration. `AddFileOpsUI` requires FileOps, contributes `FileOpsResource` localization and registers the Files page and hidden policy compatibility entry without a default page-specific access-policy type. It offers no separate policy option and duplicate routes cannot overwrite those registrations. A host can instead compose manual page registrations or a custom page catalog with its existing `AccessPolicyType` authority. This does not automatically apply `OperationalPageAccessOption`; FileOps introduces no separate authentication or role model. Host page access and underlying API authorization remain separate concerns. Full web hosts also use `app.UseMonica()` and `app.MapMonica()`.

## Files and policy share one visit

`/ui/file-ops` owns one file manager: Up immediately precedes the root selector, directory commands stay with the current location above the inventory/editor region, and text inspection is a continuous adjacent or compact editor. Runtime policy opens a native dialog over that same Files controller. Opening or dismissing the dialog preserves the accepted directory, ordering, checked paths, inspected identity and text draft. There is no visible policy peer rail or second shell destination. The hidden `/ui/file-ops/policy` compatibility entry replace-navigates to `/ui/file-ops?policy=edit`; after initialization and interactivity, that query opens the same dialog once. It carries no filesystem path or draft. Automatic entry focuses the connected Runtime policy trigger before native modal admission so the native trap has a return target.

The outer policy dialog disables automatic provider dismissal on navigation. Same-path query changes keep its draft in the current visit; genuine departure obtains both policy and file-draft consent before closing. Forced retirement closes and drains the separate outer and inner native leases.

The circuit's passive return context contains directory, ordering, checked paths and a clean inspected identity only. Leaving and returning reloads policy and revalidates those identities; it does not resurrect a controller, admitted task or unsaved text.

Browsing and text inspection remain one continuous task. Checked batch paths are independent of the inspected file. Select-all and client sorting operate on the accepted displayed subset, not an undisclosed complete directory. The service returns `IsTruncated` and `EntryLimit`, with no total count or paging contract. A file absent from that bounded subset is not necessarily deleted.

The inventory changes between a native table and compact list at an observed inventory width of 800 CSS pixels. An actual inspected target uses the split only when the work owner is at least 1,080 pixels wide with 520 pixels of usable opening after real controls. Otherwise the file picker can collapse above the continuous inspector. A sparse directory has natural height; the list's cap subtracts actual toolbar, notices, footer and page space. Only an active inspector split uses the smaller list cap. These are owner measurements, not browser-width or total-row-count claims.

Keep native checkbox ownership stable while updating check state. MudTable keys rows by item reference, so the inventory retains one presentation object per exact path while adopting fresh fields from controller snapshots. This is presentation continuity, not independent capability authority. Checking never inspects or reveals a file. Clear removes its contextual band and deliberately returns focus to the visible select-all input or usable inventory fallback after render, guarded by the visit, focus version and accepted directory. Preserve a 44-pixel native target and its focus clearance separately from selection-column width or SVG inset.

The file editor binds a full target identity, encoding, accepted disk metadata and local draft. Replacing a dirty draft requires an explicit native discard decision. Cancel preserves the draft and starts no mutation. Refreshing the current directory preserves an unrelated draft; deleting an unrelated entry does not clear the inspector. Route-owned work is canceled where supported and actually drained before retirement; injected facades and service scopes remain borrowed.

Save replaces the complete supplied text using the requested encoding. There is no expected-version check, atomic replacement, backup, history or rollback. The UI rereads the exact target after a completed save to obtain fresh metadata. A later read failure does not undo persistence or erase a newer draft; command outcome and refresh failure remain separate.

## Runtime policy is host-wide and in memory

The singleton runtime configuration store returns detached normalized snapshots. Saving replaces the host-wide in-memory policy; it is neither persisted nor user-specific. Opening, editing or canceling a UI draft does not change it. Restart restores the host's configured starting policy.

The dialog borrows the existing Files controller's `SavePolicyAsync(capturedDraft, discardCallback)` rather than constructing a policy-only controller. The admitted operation captures the file identity/revision and obtains `PolicyReplacement` consent if the new policy invalidates that dirty text. Refusal starts no policy write. Confirmed policy can clear only the still-unchanged invalidated inspection; newer text survives with stale evidence rather than being relocated by an older operation.

Apply keeps the dialog open. The form owns raw multiline values, a detached draft, an accepted fingerprint and a monotonically increasing edit revision. Authoritative normalized values replace fields only if the captured revision is current; otherwise the accepted baseline updates while newer unapplied edits remain. Failed saves or readbacks retain the actual form. Policy reads/reloads use the borrowed facade without reinitializing or disposing the Files controller. Native multiline fields use `InputSizing.Auto`, two minimum lines and a deliberate 12-line visible cap; overflow beyond it remains accessible. The installed native sizing reacts to loaded text, input and window resizing; owner-only width or font-metric changes require verification rather than a claimed custom observer.

Admission completion is not persistence success. The UI receipt requires a new successful SavePolicy command identity, so refusal cannot adopt an older success. A following failed LoadPolicy leaves normalized policy unconfirmed. A subsequent Browse/ReadText failure follows confirmed policy readback and is presented separately; it does not undo the applied policy. No compensating write restores an older snapshot.

An empty or entirely invalid allowed-root list normalizes to the working directory, executable base directory and process temporary directory. It does **not** deny all access. Supply explicit roots when a bounded scope is intended. An invalid, unavailable or out-of-scope startup path falls back to the first normalized root. Read-only paths outside allowed roots are discarded during normalization.

Defaults and normalization are owned by `FileOpsRuntimeConfig`:

| Setting | Default | Normalization |
| --- | --- | --- |
| Write operations | Enabled | Current policy remains authoritative |
| Delete operations | Disabled | Independent of write enablement |
| Hidden entries | Hidden | Dot-prefixed names and hidden attributes are excluded |
| Text size | 512 KiB | At least 4 KiB |
| Upload size | 20 MiB | At least 64 KiB |
| Per-file download size | 100 MiB | At least the normalized upload size |
| Displayed directory entries | 500 | Between 50 and 5,000 |

Read-only means configured path ancestry, rather than an OS permission probe. Allowed-root and read-only checks use lexical paths and OS-specific path identity. They do not establish physical symlink or junction containment. Allowed roots themselves cannot be deleted. Preview accepts decoded bounded text independently of the editable-extension allowlist; saving additionally requires an editable extension and writable target.

## Transfers and destructive results

The UI uploads one file and retains same-name replacement (`overwrite: true`). The final target's read-only policy is checked before it is opened, including an existing protected file under a writable parent. Successful upload metadata is sampled after destination stream closure. The service checks declared upload length; copy failure or cancellation can leave partial content. No rollback, resume or browser progress contract exists.

Single downloads return caller-owned streams. ZIP downloads recursively include visible selected descendants, preserve empty directories and disambiguate duplicate archive names. Policy limits each file; there is no total archive-size limit or recursive entry-count limit. The successful temporary archive stream uses delete-on-close ownership. Dispose every successful descriptor stream even if its UI visit retired before the producer returned.

Browser transfer interop retains the descriptor stream until `arrayBuffer()` finishes, then releases its stream reference and the caller closes the descriptor. A successful UI outcome means download initiation, not confirmation that the browser persisted the file.

Batch deletion uses one policy snapshot, deduplicates target paths in their original order, and deletes sequentially. It stops at the first exception. Earlier deletions remain applied, while a failed facade result carries no partial-success list. Refresh the captured directory after an attempted mutation, including failure, without inventing an exact partial count. Native destructive confirmation displays the full frozen targets; selection changes cannot retarget it.

## Developer ownership

`IFileOpsWorkspaceService` and `IFileOpsTransferService` expose the actual scoped filesystem and transfer boundaries. The module aliases them to its existing scoped concrete services, preserving one instance per scope. `FileOpsFacade` borrows these interfaces and projects exceptions into `Result` envelopes; controlled behavior tests can replace these external boundaries while retaining the real facade. Direct facade construction now supplies those interface contracts; existing concrete implementations implement them.

The workspace controller belongs to one routed visit. Native dialog ownership must observe late `ShowAsync` references, close them on retirement and join the actual result and close dispatch. Retire dialog owners before waiting for work that awaits their result. Download interop owns its imported module and `DotNetStreamReference`, while the descriptor's stream belongs to the invoking operation. Cancellation alone is not drainage; once a UI mutation starts, the visit joins it without promising cancellation or rollback.

Reserve the Files visit's New folder opener before Create admission publishes Busy. Keep only that native button focusable through its owned name-dialog/result chain, expose temporary unavailability with `aria-disabled`, and reject duplicate activation in both inventory and visit handlers. Other busy operations and missing Create capability retain native disabling. Release the reservation after the chain drains; route retirement still closes/drains native ownership and rejects late name adoption. MudBlazor restores focus on Cancel and Escape, without a second JavaScript focus owner.

The policy dialog has a separate outer native lease owner; the Files visit's existing inner owner handles policy-draft and captured-file decisions. Disable the outer native CloseButton and CloseOnEscapeKey bypasses. Custom Close, backdrop and `MudDialog.OnKeyUp` Escape share one awaited dismissal gate. In the installed Mud version, OnKeyDown does not forward Escape. Dirty policy dismissal captures a form revision, and a newer edit invalidates that consent. Apply/Reload/decision overlap rejects dismissal and duplicate writes; native Apply and each active decision opener remain connected and focusable with truthful `aria-disabled` guards. Do not use CancelAll or replace native focus trapping.

Genuine route departure collects policy consent without clearing it, then runs the controller's file-leave decision, then rechecks consent before closing. Refusing either decision keeps both drafts. Query-only changes are the same Files visit. Forced retirement rejects new admissions, initiates both dialog owners' closure before draining form/controller work, cancels form-owned reads, and joins late references and uncancelled admitted writes. The form never owns the controller or facade lifetime.

`FileOpsPolicyEditor` is now the native dialog form and requires a borrowed `Controller`, an `Apply` receipt delegate, an inner `ConfirmDiscard` delegate and an `Attach` owner callback. Direct consumers of the former parameterless standalone component must migrate to this owned composition; the old route alias remains available. `FileOpsPolicyApplyReceipt` is detached UI evidence, not a filesystem/service API or a host policy version token.

Direct `FileOpsInventory` or `FileOpsLocation` consumers move `CreateActive`, `Create`, `Upload` and `UploadName` to `FileOpsDirectoryActions`, supplying the same borrowed controller and visit-owned callbacks. Compose those directory commands above the inventory/editor region; the inventory retains sorting, checking and batch actions. The extraction introduces no second controller or operation owner.

`FileOpsFamilyHeader` no longer accepts `IsPolicy` or renders sibling-route navigation. Prefer the composed `FileOpsWorkspace`; direct header composition supplies `OpenPolicy`, `Busy` and `PolicyOpen` from the same visit owner so the native dialog opener survives its result chain.

Same-page policy editing still enforces the registered `/ui/file-ops/policy` authority through the circuit-scoped `PageAccessEvaluator.IsAuthorizedAsync(route, token)`. Files authorization alone is insufficient when a host protects policy separately. The visit checks before opening and independently before controller mutation admission; the form checks before policy reads, reload decisions and Apply. Missing catalog entries, missing/failed policies and denied access fail closed with a localized state and no new policy operation. Explicit reload can re-evaluate restored access without replacing a retained draft prematurely. Owned access/read evaluations are canceled and drained on retirement; an already-admitted filesystem write keeps the existing uncancelled ownership contract. The hidden compatibility route retains router enforcement. These checks do not redefine the shell's global operational gate or authorize underlying filesystem APIs.
