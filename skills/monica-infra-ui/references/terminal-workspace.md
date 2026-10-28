# Terminal workspace

Use Terminal to execute a command in the Monica host's local process environment and inspect its retained output. This is a fresh shell for each command, not a PTY, remote connection, interactive shell, or stdin terminal.

## Compose the host

Register the underlying capability and UI through the host's existing Monica composition:

```csharp
builder.AddMonica(monica =>
{
    monica.AddTerminal(options =>
    {
        options.DefaultTimeoutSeconds = 120;
        options.MaxTimeoutSeconds = 900;
        options.MaxOutputLines = 2_000;
        options.MaxHistoryEntries = 100;
    });
    monica.AddTerminalUI();
});
```

These are framework option defaults, not evidence that a running host uses them. `AddTerminalUI` requires Terminal, localization and Shell through its module dependencies. In a complete web host retain the ordinary `app.UseMonica()` and `app.MapMonica()` lifecycle. See [Shell and access](shell-and-access.md) for shell composition and page policy configuration.

The single public route is `/ui/terminal`, registered under Infrastructure. It has no built-in page-specific access policy. The host owns authentication and authorization for local command execution. Browser owner keys partition in-memory sessions; they do not authenticate a user or grant execution permission. A session working directory is a command target, not a sandbox. FileOps path rules do not constrain Terminal.

The environment detector, command runner and session service are host singletons. The facade is scoped and borrowed by a page. The session singleton owns actual commands and async shutdown; a routed visit owns only its reads, short admission calls, draft, polling and presentation work.

## Work in one command context

The target band identifies the selected session and its exact directory. During execution the captured starting directory belongs to the current command; the session directory and a previous result's directory are separate evidence. Session and host details distinguish the host default directory from the application base directory, shell executable and arguments, OS description, architecture and runtime identifier. A runtime identifier is not a loaded .NET patch version.

