# Utilities workspaces

`AddUtilitiesUI()` contributes two Infrastructure destinations to the shell: connectivity at
`/ui/utilities` and text transformation at `/ui/utilities/text`. It requires the Utilities,
Localization, and Shell UI modules through its module descriptor. Existing shell access policy
applies to both pages; the UI module does not introduce a separate authentication scheme.

```csharp
builder.AddMonica(monica =>
{
    monica.AddUtilitiesUI();
    monica.AddUtilities(options =>
    {
        options.DefaultProbeTimeoutMilliseconds = 3000;
        options.MaximumProbeTimeoutMilliseconds = 30000;
        options.DefaultHttpRequestPath = "/";
    });
});
```

The explicit `AddUtilities` call configures the dependency already required by the UI module.
The shown option values are the defaults. The connectivity field uses the configured effective
maximum, with a minimum bound of 250 ms. The service normalizes the submitted timeout and applies
it to individual network operations; total duration can be longer. Probes run from the application
host, not from the browser. A host/IP input is distinct from the separate HTTP path.

Connectivity supports ICMP echo, TCP connection, and HTTP/HTTPS GET after the TCP check. Address
resolution evidence and each probe outcome remain separate. Redirects are not followed. Every
received HTTP response, including 404 or 503, establishes application contact; its response code
does not redefine transport success or establish application health. An expected network failure
is structured result data. Invalid input or an execution failure can instead produce a failed
result envelope. Absent latency remains unavailable rather than becoming zero.

HTTPS certificate validation is enabled by default. The explicit bypass checkbox applies to that
request only and resets when leaving HTTPS or resetting the form. Completed evidence uses the
accepted request snapshot for this option, because the probe result does not carry a certificate
validation field. It reports requested policy, not a certificate inspection or a trust verdict.

Text operations decode JSON-style escapes, format JSON, or minify JSON. The real transform service
retains readable Unicode, bounds escape decoding and nested JSON-string unwrapping to five layers,
and trims input for format/minify. Reported lengths are .NET UTF-16 code units. Unchanged decoding
is a valid completion. A failed attempt preserves the previous successful output and its metadata;
the page identifies the attempted operation and the retained result's original input separately.
Editing input does not recompute or validate its prior output. Reusing output copies the complete
accepted output into the draft, clears the latest error, and does not execute a new transformation.
Clear removes both editors, accepted metadata, and attempt provenance.

The two scoped UI states belong to the Blazor circuit. Panels borrow them, so working drafts and
accepted results survive sibling navigation. A panel owns its in-flight invocation and lifetime
cancellation; route departure cancels and drains that invocation without disposing borrowed state
or facades. Canceled or reset work cannot publish a late result or error. A direct reload creates
the usual new circuit; these drafts are not durable browser storage or a probe history.

Use native sibling links for direct load, browser history, and shell search. Component authoring
and responsive evidence review follow the working-interface language in `$monica-ui-development` (`references/monica-workbench-design-language.md`).
