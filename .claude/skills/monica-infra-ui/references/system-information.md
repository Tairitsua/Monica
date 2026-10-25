# System information

Register `monica.AddSystemInfoUI()` inside the host's `builder.AddMonica(...)` callback to supply `/system-info`; its module declares the shell dependency. Follow [shell and access](shell-and-access.md) for host composition and operational access policy.

The page presents immutable host-local snapshot evidence: application identity, process/runtime details, host environment, listening addresses, and executable artifact metadata. Refresh captures a new snapshot manually. The one-second presentation clock advances process uptime and evidence age; it does not automatically resample working-set memory. The working-set reading includes the exact captured byte count. Missing artifact metadata remains absent rather than being inferred from a path or filename.

The Precision v3 composition puts application identity and refresh/restart actions above one signature surface: a live uptime display, a finding sentence, a process lifeline, and a contiguous four-fact strip. With startup tracking, the first fact is the monotonic application startup duration; without it, the fact shows the actual process start. Runtime, host and artifact evidence share the lower dossier, while configured links and bound addresses occupy the adjoining column. Narrow sheets reflow into one column.

The lifeline uses separate seconds and clock axes with an explicit scale break. The startup lane distinguishes process start from the application-owned timing origin and host-ready instant. The running lane ends at the presentation clock; the portion after capture is hatched as unobserved rather than represented as newly sampled evidence. A recent physical artifact timestamp can appear on the startup axis; older or missing timestamps stay in the artifact dossier. Pointer inspection and ArrowLeft/ArrowRight, Shift+Arrow, Home, End and Escape expose exact instants without changing the snapshot. Short startup intervals use separate labels so fast hosts remain readable. The browser observer is component-owned and stops callbacks before releasing its .NET target.

Page timestamps and the timezone fact use the [shared Clock display policy](time-display.md), not the server operating-system timezone. The timezone fact retains its current UTC offset; ordinary timestamps omit repeated offsets and fractional seconds by default.

Optional startup timing records a completed interval ending at the Generic Host's `ApplicationStarted` milestone. It does not query health checks or establish current dependency readiness. Use the [health-check page](../../monica-infra-observability/references/health-checks.md) for that decision. Actual process architecture and process/operating-system bitness are separate evidence. Artifact timestamp means physical artifact last-write time, not a verified build time.

Configured links retain their host-provided ordering, grouping, enabled state, target, and credential-copy actions. Inspecting a listening address shows its exact normalized address, transport, and captured timestamp; wildcard listening addresses are not assumed to be browser destinations.

Application identity, machine name, paths, build metadata and endpoint details provide explicit copy actions. Compact build labels copy the full metadata value; tooltip timestamps and HTML `datetime` values retain their complete source evidence.

The opt-in `EnableSelfRestartAction(...)` registration helper exposes a confirmation action that requests delayed graceful shutdown. An external supervisor owns starting the process again. Canceling the confirmation leaves the host running. Do not enable or exercise shutdown merely to inspect the visual interface.

Source: `Monica.UI/Modules/ModuleSystemInfoUI.cs`, `UISystemInfo/Models/SystemInfoSnapshot.cs`, `UISystemInfo/State/SystemInfoPageSession.cs`, and `Pages/UISystemInfoPage.razor`.
