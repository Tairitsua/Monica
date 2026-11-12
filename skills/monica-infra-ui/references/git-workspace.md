# Git working-copy workspace

`AddGitUI()` contributes `/git-repositories` and `/git-credentials` to the Infrastructure
category. The first page inspects and maintains configured working copies; the second reads
credential registration metadata and resolver precedence. They share native route navigation,
direct links, and the host's existing shell access policy. The UI module requires Git,
Localization, and Shell UI; it does not add a separate authentication scheme.

```csharp
builder.AddMonica(monica =>
{
    monica.AddGit()
        .UseSyncTriggers(GitSyncTrigger.Manual)
        .AddRepository(
            id: "handbook",
            remoteUrl: builder.Configuration["Git:HandbookRemote"]
                ?? throw new InvalidOperationException("Git:HandbookRemote is required."),
            localPath: Path.Combine(builder.Environment.ContentRootPath,
                "managed-working-copies", "handbook"),
            branch: "main");
    monica.AddGitUI();
});
```

The registration methods are in `Monica.Modules`; `GitSyncTrigger` is in
`Monica.DevOps.Git.Models`. Use a dedicated disposable working-copy directory. Synchronization
and deletion can discard local-only content, so do not point maintenance actions at an ordinary
development checkout. The underlying module requires EventBus and HostedService through its
descriptor. `UseSyncTriggers` replaces the enabled set; the example selects Manual only. The
module's default set includes Startup, Webhook, and Manual. The facade and service enforce the
configured Manual policy; disabling it does not disable the independent delete-local action.

Repository filters combine selected runtime states with search and the independent local-changes
filter. They do not change the selected target or narrow Sync all. Refresh reads current disk
and runtime evidence rather than synchronizing the remote. A failed read retains accepted data
with a separate failure indication. The selected sheet preserves configured and resolved branch
and path, full commit, reported directory size, and actual error/message evidence.

Ready is a runtime state, not proof of a clean worktree, remote connectivity, or successful
authentication. Local availability describes disk evidence; during an operation it can mean
directory existence. Local changes are independent of Ready. Last successful sync is distinct
from snapshot receipt time and remains recorded after local deletion. A missing timestamp means
no successful sync is recorded in the current host runtime. Progress percentages belong to an
actual stage; they are not an overall batch percentage or an ETA.

Synchronize fetches or clones, forcibly checks out the target branch, hard-resets to the remote
tip, and removes untracked files. Its confirmation identifies the target and loss effect. Sync
all applies to the complete configured set and can return mixed successes and failures. Delete
local removes the resolved directory and its contents, including Git data and local-only files;
the registration and remote remain. Re-cloning cannot recover discarded local-only content.
Cancel, Escape, dismissal, or retiring a pending confirmation does not admit a mutation. Leaving
after an affirmative action stops UI publication and tracking while joining the actual operation;
the current facade does not offer page-lifetime cancellation of an admitted backend mutation.

`/git-credentials?credential=<escaped-id>` inspects an exact case-insensitive registration
reference. The page reconciles its native pagination and reveals the matching identity. Missing
references, empty registrations, filters, and metadata-read failures remain distinct. This route
does not read repository disk state or invoke credential resolution.

Credential metadata contains ID, authentication kind, user name, and registered token/password
presence. Presence is not an authentication result or a claim about externally supplied secrets.
Resolvers are listed in descending Order, then case-insensitive Resolver ID order; the first
resolver returning a credential wins. The ledger does not claim which resolver ran for a
particular reference. Implementation type names are namespace-qualified display names with
readable generic arguments, not serialized CLR identities.

UI text redacts configured token/password literals and common URL credential fields before
presentation. That bounded hygiene is not an arbitrary-secret detector. Keep credentials and
complete module options out of UI payloads, copied evidence, and screenshots. The page-created
state and interop belong to its visit; injected facades and repository services remain borrowed.
Component authoring and responsive review follow the
working-interface language in `$monica-ui-development` (`references/monica-workbench-design-language.md`).
