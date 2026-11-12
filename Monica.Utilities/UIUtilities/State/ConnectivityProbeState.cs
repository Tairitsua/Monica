using Microsoft.Extensions.Options;
using Monica.Core.Results;
using Monica.Modules;
using Monica.Utilities.Connectivity.Facades;
using Monica.Utilities.Connectivity.Models;

namespace Monica.Utilities.UIUtilities.State;

/// <summary>
/// Retains connectivity drafts and completed evidence for the circuit across tool visits.
/// Each admitted request borrows its caller's lifetime token; canceling that visit cannot publish late evidence.
/// </summary>
public sealed class ConnectivityProbeState(
    ConnectivityProbeFacade connectivityProbeFacade,
    IOptions<ModuleUtilitiesOption> moduleOptions)
{
    private readonly ModuleUtilitiesOption _moduleOption = moduleOptions.Value;
    private CancellationTokenSource? _activeCancellation;
    private long _revision;

    /// <summary>
    /// Notifies current panels of operation changes. Subscribers must retire their renderer callbacks on departure.
    /// Notifications are awaited before the operation completes.
    /// </summary>
    public event Func<Task>? Changed;

    /// <summary>Gets the current request/reset revision used to reject superseded UI continuations.</summary>
    public long Revision => _revision;

    /// <summary>Gets the immutable request currently being executed, or null when no request is active.</summary>
    public ConnectivityProbeRequest? ActiveRequest { get; private set; }

    /// <summary>
    /// Gets the request associated with <see cref="LastResult"/>, including its HTTPS certificate-validation choice.
    /// This snapshot never follows subsequent draft edits.
    /// </summary>
    public ConnectivityProbeRequest? CompletedRequest { get; private set; }

    /// <summary>Gets the service's effective timeout upper bound, including its minimum of 250 milliseconds.</summary>
    public int MaximumTimeoutMilliseconds => Math.Max(250, _moduleOption.MaximumProbeTimeoutMilliseconds);

    /// <summary>
    /// Gets or sets the target host name or IP address.
    /// </summary>
    public string Host { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the target port. The protocol default is used when this value is left empty for HTTP or HTTPS probes.
    /// </summary>
    public int? Port { get; set; }

    /// <summary>
    /// Gets or sets the selected probe kind.
    /// </summary>
    public ConnectivityProbeKind ProbeKind { get; private set; } = ConnectivityProbeKind.Ping;

    /// <summary>
    /// Gets or sets the HTTP request path used by HTTP or HTTPS probes.
    /// </summary>
    public string Path { get; set; } = moduleOptions.Value.DefaultHttpRequestPath;

    /// <summary>
    /// Gets or sets the timeout applied to the next probe, in milliseconds.
    /// </summary>
    public int TimeoutMilliseconds { get; set; } = moduleOptions.Value.DefaultProbeTimeoutMilliseconds;

    /// <summary>
    /// Gets or sets whether HTTPS certificate validation should be bypassed.
    /// </summary>
    public bool AllowInvalidCertificate { get; set; }

    /// <summary>
    /// Gets whether the panel is currently running a probe.
    /// </summary>
    public bool IsBusy { get; private set; }

    /// <summary>
    /// Gets the latest validation or execution error reported by the facade.
    /// </summary>
    public string? ErrorMessage { get; private set; }

    /// <summary>
    /// Gets the latest completed probe result.
    /// </summary>
    public ConnectivityProbeResult? LastResult { get; private set; }

    /// <summary>
    /// Gets whether the current probe kind requires a target port.
    /// </summary>
    public bool RequiresPort => ProbeKind != ConnectivityProbeKind.Ping;

    /// <summary>
    /// Gets whether the current probe kind uses an HTTP request path.
    /// </summary>
    public bool RequiresHttpPath => ProbeKind is ConnectivityProbeKind.Http or ConnectivityProbeKind.Https;

    /// <summary>
    /// Gets whether the HTTPS certificate toggle should be visible.
    /// </summary>
    public bool ShowsCertificateToggle => ProbeKind == ConnectivityProbeKind.Https;

    /// <summary>
    /// Updates the selected probe kind and applies sensible default values for protocol-specific fields.
    /// </summary>
    /// <param name="probeKind">The new probe kind.</param>
    public void SetProbeKind(ConnectivityProbeKind probeKind)
    {
        ProbeKind = probeKind;

        if (probeKind == ConnectivityProbeKind.Ping)
        {
            Port = null;
            AllowInvalidCertificate = false;
            return;
        }

        switch (probeKind)
        {
            case ConnectivityProbeKind.Http when Port is null:
                Port = 80;
                break;
            case ConnectivityProbeKind.Https when Port is null:
                Port = 443;
                break;
        }

        if (!RequiresHttpPath)
        {
            if (probeKind != ConnectivityProbeKind.Https)
            {
                AllowInvalidCertificate = false;
            }

            return;
        }

        Path = string.IsNullOrWhiteSpace(Path) ? _moduleOption.DefaultHttpRequestPath : Path.Trim();
        if (probeKind != ConnectivityProbeKind.Https)
        {
            AllowInvalidCertificate = false;
        }
    }

    /// <summary>
    /// Runs the current probe request through the facade.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token that aborts the probe.</param>
    /// <returns>The real facade result, accepted only while this request remains current.</returns>
    /// <exception cref="InvalidOperationException">Another request is already active.</exception>
    /// <exception cref="OperationCanceledException">The caller canceled or Reset invalidated this request.</exception>
    public async Task<Result<ConnectivityProbeResult>> RunAsync(CancellationToken cancellationToken = default)
    {
        if (IsBusy)
            throw new InvalidOperationException("A connectivity request is already active.");
        cancellationToken.ThrowIfCancellationRequested();

        using var operationCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var revision = ++_revision;
        var request = new ConnectivityProbeRequest
        {
            Host = Host,
            Port = Port,
            ProbeKind = ProbeKind,
            Path = Path,
            TimeoutMilliseconds = TimeoutMilliseconds,
            AllowInvalidCertificate = ProbeKind == ConnectivityProbeKind.Https && AllowInvalidCertificate
        };
        _activeCancellation = operationCancellation;
        ActiveRequest = request;
        IsBusy = true;
        ErrorMessage = null;

        try
        {
            await NotifyChangedAsync();
            operationCancellation.Token.ThrowIfCancellationRequested();
            var result = await connectivityProbeFacade.ProbeAsync(request, operationCancellation.Token);

            // Network services can return evidence, and the facade can return an envelope, after cancellation.
            operationCancellation.Token.ThrowIfCancellationRequested();
            if (revision != _revision)
                throw new OperationCanceledException(operationCancellation.Token);

            var error = result.WithoutData();
            var probeResult = result.Data;
            if (result.IsFailure())
            {
                ErrorMessage = error.Message;
                LastResult = null;
                CompletedRequest = null;
            }
            else
            {
                LastResult = probeResult;
                CompletedRequest = request;
            }

            return result;
        }
        finally
        {
            if (ReferenceEquals(_activeCancellation, operationCancellation))
            {
                _activeCancellation = null;
                ActiveRequest = null;
                IsBusy = false;
                await NotifyChangedAsync();
            }
        }
    }

    /// <summary>
    /// Restores configured draft defaults and clears completed evidence.
    /// Invalidates and cancels an active request; admission remains busy until that request drains.
    /// </summary>
    public void Reset()
    {
        _revision++;
        _activeCancellation?.Cancel();
        Host = string.Empty;
        Port = null;
        ProbeKind = ConnectivityProbeKind.Ping;
        Path = _moduleOption.DefaultHttpRequestPath;
        TimeoutMilliseconds = _moduleOption.DefaultProbeTimeoutMilliseconds;
        AllowInvalidCertificate = false;
        ErrorMessage = null;
        LastResult = null;
        CompletedRequest = null;
    }

    private async Task NotifyChangedAsync()
    {
        if (Changed is not { } changed)
            return;
        foreach (var subscriber in changed.GetInvocationList().Cast<Func<Task>>())
            await subscriber();
    }
}