History and Session & host are contextual views of the same task rather than another route or nested tab rail. Wide layouts use an inline context panel; narrow layouts use [MoSideSheet](visual-components.md#modal-evidence-side-sheets) for native modal focus, dismissal and viewport placement. Both render the same captured context; command reuse and visit retirement remain with the terminal owner. History shows the latest twelve retained command strings in reverse order. Reuse copies a command into the shared visit draft and never executes it; duplicate strings remain separate retained entries. No per-history timestamps, outcomes or durations are stored.

Output follows new lines until the reader scrolls upward. Latest or scrolling back to the end resumes following. Appends and layout changes preserve that choice; the page does not infer user intent from every scroll event. Browser input can arrive after the browser has already moved the scroll position, so upward intent must still pause a scrollable transcript when the delivered position is already at the top.

New sessions inherit the selected directory when the UI supplies it. Closing the final visible session through the page creates a replacement. New, select, close, clear and history reuse are admitted only while the selected session is idle and no conflicting page operation is pending. These are UI admission rules; the service separately permits independent sessions and owners to run commands.

The draft is shared across selection within one visit. Start freezes the owner, session and trimmed command before awaiting presentation. A rejected start retains the draft. Acceptance clears only the submitted draft generation, so a queued newer edit survives. Commands trim surrounding whitespace and preserve interior text; the module does not promise an unchanged copy of surrounding whitespace.

## Execution and retained evidence

On Windows the detector prefers `powershell.exe` with `-NoLogo -NoProfile -ExecutionPolicy Bypass -Command`, falling back to `cmd /C`. Other platforms select the detected shell. Each submitted shell command starts a new process; shell variables and ordinary shell-local state do not persist to the next command. Output is captured as plaintext line frames with input, stdout, stderr and system stream identity. It is not an ANSI terminal emulator, and there is no interactive input stream.

The session service handles its dedicated `cd` command without starting a shell. Relative, quoted and supported home paths update the session directory for later commands. An invalid directory is an ordinary nonzero command result. Do not treat compound shell expressions beginning with `cd` as a supported shell parser or imply Windows `cd /d` semantics.

Timeout overrides are clamped to at least one second and the configured maximum. Timeout results have `TimedOut=true`, `Canceled=true` and exit code `-1`; explicit cancellation has `Canceled=true` and exit code `-1`. Neither rolls back effects already performed by a command. A successful facade envelope can contain a nonzero process exit, cancellation or timeout; inspect the result itself.

Retained session output includes submitted input, while a command result contains captured command output. Session and per-result retention use the configured line limit and explicit truncation frames; zero retention does not disable execution. This is a line-retention policy, not a bound on line length, bytes, transient capture or the async observer queue. Streams retain the order observed by the runner, not a guaranteed OS-level merged stdout/stderr chronology. Blank and whitespace-only lines remain evidence. Stderr presence alone does not prove command failure.

Clear removes retained output, latest result and failed-attempt evidence while preserving history and the session directory. A later active or failed command does not erase the previous completed result. Producer/start/observer failure is separate from a completed result and includes the failed command identity; the UI does not invent an exit code for it.

## Admission, cancellation and lifetime

`TerminalFacade.StartCommandAsync(owner, sessionId, request)` returns `Res<TerminalCommandAcceptance>`. Its command ID and detached session capture prove admission only. A fast command may already have completed in that capture; a later process-start failure is visible in `LastExecutionFailure`. The page observes immutable snapshots through a single serial loop, including while idle so another same-owner tab's active command can become visible.

Leaving the route retires the visit without canceling the host-owned command or waiting for its duration. Returning reads actual running state and can request cancellation. A visit requests command-field focus only for a command it admitted, with the matching session and eligible draft generation; reentry or another visit's completion does not take focus.

`CancelCommandAsync(owner, sessionId, expectedCommandId)` targets that captured command, never whichever command happens to run later. A stale identity fails. Request completion joins cancellation callbacks and returns current evidence; it does not establish process exit. `ActiveCommand.CancellationRequested` remains distinct from completion. Running state remains true through associated process exit, stream EOF and async output-observer drainage.

The runner requests process-tree termination on cancel or timeout, then awaits the associated process and output producers with an uncanceled cleanup path. Associated-process exit does not prove that every descendant terminated. A failed kill or unfinished observer cannot be reported as completed cancellation merely to release session admission. Duration measures the runner operation through output/observer drainage, not CPU usage or a live progress estimate.

The singleton service retains active producers after a direct caller closes a running session. `CloseSessionAsync` removes that session and requests cancellation; its receipt is not a drained-process receipt. Missing close is idempotent. Host-owned async disposal stops new admission, requests cancellation and joins actual producers, including those removed from the visible catalog. Host shutdown and route departure are separate lifetimes.

## Direct consumers and migration

Read/list/create APIs now return detached `TerminalSessionSnapshot` evidence instead of the former mutable live `TerminalSession`. Environment reads return `TerminalEnvironmentSnapshot`. Collections and output-line records are immutable; retaining or copying a snapshot cannot mutate server identity, working directory, transcript or results. Use service operations for changes.

The cancel API now requires `expectedCommandId` and returns `Res<TerminalSessionSnapshot>` at the facade. Capture `session.ActiveCommand.CommandId` before awaiting any confirmation or rendering work. Service callers replace synchronous close with `CloseSessionAsync` and await its removal/request receipt.

`ExecuteCommandAsync` retains its direct await-to-completion API over the same producer as Start. Its caller token explicitly cancels execution; do not pass a route-lifetime token when continued execution is intended. Its optional output observer is invoked serially and awaited. Synchronous and late observer faults remain observable after process/output cleanup; callbacks must not await their own command completion. The service stores immutable results separately from the returned mutable direct-completion DTO, so subsequent caller mutation cannot change retained evidence.

These model and cancel/close signature changes require rebuilding consumers. The page uses Start and observation rather than holding an execution callback into a retired renderer. `TerminalOutputLine` keeps object-initializer construction but its properties are now `init` values.
