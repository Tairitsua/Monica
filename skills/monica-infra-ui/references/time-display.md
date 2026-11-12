# Page time display

Monica pages format human-readable timestamps through the shell-owned `MoTimeFormatter`
(`Monica.UI.Shared.Services`). `ModuleShellUI` requires `ModuleClock`; its
`LocalTimeZone` selects the display timezone independently of the server and browser.
An unset Clock timezone uses UTC. Configure the deployment timezone explicitly when
operators work in local wall time:

```csharp
builder.AddMonica(monica =>
{
    monica.AddClock(options => options.LocalTimeZone = CommonTimeZone.China);
    monica.AddUIShell(options =>
    {
        options.TimeDisplay.DateTimeFormat = "yyyy-MM-dd HH:mm:ss";
        options.TimeDisplay.DateFormat = "yyyy-MM-dd";
        options.TimeDisplay.TimeFormat = "HH:mm:ss";
    });
});
```

Import `Monica.Core.Clock` for `CommonTimeZone` and `Monica.Modules` for registration.
These are the default formats: ordinary rows omit fractional seconds, timezone
suffixes and duplicate UTC values. Hosts can override all three formats before
startup, including adding precision when their operational task requires it.
The System information timezone fact identifies the effective Clock zone and its
current offset once, alongside the host evidence.

Inject `MoTimeFormatter` into pages or their display owners. Use `Format` for a full
timestamp, `FormatTime` or `FormatDate` for intentionally compact contexts, and
`FormatRange` for a range that omits a repeated same-day end date. A nullable value
renders an em dash. All conversions use the target instant's timezone rules,
including daylight saving; the current culture is read on every format call.

`DateTimeOffset` always represents an instant. UTC or Local `DateTime` values are
also converted as instants. Unspecified `DateTime` is already deployment wall time
(for example a timezone-free business audit field); it is not converted again.
For an explicitly requested offset format, the Clock zone supplies its offset;
the wall time must then be representable as a DateTimeOffset. Offset-free default
formats also preserve boundary/sentinel wall dates without constructing an instant.
A source contract that returns UTC with a lost Kind must mark it UTC at the display
boundary. Do not infer UTC solely from a timestamp's current numeric value.

This contract changes presentation, not storage or scheduling. Keep UTC wire
values, exports, log text and HTML `datetime` attributes exact. Do not rewrite
date-only values or elapsed durations as timestamps. Chart axes may pass an
explicit precision-sensitive format after conversion; their point geometry
continues to use actual instants. A chart needs an accepted date/range context
when labels show only the time of day.

Cron inputs, schedule previews and scheduler-calendar presets retain their declared
schedule timezone because that timezone defines the user's action. Clearly label
it once, and use `FormatWallTime` on an explicitly projected schedule instant.
Ordinary scheduler observation/execution timestamps still use Clock. Fixed
analytics buckets retain their actual UTC interval boundaries even though their
labels render through Clock. Ranges spanning a timezone offset change include
endpoint offsets to distinguish repeated wall hours; ordinary ranges do not.
Exact ISO endpoints remain in `datetime` metadata.

Verify a configured zone different from the server zone, a conversion crossing
midnight, a DST zone and a non-default format. Inspect the ordinary table and its
phone layout; removing text must not remove exact identities or query semantics.

## Prototype time display

Every new or revised prototype page follows this same display contract. Model the
target deployment's `ModuleClockOption.LocalTimeZone` explicitly in the fixture and
use a shared prototype formatter that represents `MoTimeFormatter`. Do not choose
the browser or server timezone, hard-code a different zone in each page, or render
an ISO UTC string as the ordinary timestamp label. If host configuration is not
available, declare the demonstration timezone in the fixture and design note.
That choice is a prototype assumption; the framework's unset Clock default remains
UTC.

Ordinary full timestamps use `yyyy-MM-dd HH:mm:ss`, matching the default shell
format, without a repeated `UTC`, `Z`, or numeric offset suffix. Honor an explicitly
configured display-format override when the prototype models one. Compact labels
may use the configured date/time formats when their date context is visible.
Identify the effective deployment timezone once in the appropriate context when
operators need it, rather than repeating it in every row or evidence value.

Keep fixture instants and HTML `datetime` metadata exact; convert only their
human-readable labels. For example, with `CommonTimeZone.China`, the instant
`2026-10-28T16:05:09Z` displays as `2026-10-29 00:05:09`. Do not strip the source
offset and reinterpret the remaining digits as deployment wall time. Schedule
inputs and previews retain their declared schedule timezone as described above;
elapsed durations, chart geometry and UTC query boundaries are unchanged.

Verify the prototype's configured-zone conversion across midnight and with a
browser timezone different from that zone. Check full timestamps in tables,
evidence sheets, dialogs and capture labels in both UI languages. For DST-sensitive
views, also retain the offset-change distinction required by the range contract.

## Migrating custom display owners

Replace `SystemInfoDisplayFormatter.FormatDateTime` / `FormatTime` and removed
page-local timestamp `Display` properties with injected `MoTimeFormatter` calls.
`SystemInfoFacade` and `RAGManagePageState` now receive that service through their
constructors; manual construction must supply the host's formatter as well.
Git and EventBus/Observable presentation helpers that still accept a formatter
must receive the same host-owned instance, not a static service locator.
The shell registration supplies both the formatter and its Clock dependency.
Custom render/test owners that bypass the shell must register the formatter and
both `ModuleShellUIOption` and `ModuleClockOption` options explicitly.
