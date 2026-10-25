# System information

Register `monica.AddSystemInfoUI()` inside the host's `builder.AddMonica(...)` callback to supply `/system-info`; its module declares the shell dependency. Follow [shell and access](shell-and-access.md) for host composition and operational access policy.

The page presents immutable host-local snapshot evidence: application identity, process/runtime details, host environment, listening addresses, and executable artifact metadata. Refresh captures a new snapshot manually. The one-second presentation clock advances process uptime and evidence age; it does not automatically resample working-set memory. The working-set reading includes the exact captured byte count. Missing artifact metadata remains absent rather than being inferred from a path or filename.

Page timestamps and the timezone fact use the [shared Clock display policy](time-display.md), not the server operating-system timezone. The timezone fact retains its current UTC offset; ordinary timestamps omit repeated offsets and fractional seconds by default.

Optional startup timing records a completed interval ending at the Generic Host's `ApplicationStarted` milestone. It does not query health checks or establish current dependency readiness. Use the [health-check page](../../monica-infra-observability/references/health-checks.md) for that decision. Actual process architecture and process/operating-system bitness are separate evidence. Artifact timestamp means physical artifact last-write time, not a verified build time.

Configured links retain their host-provided ordering, grouping, enabled state, target, and credential-copy actions. Inspecting a listening address shows its exact normalized address, transport, and captured timestamp; wildcard listening addresses are not assumed to be browser destinations.

The opt-in `EnableSelfRestartAction(...)` registration helper exposes a confirmation action that requests delayed graceful shutdown. An external supervisor owns starting the process again. Canceling the confirmation leaves the host running. Do not enable or exercise shutdown merely to inspect the visual interface.

Source: `Monica.UI/Modules/ModuleSystemInfoUI.cs`, `UISystemInfo/Models/SystemInfoSnapshot.cs`, `UISystemInfo/State/SystemInfoPageSession.cs`, and `Pages/UISystemInfoPage.razor`.
